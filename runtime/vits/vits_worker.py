"""VITS（MoeGoe 血统）推理 worker——ComfyDock 自持，供语音合成 / 语音转换两条链复用。

形态：一次性常驻子进程，stdin/stdout 行协议（每行一个 JSON 请求 / 响应）。
不启 ComfyUI 服务、不开端口；解释器 = 本机 ComfyUI 的 python_embeded。

模型根（默认）``<ComfyUI>/models/VITS``，一层子目录分组：
    models/VITS/346/346lab_v3.pth + 346lab_v3.json

请求 / 响应：
    {"op":"models"}                                  -> {"ok":true,"models":[{"group","name","pth","json","ok"}]}
    {"op":"load","model":"346lab_v3"}                -> {"ok":true,"model":..,"sampling_rate":..,"n_speakers":..,"symbols":..,"cleaners":[..],"speakers":[..]}
    {"op":"clean","text":"..."}                      -> {"ok":true,"cleaned":"..."}
    {"op":"tts","text":..,"sid":..,"length_scale":..,"noise_scale":..,"noise_scale_w":..,"cleaned":false,"out":"..."}
                                                    -> {"ok":true,"out":"...","seconds":..,"tokens":..}
    {"op":"vc","audio":"...","src":..,"dst":..,"out":"..."}
                                                    -> {"ok":true,"out":"...","seconds":..}
    {"op":"tts_batch","text":..,"items":[{"sid":..,"out":".."}],"length_scale":..,"noise_scale":..,"noise_scale_w":..,"cleaned":false}
                                                    -> 逐条 {"stage":"item","sid":..,"out":..,"seconds":..,"ms":..}
                                                       收尾 {"stage":"done","total":..,"ok_count":..,"fail_count":..}
    {"op":"ping"} / {"op":"stop"}

原始实现：MoeGoe（MIT，https://github.com/cjangcjengh/MoeGoe）——模型定义与日语文本前端按原样搬入
``vits_lite``，本文件只做模型生命周期 + 行协议 + 落盘。
"""

import argparse
import json
import os
import sys
import time
import traceback

import numpy as np
import torch
from scipy.io.wavfile import write as wav_write
from torch import LongTensor, no_grad

HERE = os.path.dirname(os.path.abspath(__file__))
LITE = os.path.join(HERE, "vits_lite")
if LITE not in sys.path:
    sys.path.insert(0, LITE)

import commons  # noqa: E402
import utils  # noqa: E402
from mel_processing import spectrogram_torch  # noqa: E402
from models import SynthesizerTrn  # noqa: E402
from text import _clean_text, text_to_sequence  # noqa: E402


def log(msg):
    """诊断走 stderr——stdout 只留协议行。"""
    sys.stderr.write(str(msg) + "\n")
    sys.stderr.flush()


def load_state_dict_strict(model, path):
    """MoeGoe utils.load_checkpoint 的同语义版本，补 weights_only=False（torch 2.6+ 默认值会拒载 dict）。"""
    ckpt = torch.load(path, map_location="cpu", weights_only=False)
    saved = ckpt["model"] if isinstance(ckpt, dict) and "model" in ckpt else ckpt
    iteration = ckpt.get("iteration") if isinstance(ckpt, dict) else None
    own = model.state_dict()
    merged = {}
    missing = []
    for k, v in own.items():
        if k in saved:
            merged[k] = saved[k]
        else:
            missing.append(k)
            merged[k] = v
    model.load_state_dict(merged)
    return iteration, len(missing)


