namespace ComfyDock.Rvc;

/// <summary>
/// RVC 实时变声 worker 的 Python 源码——编译进程序，运行时解到 <c>data\rvc\rvc_realtime_worker.py</c> 再执行。
/// <para>与离线 worker（<see cref="RvcWorkerScript"/>）分立：离线是「一请求一响应」，实时是「流式状态机」——
/// 进程常驻、状态机常开，InputStream 持续收块、OutputStream 持续放块，参数走 update 热更、不重开流。</para>
/// <para>协议：stdin 每行一个 JSON 请求，stdout 每行一个 JSON 事件（fd1 让给 stderr）。
/// 请求 <c>devices</c> / <c>start</c> / <c>stop</c> / <c>update</c> / <c>ping</c> / <c>quit</c>；
/// 事件 <c>ready</c> / <c>devices</c> / <c>started</c> / <c>stopped</c> / <c>level</c> / <c>latency</c> / <c>pong</c> / <c>error</c>。</para>
/// <para>自检：<c>--selftest</c> 不开设备，用 3 秒 440 Hz 正弦走同一条处理链，落 wav 并打印延迟四项与输出时长。</para>
/// </summary>
internal static class RvcRealtimeWorkerScript
{
    /// <summary>worker 脚本正文。</summary>
    public const string Source = """
# -*- coding: utf-8 -*-
# RVC 实时变声 worker——流式状态机：InputStream 持续收块、OutputStream 持续放块。
# 协议：stdin 每行一个 JSON 请求，stdout 每行一个 JSON 事件（stdout 被独占，库里的 print 全走 stderr）。
#   请求：{"cmd":"devices"}
#         {"cmd":"start","model":"雾雾40k.pth",
#          "device":{"hostapi":-1,"input":-1,"output":-1,"exclusive":false,"samplerate":48000},
#          "params":{"f0Method":"rmvpe","f0UpKey":0,"formant":0,"indexRate":0.75,"filterRadius":3,
#                    "rmsMixRate":0.25,"protect":0.33,"threhold":-60,"inputNoiseReduce":false,
#                    "outputNoiseReduce":false,"usePv":false,"bypass":false,"sid":0,
#                    "blockTime":0.25,"crossfadeTime":0.05,"extraTime":2.5}}
#         {"cmd":"update","params":{"f0UpKey":12}}   # 只吃热更白名单：f0UpKey / formant / indexRate / threhold / 降噪 / bypass
#         可选 "bridge":{"enabled":true,"blocks":2,"model":"346lab_S1","src":2,"dst":5,"modelsRoot":"...","repo":"...","device":"cuda"}
#         —— start / update 都吃 bridge：RVC 输出攒 blocks 块过一轮 VITS 语音转换再送设备（设置取自 VITS 页签）
#         {"cmd":"stop"} / {"cmd":"ping"} / {"cmd":"quit"}
#   事件：{"ev":"ready"|"devices"|"started"|"stopped"|"level"|"latency"|"pong"|"error", ...}
import os
import sys
import json
import time
import traceback

# [段1] 协议通道独占 fd 1——torch / librosa / sounddevice 会往 stdout 打日志，不隔离就污染行协议
_PROTO = os.fdopen(os.dup(1), "w", encoding="utf-8", buffering=1)
os.dup2(2, 1)
sys.stdout = sys.stderr

try:
    sys.stdin.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def emit(obj):
    try:
        _PROTO.write(json.dumps(obj, ensure_ascii=False) + "\n")
    except UnicodeEncodeError:
        _PROTO.write(json.dumps(obj, ensure_ascii=True) + "\n")
    _PROTO.flush()


def fail(stage, message):
    emit({"ev": "error", "stage": stage, "message": message,
          "traceback": traceback.format_exc()[-4000:]})
    sys.exit(5)


def arg(name, default=""):
    argv = sys.argv[1:]
    if name in argv:
        idx = argv.index(name)
        if idx + 1 < len(argv):
            return argv[idx + 1]
    return default


def flag(name):
    return name in sys.argv[1:]


REPO = arg("--repo")
MODELS = arg("--models")
DEVICE = arg("--device", "cuda")
HALF = arg("--half", "1") not in ("0", "false", "False", "")
SELFTEST = flag("--selftest")
DEVICES_ONLY = flag("--devices")

if REPO == "" or MODELS == "":
    emit({"ev": "error", "stage": "args",
          "message": "用法: rvc_realtime_worker.py --repo <rvc_lite 上级目录> --models <ComfyUI\\models\\RVC> [--device cuda] [--half 1] [--selftest]"})
    sys.exit(2)

sys.path.insert(0, REPO)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# [段2] 导入推理链（rvc_lite 只 import，不改）
try:
    import numpy as np
    import soundfile as sf
    import torch
    import torch.nn.functional as F
    from rvc_lite import load_hubert, load_model
    from torchgate import TorchGate
except Exception:
    fail("import", "导入 rvc_lite / torchgate 失败（检查 --repo 与脚本所在目录）")

WEIGHTS_DIR = os.path.join(MODELS, "weights")
INDICES_DIR = os.path.join(MODELS, "indices")
HUBERT_PATH = os.path.join(MODELS, "hubert", "hubert_base.pt")
RMVPE_DIR = os.path.join(MODELS, "rmvpe")

if DEVICE.startswith("cuda") and not torch.cuda.is_available():
    DEVICE = "cpu"

USE_HALF = HALF and DEVICE != "cpu"

# [段3] 加载 HuBERT（只做一次）
_t0 = time.time()
try:
    HUBERT = load_hubert(HUBERT_PATH, DEVICE, half=USE_HALF)
except Exception:
    fail("load", "加载 HuBERT 失败（检查 %s）" % HUBERT_PATH)

_cuda = bool(torch.cuda.is_available())
emit({
    "ev": "ready",
    "torch": str(torch.__version__),
    "cuda": _cuda,
    "device": torch.cuda.get_device_name(0) if _cuda else "cpu",
    "half": bool(USE_HALF),
    "models": MODELS,
    "seconds": round(time.time() - _t0, 1),
})

# [段4] 设备枚举（含 VB-CABLE 虚拟声卡端点标记）
def _device_row(idx, info):
    name = str(info.get("name", ""))
    upper = name.upper()
    return {
        "index": idx,
        "name": name,
        "hostapi": int(info.get("hostapi", -1)),
        "maxInput": int(info.get("max_input_channels", 0)),
        "maxOutput": int(info.get("max_output_channels", 0)),
        "defaultSr": int(info.get("default_samplerate", 0) or 0),
        "cable": "CABLE INPUT" in upper or "CABLE OUTPUT" in upper,
    }


def list_devices():
    import sounddevice as sd
    hosts = []
    try:
        for i, h in enumerate(sd.query_hostapis()):
            hosts.append({"index": i, "name": str(h.get("name", ""))})
    except Exception:
        hosts = []
    ins, outs = [], []
    try:
        for i, info in enumerate(sd.query_devices()):
            row = _device_row(i, info)
            if row["maxInput"] > 0:
                ins.append(row)
            if row["maxOutput"] > 0:
                outs.append(row)
    except Exception as exc:
        emit({"ev": "error", "stage": "devices", "message": str(exc)})
    return {"hostapis": hosts, "inputs": ins, "outputs": outs}


# 当前挂着的音色模型（换模型才重载）
_CURRENT = {"path": "", "model": None, "pipeline": None}


def resolve_model(name):
    if name == "":
        raise ValueError("没选音色模型")
    if os.path.isabs(name) and os.path.exists(name):
        return name
    candidate = os.path.join(WEIGHTS_DIR, name)
    if os.path.exists(candidate):
        return candidate
    raise ValueError("音色模型不在位：%s" % candidate)


def ensure_model(path):
    if _CURRENT["path"] == path and _CURRENT["model"] is not None:
        return _CURRENT["model"], _CURRENT["pipeline"]
    model = load_model(path, DEVICE, half=USE_HALF, index_dir=INDICES_DIR)
    pipeline = model.pipeline(RMVPE_DIR)
    _CURRENT["path"] = path
    _CURRENT["model"] = model
    _CURRENT["pipeline"] = pipeline
    return model, pipeline


def list_models():
    out = []
    if not os.path.isdir(WEIGHTS_DIR):
        return out
    for name in sorted(os.listdir(WEIGHTS_DIR)):
        if not name.lower().endswith(".pth"):
            continue
        out.append({"name": name, "bytes": os.path.getsize(os.path.join(WEIGHTS_DIR, name))})
    return out


# [段5] 实时处理链——与离线链共用 rvc_lite 的数值口径（F0 / 检索混合 / protect / rms 混合不另写一套）
import threading

import librosa
from torchaudio.transforms import Resample

# 参数默认值（面板未给时用；对齐原版实时 GUI 的出厂档）
DEFAULTS = {
    "f0Method": "rmvpe",
    "f0UpKey": 0.0,
    "formant": 0.0,
    "indexRate": 0.75,
    "filterRadius": 3,
    "rmsMixRate": 0.25,
    "protect": 0.33,
    "threhold": -60.0,
    "inputNoiseReduce": False,
    "outputNoiseReduce": False,
    "usePv": False,
    "bypass": False,
    "sid": 0,
    "blockTime": 0.25,
    "crossfadeTime": 0.05,
    "extraTime": 2.5,
}

# 热更白名单——改了不重开流（其余改动要 stop + start）
HOT_KEYS = ("f0UpKey", "formant", "indexRate", "threhold",
            "inputNoiseReduce", "outputNoiseReduce", "bypass")


class RealtimeChain:
    # 一条实时变声链——按块进、按块出；SOLA 对齐 + crossfade 拼接。
    # 数值口径全部走 rvc_lite（HuBERT / F0 四取法 / 检索混合 / protect / rms 混合），
    # 分块与拼接照 RVC 原版实时 GUI（gui_v1.py）的口径：block / crossfade / extra 三段缓冲 + SOLA 搜索。

    def __init__(self, model, pipeline, params, samplerate):
        self.model = model
        self.pipeline = pipeline
        self.device = torch.device(DEVICE)
        self.half = USE_HALF
        self.lock = threading.Lock()
        self.seq = 0
        self.infer_ms = []
        self.params = dict(DEFAULTS)
        self.params.update(params or {})
        self.samplerate = int(samplerate)
        self.tgt_sr = int(pipeline.tgt_sr)
        self._layout()

    # [段5.1] 缓冲布局——设备采样率下按 10 ms 一格的 zc 对齐
    def _layout(self):
        sr = self.samplerate
        p = self.params
        self.zc = max(sr // 100, 1)
        self.block_frame = max(int(np.round(p["blockTime"] * sr / self.zc)) * self.zc, self.zc)
        self.block_frame_16k = 160 * self.block_frame // self.zc
        self.crossfade_frame = int(np.round(p["crossfadeTime"] * sr / self.zc)) * self.zc
        self.sola_buffer_frame = min(self.crossfade_frame, 4 * self.zc)
        self.sola_search_frame = self.zc
        self.extra_frame = int(np.round(p["extraTime"] * sr / self.zc)) * self.zc

        total = self.extra_frame + self.crossfade_frame + self.sola_search_frame + self.block_frame
        self.input_wav = torch.zeros(total, device=self.device, dtype=torch.float32)
        self.input_wav_denoise = self.input_wav.clone()
        self.input_wav_res = torch.zeros(160 * total // self.zc, device=self.device, dtype=torch.float32)
        self.rms_buffer = np.zeros(4 * self.zc, dtype="float32")
        self.sola_buffer = torch.zeros(self.sola_buffer_frame, device=self.device, dtype=torch.float32)
        self.nr_buffer = self.sola_buffer.clone()
        self.output_buffer = self.input_wav.clone()

        self.skip_head = self.extra_frame // self.zc
        self.return_length = (self.block_frame + self.sola_buffer_frame + self.sola_search_frame) // self.zc
        self.fade_in_window = (
            torch.sin(0.5 * np.pi * torch.linspace(0.0, 1.0, steps=self.sola_buffer_frame,
                                                   device=self.device, dtype=torch.float32)) ** 2
        )
        self.fade_out_window = 1 - self.fade_in_window
        self.cache_pitch = torch.zeros(1024, device=self.device, dtype=torch.long)
        self.cache_pitchf = torch.zeros(1024, device=self.device, dtype=torch.float32)
        self.resample_kernel = {}

        self.resampler = Resample(orig_freq=sr, new_freq=16000, dtype=torch.float32).to(self.device)
        self.resampler2 = None
        if self.tgt_sr != sr:
            self.resampler2 = Resample(orig_freq=self.tgt_sr, new_freq=sr, dtype=torch.float32).to(self.device)
        self.tg = TorchGate(sr=sr, n_fft=4 * self.zc, prop_decrease=0.9).to(self.device)

    # [段5.2] 延迟分解——块 + crossfade + 额外推理 + 设备（设备项由调用方补）
    def latency(self):
        ms = 1000.0 / float(self.samplerate)
        block = self.block_frame * ms
        crossfade = self.crossfade_frame * ms
        extra = self.extra_frame * ms
        return {
            "blockMs": round(block, 1),
            "crossfadeMs": round(crossfade, 1),
            "extraMs": round(extra, 1),
            "algoMs": round(block + crossfade + extra, 1),
        }

    def info(self):
        return {
            "samplerate": self.samplerate,
            "modelSr": self.tgt_sr,
            "blockFrame": self.block_frame,
            "blockFrame16k": self.block_frame_16k,
            "crossfadeFrame": self.crossfade_frame,
            "solaBufferFrame": self.sola_buffer_frame,
            "extraFrame": self.extra_frame,
            "skipHead": self.skip_head,
            "returnLength": self.return_length,
            "version": self.model.version,
            "index": os.path.basename(self.model.index_path or ""),
            "ifF0": self.model.if_f0,
        }

    # [段5.3] 热更——白名单内直接改，其余拒绝
    def update(self, params):
        applied, rejected = {}, []
        for key, value in (params or {}).items():
            if key not in DEFAULTS:
                rejected.append(key)
                continue
            if key not in HOT_KEYS:
                rejected.append(key)
                continue
            with self.lock:
                self.params[key] = value
            applied[key] = value
        return applied, rejected

    # [段5.4] 单块推理——照 RVC 原版 rtrvc.RVC.infer 口径（分块 + 音高缓存滚动）
    def infer(self, p):
        t0 = time.perf_counter()
        with torch.no_grad():
            feats = self.input_wav_res.half() if self.half else self.input_wav_res.float()
            feats = feats.view(1, -1)
            out = HUBERT(feats, output_layer=9 if self.model.version == "v1" else 12)
            feats = HUBERT.final_proj(out) if self.model.version == "v1" else out
            feats = torch.cat((feats, feats[:, -1:, :]), 1)

            if self.model.index is not None and self.model.big_npy is not None and p["indexRate"] != 0:
                npy = feats[0][self.skip_head // 2:].detach().cpu().numpy().astype("float32")
                score, ix = self.model.index.search(npy, k=8)
                if (ix >= 0).all():
                    weight = np.square(1 / score)
                    weight /= weight.sum(axis=1, keepdims=True)
                    npy = np.sum(self.model.big_npy[ix] * np.expand_dims(weight, axis=2), axis=1)
                    if self.half:
                        npy = npy.astype("float16")
                    feats[0][self.skip_head // 2:] = (
                        torch.from_numpy(npy).unsqueeze(0).to(self.device) * p["indexRate"]
                        + (1 - p["indexRate"]) * feats[0][self.skip_head // 2:]
                    )

            p_len = self.input_wav_res.shape[0] // 160
            factor = pow(2, p["formant"] / 12)
            return_length2 = int(np.ceil(self.return_length * factor))
            if self.model.if_f0 == 1:
                f0_extractor_frame = self.block_frame_16k + 800
                if p["f0Method"] == "rmvpe":
                    f0_extractor_frame = 5120 * ((f0_extractor_frame - 1) // 5120 + 1) - 160
                self.seq += 1
                key = "rt:%d" % self.seq
                seg = self.input_wav_res[-f0_extractor_frame:].detach().cpu().numpy()
                pitch, pitchf = self.pipeline.get_f0(
                    key, seg, p_len, p["f0UpKey"] - p["formant"], p["f0Method"], p["filterRadius"])
                if p["f0Method"] == "harvest":
                    import rvc_lite.pipeline as _pl
                    _pl._AUDIO_INPUT_CACHE.clear()
                shift = self.block_frame_16k // 160
                self.cache_pitch[:-shift] = self.cache_pitch[shift:].clone()
                self.cache_pitchf[:-shift] = self.cache_pitchf[shift:].clone()
                self.cache_pitch[4 - pitch.shape[0]:] = torch.from_numpy(
                    np.asarray(pitch, dtype="int64")[3:-1]).to(self.device)
                self.cache_pitchf[4 - pitchf.shape[0]:] = torch.from_numpy(
                    np.asarray(pitchf, dtype="float32")[3:-1]).to(self.device)
                cache_pitch = self.cache_pitch[None, -p_len:]
                cache_pitchf = self.cache_pitchf[None, -p_len:] * return_length2 / self.return_length

            feats = F.interpolate(feats.permute(0, 2, 1), scale_factor=2).permute(0, 2, 1)
            feats = feats[:, :p_len, :]
            p_len_t = torch.LongTensor([p_len]).to(self.device)
            sid_t = torch.LongTensor([int(p["sid"])]).to(self.device)
            skip_t = torch.LongTensor([self.skip_head]).to(self.device)
            len_t = torch.LongTensor([self.return_length]).to(self.device)
            len2_t = torch.LongTensor([return_length2]).to(self.device)

            if self.model.if_f0 == 1:
                audio, _, _ = self.model.net_g.infer(
                    feats, p_len_t, cache_pitch, cache_pitchf, sid_t, skip_t, len_t, len2_t)
            else:
                audio, _, _ = self.model.net_g.infer(feats, p_len_t, sid_t, skip_t, len_t, len2_t)
        audio = audio.squeeze(1).float()
        upp_res = int(np.floor(factor * self.tgt_sr // 100))
        if upp_res != self.tgt_sr // 100:
            if upp_res not in self.resample_kernel:
                self.resample_kernel[upp_res] = Resample(
                    orig_freq=upp_res, new_freq=self.tgt_sr // 100, dtype=torch.float32).to(self.device)
            audio = self.resample_kernel[upp_res](audio[:, : self.return_length * upp_res])
        self.infer_ms.append(round((time.perf_counter() - t0) * 1000, 1))
        if len(self.infer_ms) > 40:
            self.infer_ms = self.infer_ms[-40:]
        return audio.squeeze(0)

    # [段5.5] 单块处理——进块出块（干声旁路时输入直通）
    def process(self, indata):
        with self.lock:
            p = dict(self.params)

        x = np.asarray(indata, dtype="float32").reshape(-1)
        if x.shape[0] < self.block_frame:
            x = np.pad(x, (self.block_frame - x.shape[0], 0))
        elif x.shape[0] > self.block_frame:
            x = x[-self.block_frame:]

        if p["threhold"] > -60:
            buf = np.append(self.rms_buffer, x)
            rms = librosa.feature.rms(y=buf, frame_length=4 * self.zc, hop_length=self.zc)[:, 2:]
            self.rms_buffer[:] = buf[-4 * self.zc:]
            seg = buf[2 * self.zc - self.zc // 2:]
            gate = librosa.amplitude_to_db(rms, ref=1.0)[0] < p["threhold"]
            for i in range(gate.shape[0]):
                if gate[i]:
                    seg[i * self.zc:(i + 1) * self.zc] = 0
            x = seg[self.zc // 2:]
            if x.shape[0] < self.block_frame:
                x = np.pad(x, (self.block_frame - x.shape[0], 0))
            elif x.shape[0] > self.block_frame:
                x = x[:self.block_frame]

        # 干声旁路——输入块直通输出（不过推理），块边界生效；缓冲尾巴留给切回时 crossfade
        if p["bypass"]:
            if self.sola_buffer_frame > 0:
                self.sola_buffer[:] = torch.from_numpy(x[-self.sola_buffer_frame:]).to(self.device)
            return x

        self.input_wav[:-self.block_frame] = self.input_wav[self.block_frame:].clone()
        self.input_wav[-self.block_frame:] = torch.from_numpy(x).to(self.device)
        self.input_wav_res[:-self.block_frame_16k] = self.input_wav_res[self.block_frame_16k:].clone()

        if p["inputNoiseReduce"]:
            self.input_wav_denoise[:-self.block_frame] = self.input_wav_denoise[self.block_frame:].clone()
            src = self.input_wav[-self.sola_buffer_frame - self.block_frame:]
            src = self.tg(src.unsqueeze(0), self.input_wav.unsqueeze(0)).squeeze(0)
            src[: self.sola_buffer_frame] *= self.fade_in_window
            src[: self.sola_buffer_frame] += self.nr_buffer * self.fade_out_window
            self.input_wav_denoise[-self.block_frame:] = src[: self.block_frame]
            self.nr_buffer[:] = src[self.block_frame:]
            self.input_wav_res[-self.block_frame_16k - 160:] = self.resampler(
                self.input_wav_denoise[-self.block_frame - 2 * self.zc:])[160:]
        else:
            self.input_wav_res[-160 * (x.shape[0] // self.zc + 1):] = self.resampler(
                self.input_wav[-x.shape[0] - 2 * self.zc:])[160:]

        infer_wav = self.infer(p)
        if self.resampler2 is not None:
            infer_wav = self.resampler2(infer_wav)

        if p["outputNoiseReduce"]:
            self.output_buffer[:-self.block_frame] = self.output_buffer[self.block_frame:].clone()
            self.output_buffer[-self.block_frame:] = infer_wav[-self.block_frame:]
            infer_wav = self.tg(infer_wav.unsqueeze(0), self.output_buffer.unsqueeze(0)).squeeze(0)

        if p["rmsMixRate"] < 1:
            if p["inputNoiseReduce"]:
                ref = self.input_wav_denoise[self.extra_frame:]
            else:
                ref = self.input_wav[self.extra_frame:]
            rms1 = librosa.feature.rms(y=ref[: infer_wav.shape[0]].detach().cpu().numpy(),
                                       frame_length=4 * self.zc, hop_length=self.zc)
            rms1 = torch.from_numpy(rms1).to(self.device)
            rms1 = F.interpolate(rms1.unsqueeze(0), size=infer_wav.shape[0] + 1,
                                 mode="linear", align_corners=True)[0, 0, :-1]
            rms2 = librosa.feature.rms(y=infer_wav.detach().cpu().numpy(),
                                       frame_length=4 * self.zc, hop_length=self.zc)
            rms2 = torch.from_numpy(rms2).to(self.device)
            rms2 = F.interpolate(rms2.unsqueeze(0), size=infer_wav.shape[0] + 1,
                                 mode="linear", align_corners=True)[0, 0, :-1]
            rms2 = torch.max(rms2, torch.zeros_like(rms2) + 1e-3)
            infer_wav = infer_wav * torch.pow(rms1 / rms2, torch.tensor(1 - p["rmsMixRate"]))

        # SOLA 对齐 + crossfade 拼接（照原版）
        conv_input = infer_wav[None, None, : self.sola_buffer_frame + self.sola_search_frame]
        cor_nom = F.conv1d(conv_input, self.sola_buffer[None, None, :])
        cor_den = torch.sqrt(F.conv1d(
            conv_input ** 2,
            torch.ones(1, 1, self.sola_buffer_frame, device=self.device)) + 1e-8)
        sola_offset = int(torch.argmax(cor_nom[0, 0] / cor_den[0, 0]))
        infer_wav = infer_wav[sola_offset:]
        if not p["usePv"]:
            infer_wav[: self.sola_buffer_frame] *= self.fade_in_window
            infer_wav[: self.sola_buffer_frame] += self.sola_buffer * self.fade_out_window
        else:
            infer_wav[: self.sola_buffer_frame] = _phase_vocoder(
                self.sola_buffer, infer_wav[: self.sola_buffer_frame],
                self.fade_out_window, self.fade_in_window)
        self.sola_buffer[:] = infer_wav[self.block_frame: self.block_frame + self.sola_buffer_frame]

        out = infer_wav[: self.block_frame]
        if out.shape[0] < self.block_frame:
            out = torch.nn.functional.pad(out, (0, self.block_frame - out.shape[0]))
        return out.detach().cpu().numpy()


def _phase_vocoder(a, b, fade_out, fade_in):
    # 相位声码器交叉（原版 phase_vocoder——usePv 打开时用）。
    window = torch.sqrt(fade_out * fade_in)
    fa = torch.fft.rfft(a * window)
    fb = torch.fft.rfft(b * window)
    absab = torch.abs(fa) + torch.abs(fb)
    n = a.shape[0]
    if n % 2 == 0:
        absab[1:-1] *= 2
    else:
        absab[1:] *= 2
    phia = torch.angle(fa)
    phib = torch.angle(fb)
    deltaphase = phib - phia
    deltaphase = deltaphase - 2 * np.pi * torch.floor(deltaphase / 2 / np.pi + 0.5)
    w = 2 * np.pi * torch.arange(n // 2 + 1).to(a) + deltaphase
    t = torch.arange(n).unsqueeze(-1).to(a) / n
    result = (
        a * (fade_out ** 2)
        + b * (fade_in ** 2)
        + torch.sum(absab * torch.cos(w * t + phia), -1) * window / n
    )
    return result


# [段6b] 桥接 VITS 语音转换——RVC 输出攒 N 块过一轮 VITS VC 再送设备
# 形态：回调只做「入队 + 取块」（不阻塞音频线程），推理在后台线程串行跑；
# 攒够 blocks 块 → 降采样到 VITS 模型采样率 → 说话人转换 → 升回设备采样率 → 播放队列。
# 设置面单一真相源 = VITS 页签（model / src / dst 由 C# 侧随 start / update 下发）。
class VitsBridge:
    def __init__(self, cfg, samplerate, block_frame):
        self.samplerate = int(samplerate)
        self.block_frame = int(block_frame)
        self.lock = threading.Lock()
        self.cond = threading.Condition(self.lock)
        self.jobs = []
        self.pending = []
        self.ready = np.zeros(0, dtype="float32")
        self.blocks = max(1, min(16, int(cfg.get("blocks", 1) or 1)))
        self.model = str(cfg.get("model", "") or "")
        self.src = int(cfg.get("src", 0) or 0)
        self.dst = int(cfg.get("dst", 1) or 1)
        self.repo = str(cfg.get("repo", "") or "")
        self.models_root = str(cfg.get("modelsRoot", "") or "")
        self.device = str(cfg.get("device", "") or DEVICE)
        self.enabled = bool(cfg.get("enabled", True))
        self.runner = None
        self.model_sr = 0
        self.state = "loading"
        self.message = ""
        self.infer_ms = []
        self.underruns = 0
        self.dropped = 0
        self.running = True
        self.thread = threading.Thread(target=self._loop, daemon=True)
        self.thread.start()

    # [段6b.1] 模型——首次转换时加载（加载期间播放队列空着，出声为静音）
    def _ensure(self):
        if self.runner is None:
            if self.repo and self.repo not in sys.path:
                sys.path.insert(0, self.repo)
            try:
                import vits_worker as _vw
            except Exception:
                raise RuntimeError("导入 vits_worker 失败（检查 VITS 运行目录：%s）" % (self.repo or "（空）"))
            device = self.device
            if device.startswith("cuda") and not torch.cuda.is_available():
                device = "cpu"
            self.runner = _vw.Runner(self.models_root, device)
        if not self.model:
            raise RuntimeError("桥接没拿到 VITS 模型——去「本地生成 → VITS」选一个模型并保存")
        if self.model_sr <= 0 or self.runner.cur is None or self.runner.cur["name"] != self.model:
            self.state = "loading"
            cur = self.runner._resolve(self.model)
            self.model_sr = int(cur["sr"])
        self.state = "ready"
        return self.runner

    # [段6b.2] 后台线程——串行取段推理；队列上限 4 段（跟不上实时就丢最旧，不无限涨延迟）
    def _loop(self):
        # 线程一起来就把模型加载掉——首段不用等加载（加载期间不占音频线程）
        try:
            self._ensure()
        except Exception as exc:
            self.state = "error"
            self.message = str(exc)
        while True:
            with self.cond:
                while self.running and not self.jobs:
                    self.cond.wait(0.2)
                if not self.running:
                    return
                seg = self.jobs.pop(0)
            try:
                runner = self._ensure()
                t0 = time.perf_counter()
                y = np.asarray(seg, dtype="float32")
                # 段长按采样率比例换算——VITS 输出长度可能与输入差几帧，不校正会逐段累积漂移
                need = int(round(y.shape[0] * float(self.model_sr) / float(self.samplerate))) if self.samplerate else y.shape[0]
                if self.samplerate != self.model_sr:
                    y = librosa.resample(y, orig_sr=self.samplerate, target_sr=self.model_sr)
                if y.shape[0] < 4096:
                    y = np.pad(y, (0, 4096 - y.shape[0]))
                out = np.asarray(runner.vc_pcm(y, self.src, self.dst), dtype="float32")
                if out.shape[0] > need:
                    out = out[:need]
                elif out.shape[0] < need:
                    out = np.pad(out, (0, need - out.shape[0]))
                if self.samplerate != self.model_sr:
                    out = librosa.resample(out, orig_sr=self.model_sr, target_sr=self.samplerate)
                # 回到设备采样率后再对齐到原段长——差几个样本也会逐段累积成漂移
                seg_len = int(seg.shape[0]) if hasattr(seg, "shape") else len(seg)
                if out.shape[0] > seg_len:
                    out = out[:seg_len]
                elif out.shape[0] < seg_len:
                    out = np.pad(out, (0, seg_len - out.shape[0]))
                self.infer_ms.append(round((time.perf_counter() - t0) * 1000, 1))
                if len(self.infer_ms) > 20:
                    self.infer_ms = self.infer_ms[-20:]
                with self.cond:
                    # 播放队列上限 = 攒块数 + 2 块——推理比实时快时队列会越攒越长（延迟只增不减），
                    # 超出就丢最旧的（一次性丢帧，换来延迟有界）
                    cap = (self.blocks + 2) * self.block_frame
                    self.ready = np.concatenate((self.ready, np.asarray(out, dtype="float32")))
                    if self.ready.shape[0] > cap:
                        self.dropped += 1
                        self.ready = self.ready[-cap:]
                    self.message = ""
            except Exception as exc:
                self.state = "error"
                self.message = str(exc)
                sys.stderr.write("vits bridge: %s\n" % traceback.format_exc(limit=3))
                sys.stderr.flush()

    # [段6b.3] 音频回调侧——攒块 + 取一块播放（不够就静音，计欠载）
    def push(self, block):
        x = np.asarray(block, dtype="float32").reshape(-1)
        n = x.shape[0]
        with self.cond:
            if self.enabled:
                self.pending.append(x.copy())
                if len(self.pending) >= self.blocks:
                    seg = np.concatenate(self.pending)
                    self.pending = []
                    if len(self.jobs) >= 4:
                        self.jobs.pop(0)
                        self.dropped += 1
                    self.jobs.append(seg)
                    self.cond.notify()
            if self.ready.shape[0] >= n:
                out = self.ready[:n].copy()
                self.ready = self.ready[n:]
                return out
            self.underruns += 1
            return np.zeros(n, dtype="float32")

    # [段6b.4] 热更——攒块数 / 说话人 / 模型（换模型下一段重载）即时生效，不断流
    def update(self, cfg):
        with self.cond:
            if "blocks" in cfg:
                self.blocks = max(1, min(16, int(cfg["blocks"] or 1)))
            if "enabled" in cfg:
                self.enabled = bool(cfg["enabled"])
                if not self.enabled:
                    self.pending = []
                    self.jobs = []
                    self.ready = np.zeros(0, dtype="float32")
            if "src" in cfg:
                self.src = int(cfg["src"] or 0)
            if "dst" in cfg:
                self.dst = int(cfg["dst"] or 0)
            if "model" in cfg:
                name = str(cfg["model"] or "")
                if name != self.model:
                    self.model = name
                    self.model_sr = 0
                    self.state = "loading"
            if "repo" in cfg:
                self.repo = str(cfg["repo"] or "")
            if "modelsRoot" in cfg:
                self.models_root = str(cfg["modelsRoot"] or "")
            if "device" in cfg:
                self.device = str(cfg["device"] or DEVICE)
            self.cond.notify()

    # [段6b.5] 状态——随 latency 事件上报（面板显示桥接队列与欠载）
    def stats(self):
        with self.cond:
            queue_ms = round(self.ready.shape[0] * 1000.0 / float(self.samplerate), 1) if self.samplerate else 0.0
            pending = len(self.pending)
            under = self.underruns
            dropped = self.dropped
            state = self.state
            message = self.message
        ms = self.infer_ms
        return {
            "enabled": self.enabled,
            "blocks": self.blocks,
            "state": state,
            "message": message,
            "model": self.model,
            "src": self.src,
            "dst": self.dst,
            "modelSr": self.model_sr,
            "queueMs": queue_ms,
            "pendingBlocks": pending,
            "underruns": under,
            "dropped": dropped,
            "inferMs": round(sum(ms) / len(ms), 1) if ms else 0.0,
        }

    def stop(self):
        self.running = False
        with self.cond:
            self.cond.notify_all()


# [段7] 设备流管理——sounddevice 全双工（回调在音频线程，协议循环在主线程）
def _db(peak):
    return round(20.0 * float(np.log10(max(float(peak), 1e-6))), 1)


def _first_model():
    if not os.path.isdir(WEIGHTS_DIR):
        return ""
    for name in sorted(os.listdir(WEIGHTS_DIR)):
        if name.lower().endswith(".pth"):
            return name
    return ""


class RealtimeHost:
    # 实时链宿主——设备流开停 / 参数热更 / level 与 latency 节流上报。

    def __init__(self):
        self.chain = None
        self.stream = None
        self.stream_latency = (0.0, 0.0)
        self.model_path = ""
        self.blocks = 0
        self.peak_in = 0.0
        self.peak_out = 0.0
        self.status = "stopped"
        self.out_channels = 1
        self.bridge = None

    def _callback(self, indata, outdata, frames, times, status):
        try:
            if self.chain is None:
                outdata.fill(0)
                return
            mono = indata[:, 0]
            out = self.chain.process(mono)
            bridge = self.bridge
            if bridge is not None and bridge.enabled:
                out = bridge.push(out)
            if outdata.shape[1] == 1:
                outdata[:, 0] = out
            else:
                outdata[:] = out[:, None]
            self.blocks += 1
            self.peak_in = max(self.peak_in, float(np.abs(mono).max()))
            self.peak_out = max(self.peak_out, float(np.abs(out).max()))
        except Exception:
            outdata.fill(0)

    def ticker(self):
        # 后台节流——每 0.4 秒报一次 latency 与 level（running 时才报）。
        while True:
            time.sleep(0.4)
            chain = self.chain
            if chain is None or self.status != "running":
                continue
            lat = chain.latency()
            lat["deviceMs"] = round(max(self.stream_latency) * 1000, 1)
            bridge = self.bridge
            if bridge is not None:
                info = bridge.stats()
                lat["bridge"] = info
                lat["bridgeMs"] = round(info["queueMs"] + info["pendingBlocks"] * lat["blockMs"]
                                        + info["inferMs"], 1)
                lat["totalMs"] = round(lat["algoMs"] + lat["deviceMs"] + lat["bridgeMs"], 1)
            else:
                lat["totalMs"] = round(lat["algoMs"] + lat["deviceMs"], 1)
            ms = chain.infer_ms
            lat["inferMs"] = round(sum(ms) / len(ms), 1) if ms else 0.0
            lat["inferPeakMs"] = round(max(ms), 1) if ms else 0.0
            emit({"ev": "latency", **lat})
            emit({"ev": "level", "inDb": _db(self.peak_in), "outDb": _db(self.peak_out),
                  "blocks": self.blocks})
            self.peak_in = 0.0
            self.peak_out = 0.0

    def start(self, req):
        import sounddevice as sd

        dev = req.get("device") or {}
        params = req.get("params") or {}
        model_path = resolve_model(str(req.get("model", "")).strip())
        model, pipeline = ensure_model(model_path)
        self.model_path = model_path

        # 换音色时说话人索引可能超出新模型的说话人数（单说话人模型只有 0）——
        # 越界会让 emb_g 索引报错、整条流起不来：这里按模型实际人数收敛，并在 started 回执里出声
        n_spk = max(1, int(getattr(model, "n_spk", 1) or 1))
        sid = int(params.get("sid", 0) or 0)
        if sid < 0 or sid >= n_spk:
            sid = 0
            params = dict(params)
            params["sid"] = sid

        in_idx = int(dev.get("input", -1))
        out_idx = int(dev.get("output", -1))
        exclusive = bool(dev.get("exclusive", False))
        sr = int(dev.get("samplerate", 0) or 0)
        if sr < 8000:
            sr = 48000

        self.stop(silent=True)
        chain = RealtimeChain(model, pipeline, params, sr)

        # 桥接 VITS-VC（开关与攒块数随 start 下发；关着就不建对象，回调零开销）
        bridge_cfg = req.get("bridge") or {}
        if bridge_cfg.get("enabled"):
            self.bridge = VitsBridge(bridge_cfg, sr, chain.block_frame)
        else:
            self.bridge = None

        out_ch = 1
        try:
            info = sd.query_devices(out_idx if out_idx >= 0 else None, "output")
            out_ch = max(1, min(2, int(info.get("max_output_channels", 1))))
        except Exception:
            out_ch = 1
        self.out_channels = out_ch

        extra = None
        if exclusive:
            try:
                extra = sd.WasapiSettings(exclusive=True)
            except Exception:
                extra = None
        device_arg = None
        if in_idx >= 0 or out_idx >= 0:
            device_arg = (in_idx if in_idx >= 0 else None, out_idx if out_idx >= 0 else None)

        self.chain = chain
        self.status = "starting"
        try:
            self.stream = sd.Stream(
                callback=self._callback,
                blocksize=chain.block_frame,
                samplerate=sr,
                channels=(1, out_ch),
                dtype="float32",
                device=device_arg,
                extra_settings=extra,
            )
            self.stream.start()
        except Exception:
            self.chain = None
            self.stream = None
            self.status = "stopped"
            raise

        self.status = "running"
        self.blocks = 0
        try:
            self.stream_latency = tuple(self.stream.latency)
        except Exception:
            self.stream_latency = (0.0, 0.0)
        lat = chain.latency()
        lat["deviceMs"] = round(max(self.stream_latency) * 1000, 1)
        lat["totalMs"] = round(lat["algoMs"] + lat["deviceMs"], 1)
        bridge_stats = None
        if self.bridge is not None:
            bridge_stats = self.bridge.stats()
            lat["bridge"] = bridge_stats
            lat["bridgeMs"] = round(bridge_stats["queueMs"]
                                    + bridge_stats["pendingBlocks"] * lat["blockMs"]
                                    + bridge_stats["inferMs"], 1)
            lat["totalMs"] = round(lat["totalMs"] + lat["bridgeMs"], 1)
        emit({"ev": "started", "model": os.path.basename(model_path),
              "sid": sid, "nSpk": n_spk,
              "input": in_idx, "output": out_idx, "exclusive": exclusive,
              "latency": lat, "info": chain.info(), "bridge": bridge_stats})

    def stop(self, silent=False):
        if self.stream is not None:
            try:
                self.stream.abort()
                self.stream.close()
            except Exception:
                pass
            self.stream = None
        bridge = self.bridge
        self.bridge = None
        if bridge is not None:
            bridge.stop()
        had = self.chain is not None
        self.chain = None
        self.status = "stopped"
        if had and not silent:
            emit({"ev": "stopped"})

    def update(self, req):
        params = req.get("params") or {}
        if self.chain is None:
            raise ValueError("实时链没在跑——先 start")
        applied, rejected = self.chain.update(params)
        bridge_cfg = req.get("bridge")
        if bridge_cfg is not None:
            if self.bridge is None:
                if bridge_cfg.get("enabled"):
                    self.bridge = VitsBridge(bridge_cfg, self.chain.samplerate, self.chain.block_frame)
            else:
                self.bridge.update(bridge_cfg)
                if not bridge_cfg.get("enabled"):
                    self.bridge.stop()
                    self.bridge = None
        emit({"ev": "updated", "applied": applied, "rejected": rejected,
              "hot": list(HOT_KEYS),
              "bridge": self.bridge.stats() if self.bridge is not None else None})


# [段8] 自检——不开设备，用正弦走同一条处理链，落 wav 并报延迟与输出时长
def selftest():
    model_path = resolve_model(arg("--model") or _first_model())
    model, pipeline = ensure_model(model_path)
    sr = int(arg("--samplerate", "48000"))
    seconds = float(arg("--seconds", "3"))
    tone = float(arg("--tone", "440"))
    out_path = arg("--out") or os.path.join(os.path.dirname(os.path.abspath(__file__)), "rt_selftest.wav")

    params = {}
    if flag("--bypass"):
        params["bypass"] = True
    if flag("--nr-in"):
        params["inputNoiseReduce"] = True
    if flag("--nr-out"):
        params["outputNoiseReduce"] = True
    for key, name in (("blockTime", "--block"), ("crossfadeTime", "--crossfade"),
                      ("extraTime", "--extra"), ("f0UpKey", "--key"), ("indexRate", "--index")):
        value = arg(name)
        if value != "":
            params[key] = float(value)
    if arg("--f0") != "":
        params["f0Method"] = arg("--f0")

    chain = RealtimeChain(model, pipeline, params, sr)
    n = chain.block_frame
    total = int(seconds * sr)
    t = np.arange(total, dtype="float32") / float(sr)
    wave = (0.3 * np.sin(2 * np.pi * tone * t)).astype("float32")

    t0 = time.time()
    chunks = []
    for start in range(0, total, n):
        chunks.append(chain.process(wave[start:start + n]))
    audio = np.concatenate(chunks) if chunks else np.zeros(0, dtype="float32")

    # 可选：整链再过一轮桥接（--vits-model <模型名> + --vits-repo / --vits-models）
    bridge_stats = None
    if arg("--vits-model") != "":
        cfg = {
            "enabled": True,
            "blocks": int(arg("--bridge-blocks", "1")),
            "model": arg("--vits-model"),
            "src": int(arg("--vits-src", "0")),
            "dst": int(arg("--vits-dst", "1")),
            "modelsRoot": arg("--vits-models"),
            "repo": arg("--vits-repo"),
            "device": DEVICE,
        }
        bridge = VitsBridge(cfg, sr, n)
        bridged = []
        for start in range(0, total, n):
            bridged.append(bridge.push(audio[start:start + n]))
            time.sleep(float(n) / float(sr))  # 按实时节奏喂——不留节奏会把段全丢进丢弃队列
        # 攒块 + 推理有延迟：再多喂静音把队列排空
        for _ in range(int(cfg["blocks"]) + 2):
            bridged.append(bridge.push(np.zeros(n, dtype="float32")))
            time.sleep(float(n) / float(sr))
        bridge_stats = bridge.stats()
        bridge.stop()
        audio = np.concatenate(bridged) if bridged else audio

    out_dir = os.path.dirname(out_path)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    sf.write(out_path, audio, sr, subtype="PCM_16")

    ms = chain.infer_ms
    steady = ms[1:] if len(ms) > 1 else ms
    lat = chain.latency()
    lat["deviceMs"] = 0.0
    lat["totalMs"] = lat["algoMs"]
    lat["inferMs"] = round(sum(ms) / len(ms), 1) if ms else 0.0
    lat["inferFirstMs"] = round(ms[0], 1) if ms else 0.0
    lat["inferSteadyMs"] = round(sum(steady) / len(steady), 1) if steady else 0.0
    lat["inferPeakMs"] = round(max(ms), 1) if ms else 0.0
    lat["realtimeOk"] = bool(steady) and (sum(steady) / len(steady)) < lat["blockMs"]
    emit({"ev": "selftest", "file": out_path, "model": os.path.basename(model_path),
          "inputSeconds": round(seconds, 2), "outputSeconds": round(len(audio) / float(sr), 2),
          "sampleRate": sr, "blocks": len(chunks), "latency": lat,
          "wallSeconds": round(time.time() - t0, 2), "info": chain.info(),
          "bridge": bridge_stats})


# [段9] 请求循环
if DEVICES_ONLY:
    emit({"ev": "devices", **list_devices()})
    sys.exit(0)

if SELFTEST:
    try:
        selftest()
    except Exception as exc:
        emit({"ev": "error", "stage": "selftest", "message": str(exc),
              "traceback": traceback.format_exc()[-4000:]})
        sys.exit(6)
    sys.exit(0)

HOST = RealtimeHost()
threading.Thread(target=HOST.ticker, daemon=True).start()

for _line in sys.stdin:
    _line = _line.strip()
    if _line == "":
        continue
    try:
        req = json.loads(_line)
    except Exception:
        emit({"ev": "error", "stage": "parse", "message": "请求不是合法 JSON"})
        continue

    cmd = str(req.get("cmd", ""))
    if cmd == "quit":
        break
    if cmd == "ping":
        emit({"ev": "pong"})
        continue
    if cmd == "devices":
        emit({"ev": "devices", **list_devices()})
        continue
    if cmd == "models":
        emit({"ev": "models", "models": list_models()})
        continue
    if cmd == "start":
        try:
            HOST.start(req)
        except Exception as exc:
            emit({"ev": "error", "stage": "start", "message": str(exc),
                  "traceback": traceback.format_exc()[-4000:]})
        continue
    if cmd == "stop":
        HOST.stop()
        continue
    if cmd == "update":
        try:
            HOST.update(req)
        except Exception as exc:
            emit({"ev": "error", "stage": "update", "message": str(exc),
                  "traceback": traceback.format_exc()[-4000:]})
        continue

    emit({"ev": "error", "stage": "cmd", "message": "未知指令：%s" % cmd})
""";
}