class Runner:
    def __init__(self, models_root, device):
        self.models_root = models_root
        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        self.device = torch.device(device)
        self.cur = None

    # ---- 模型 ----

    def list_models(self):
        out = []
        if not os.path.isdir(self.models_root):
            return out
        for group in sorted(os.listdir(self.models_root)):
            gdir = os.path.join(self.models_root, group)
            if not os.path.isdir(gdir):
                continue
            for fn in sorted(os.listdir(gdir)):
                if not fn.lower().endswith(".pth"):
                    continue
                pth = os.path.join(gdir, fn)
                cfg = os.path.join(gdir, fn[:-4] + ".json")
                out.append({
                    "group": group,
                    "name": fn[:-4],
                    "pth": pth,
                    "json": cfg if os.path.isfile(cfg) else "",
                    "ok": os.path.isfile(cfg),
                })
        return out

    def _resolve(self, name):
        hits = [m for m in self.list_models() if m["name"] == name]
        if not hits:
            raise RuntimeError("模型未找到：%s（根 %s）" % (name, self.models_root))
        if not hits[0]["ok"]:
            raise RuntimeError("模型缺 config：%s" % hits[0]["pth"])
        if self.cur and self.cur["name"] == name:
            return self.cur
        m = hits[0]
        hps = utils.get_hparams_from_file(m["json"])
        n_speakers = hps.data.n_speakers if "n_speakers" in hps.data.keys() else 0
        n_symbols = len(hps.symbols) if "symbols" in hps.keys() else 0
        speakers = list(hps.speakers) if "speakers" in hps.keys() else ["0"]
        emotion = hps.data.emotion_embedding if "emotion_embedding" in hps.data.keys() else False
        if emotion:
            raise RuntimeError("该模型带 emotion_embedding，本移植不支持：%s" % name)
        if n_symbols == 0:
            raise RuntimeError("该模型无 symbols（hubert 系 VC 模型，不在支持面）：%s" % name)
        net = SynthesizerTrn(
            n_symbols,
            hps.data.filter_length // 2 + 1,
            hps.train.segment_size // hps.data.hop_length,
            n_speakers=n_speakers,
            **hps.model).to(self.device)
        net.eval()
        iteration, missing = load_state_dict_strict(net, m["pth"])
        log("loaded %s (iter %s, missing %d keys) -> %s" % (name, iteration, missing, self.device))
        self.cur = {
            "name": name,
            "net": net,
            "hps": hps,
            "speakers": speakers,
            "n_speakers": n_speakers,
            "n_symbols": n_symbols,
            "sr": int(hps.data.sampling_rate),
            "cleaners": list(hps.data.text_cleaners),
        }
        return self.cur

    def _require(self):
        if not self.cur:
            raise RuntimeError("尚未加载模型（先发 load）")
        return self.cur

    # ---- 文本 ----

    def clean(self, text):
        cur = self._require()
        return _clean_text(text, cur["cleaners"])

    def _to_sequence(self, text, cleaned):
        cur = self._require()
        if cleaned:
            seq = text_to_sequence(text, cur["hps"].symbols, [])
        else:
            seq = text_to_sequence(text, cur["hps"].symbols, cur["cleaners"])
        if getattr(cur["hps"].data, "add_blank", False):
            seq = commons.intersperse(seq, 0)
        if not seq:
            raise RuntimeError("清理后无有效符号（文本与 symbols 表不匹配，或文本为空）")
        return seq

    # ---- 推理 ----

    def tts(self, text, sid, length_scale, noise_scale, noise_scale_w, cleaned, out):
        cur = self._require()
        seq = self._to_sequence(text, cleaned)
        if sid < 0 or (cur["n_speakers"] > 0 and sid >= cur["n_speakers"]):
            raise RuntimeError("说话人索引越界：%s（n_speakers=%s）" % (sid, cur["n_speakers"]))
        net = cur["net"]
        with no_grad():
            x = LongTensor(seq).unsqueeze(0).to(self.device)
            x_len = LongTensor([x.size(1)]).to(self.device)
            sid_t = LongTensor([sid]).to(self.device)
            audio = net.infer(x, x_len, sid=sid_t, noise_scale=noise_scale,
                              noise_scale_w=noise_scale_w, length_scale=length_scale)[0][0, 0]
            audio = audio.data.float().cpu().numpy()
        seconds = write_wav(out, audio, cur["sr"])
        return {"out": out, "seconds": round(seconds, 3), "tokens": len(seq),
                "sampling_rate": cur["sr"], "sid": sid, "model": cur["name"]}

    def tts_batch(self, text, items, length_scale, noise_scale, noise_scale_w, cleaned, emit):
        """同一条文本批量跑多个说话人——文本前端只算一次、模型只加载一次。

        每条跑完立刻回一行（stage=item），全部跑完回 stage=done；单条失败不牵连其余。
        """
        cur = self._require()
        seq = self._to_sequence(text, cleaned)
        net = cur["net"]
        sr = cur["sr"]
        t_all = time.time()
        ok_count = 0
        fail_count = 0
        for i, it in enumerate(items):
            sid = int(it.get("sid", 0))
            out = it.get("out") or os.path.join(HERE, "out.wav")
            t0 = time.time()
            try:
                if sid < 0 or (cur["n_speakers"] > 0 and sid >= cur["n_speakers"]):
                    raise RuntimeError("说话人索引越界：%s（n_speakers=%s）" % (sid, cur["n_speakers"]))
                with no_grad():
                    x = LongTensor(seq).unsqueeze(0).to(self.device)
                    x_len = LongTensor([x.size(1)]).to(self.device)
                    sid_t = LongTensor([sid]).to(self.device)
                    audio = net.infer(x, x_len, sid=sid_t, noise_scale=noise_scale,
                                      noise_scale_w=noise_scale_w, length_scale=length_scale)[0][0, 0]
                    audio = audio.data.float().cpu().numpy()
                seconds = write_wav(out, audio, sr)
                ok_count += 1
                emit({"ok": True, "stage": "item", "index": i, "sid": sid, "out": out,
                      "seconds": round(seconds, 3), "tokens": len(seq),
                      "ms": int((time.time() - t0) * 1000)})
            except Exception as e:
                fail_count += 1
                log("批量第 %d 条失败（sid %s）：%s" % (i, sid, e))
                emit({"ok": True, "stage": "item", "index": i, "sid": sid, "out": out,
                      "error": str(e), "ms": int((time.time() - t0) * 1000)})
        emit({"ok": True, "stage": "done", "total": len(items), "ok_count": ok_count,
              "fail_count": fail_count, "ms": int((time.time() - t_all) * 1000)})

    def vc_pcm(self, y_np, src, dst):
        """对一段 PCM（已是模型采样率）做说话人转换，返回 numpy float32。

        离线 vc 与 RVC 实时链的桥接（同一进程内 import 本模块）共用这一处数值口径——
        桥接只换输入输出形态，不另写一套推理。
        """
        cur = self._require()
        if cur["n_speakers"] <= 0:
            raise RuntimeError("该模型单说话人，无法做说话人转换：%s" % cur["name"])
        for label, sid in (("src", src), ("dst", dst)):
            if sid < 0 or sid >= cur["n_speakers"]:
                raise RuntimeError("%s 说话人索引越界：%s（n_speakers=%s）" % (label, sid, cur["n_speakers"]))
        net = cur["net"]
        hps = cur["hps"]
        with no_grad():
            y = torch.FloatTensor(np.asarray(y_np, dtype="float32")).unsqueeze(0).to(self.device)
            spec = spectrogram_torch(y, hps.data.filter_length, hps.data.sampling_rate,
                                     hps.data.hop_length, hps.data.win_length, center=False)
            spec_lengths = LongTensor([spec.size(-1)]).to(self.device)
            audio = net.voice_conversion(spec, spec_lengths,
                                         sid_src=LongTensor([src]).to(self.device),
                                         sid_tgt=LongTensor([dst]).to(self.device))[0][0, 0]
            return audio.data.float().cpu().numpy()

    def vc(self, audio_path, src, dst, out):
        cur = self._require()
        if not os.path.isfile(audio_path):
            raise RuntimeError("输入音频不存在：%s" % audio_path)
        import librosa
        y_np, _ = librosa.load(audio_path, sr=cur["sr"], mono=True)
        audio = self.vc_pcm(y_np, src, dst)
        seconds = write_wav(out, audio, cur["sr"])
        return {"out": out, "seconds": round(seconds, 3), "src": src, "dst": dst,
                "sampling_rate": cur["sr"], "model": cur["name"]}


def write_wav(path, audio, sr):
    parent = os.path.dirname(os.path.abspath(path))
    if parent and not os.path.isdir(parent):
        os.makedirs(parent, exist_ok=True)
    clipped = np.clip(audio.astype(np.float32), -1.0, 1.0)
    wav_write(path, int(sr), (clipped * 32767.0).astype(np.int16))
    return float(len(clipped)) / float(sr)


def handle(runner, req):
    op = (req.get("op") or "").strip().lower()
    if op == "ping":
        return {"ok": True, "device": str(runner.device), "cuda": torch.cuda.is_available(),
                "model": runner.cur["name"] if runner.cur else ""}
    if op == "models":
        return {"ok": True, "root": runner.models_root, "models": runner.list_models()}
    if op == "load":
        cur = runner._resolve(req.get("model") or "")
        return {"ok": True, "model": cur["name"], "sampling_rate": cur["sr"],
                "n_speakers": cur["n_speakers"], "symbols": cur["n_symbols"],
                "cleaners": cur["cleaners"], "speakers": cur["speakers"],
                "device": str(runner.device)}
    if op == "clean":
        runner._require()
        return {"ok": True, "cleaned": runner.clean(req.get("text") or "")}
    if op == "tts":
        res = runner.tts(
            req.get("text") or "",
            int(req.get("sid", 0)),
            float(req.get("length_scale", 1.0)),
            float(req.get("noise_scale", 0.667)),
            float(req.get("noise_scale_w", 0.8)),
            bool(req.get("cleaned", False)),
            req.get("out") or os.path.join(HERE, "out.wav"))
        res["ok"] = True
        return res
    if op == "vc":
        res = runner.vc(req.get("audio") or "", int(req.get("src", 0)), int(req.get("dst", 1)),
                        req.get("out") or os.path.join(HERE, "out.wav"))
        res["ok"] = True
        return res
    raise RuntimeError("未知 op：%s" % op)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--models-root", default="")
    ap.add_argument("--device", default="auto")
    ap.add_argument("--selftest", default="", help="模型名：跑一遍 TTS（并可选 VC）后退出")
    ap.add_argument("--text", default="こんにちは、世界。")
    ap.add_argument("--sid", type=int, default=0)
    ap.add_argument("--out-dir", default="")
    ap.add_argument("--vc-audio", default="")
    ap.add_argument("--vc-src", type=int, default=0)
    ap.add_argument("--vc-dst", type=int, default=1)
    args = ap.parse_args()

    root = args.models_root or os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(HERE))), "models", "VITS")
    runner = Runner(root, args.device)

    if args.selftest:
        out_dir = args.out_dir or os.path.join(HERE, "out")
        os.makedirs(out_dir, exist_ok=True)
        t0 = time.time()
        cur = runner._resolve(args.selftest)
        print(json.dumps({"ok": True, "loaded": cur["name"], "sampling_rate": cur["sr"],
                          "n_speakers": cur["n_speakers"], "device": str(runner.device),
                          "load_seconds": round(time.time() - t0, 2)}, ensure_ascii=True))
        out = os.path.join(out_dir, "selftest_tts.wav")
        res = runner.tts(args.text, args.sid, 1.0, 0.667, 0.8, False, out)
        res["ok"] = True
        res["text"] = args.text
        print(json.dumps(res, ensure_ascii=True))
        if args.vc_audio:
            out2 = os.path.join(out_dir, "selftest_vc.wav")
            res2 = runner.vc(args.vc_audio, args.vc_src, args.vc_dst, out2)
            res2["ok"] = True
            print(json.dumps(res2, ensure_ascii=True))
        return 0

    print(json.dumps({"ok": True, "ready": True, "root": root, "device": str(runner.device)},
                     ensure_ascii=True), flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except Exception as e:
            print(json.dumps({"ok": False, "error": "请求不是合法 JSON：%s" % e}, ensure_ascii=True), flush=True)
            continue
        if (req.get("op") or "").strip().lower() == "stop":
            print(json.dumps({"ok": True, "stopped": True}, ensure_ascii=True), flush=True)
            break
        if (req.get("op") or "").strip().lower() == "tts_batch":
            # 批量：逐条回执（stage=item）+ 收尾回执（stage=done）——不走单回执通道
            try:
                runner.tts_batch(
                    req.get("text") or "",
                    req.get("items") or [],
                    float(req.get("length_scale", 1.0)),
                    float(req.get("noise_scale", 0.667)),
                    float(req.get("noise_scale_w", 0.8)),
                    bool(req.get("cleaned", False)),
                    lambda obj: print(json.dumps(obj, ensure_ascii=True), flush=True))
            except Exception as e:
                resp = {"ok": False, "error": str(e), "trace": traceback.format_exc(limit=3)}
                log(resp["trace"])
                print(json.dumps(resp, ensure_ascii=True), flush=True)
            continue
        try:
            resp = handle(runner, req)
        except Exception as e:
            resp = {"ok": False, "error": str(e), "trace": traceback.format_exc(limit=3)}
            log(resp["trace"])
        print(json.dumps(resp, ensure_ascii=True), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
