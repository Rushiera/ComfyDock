# -*- coding: utf-8 -*-
"""RVC 变声主链——hubert 特征 → 检索索引混合 → F0 → 生成器 → 波形。

移植自 RVC 的 `infer/modules/vc/pipeline.py`，去掉 webui 耦合（gradio / 全局 env / 模型缓存管理），
保留同一套数值口径：F0 四种取法、index 检索、protect 保护、rms 混合、重采样。
"""
from __future__ import annotations

import os
from functools import lru_cache

import numpy as np
import torch
import torch.nn.functional as F
from scipy import signal

__all__ = ["RvcPipeline", "load_audio"]

_F0_MIN = 50.0
_F0_MAX = 1100.0
_F0_MEL_MIN = 1127 * np.log(1 + _F0_MIN / 700)
_F0_MEL_MAX = 1127 * np.log(1 + _F0_MAX / 700)

# 48 Hz 高通——RVC 原链的固定前处理
_BH, _AH = signal.butter(N=5, Wn=48, btype="high", fs=16000)

_AUDIO_INPUT_CACHE: dict[str, np.ndarray] = {}


def load_audio(path: str, sr: int = 16000) -> np.ndarray:
    """读音频为单声道 float32 波形——优先 soundfile，mp3 / m4a 等回落到 librosa。"""
    import soundfile as sf

    try:
        data, rate = sf.read(path, dtype="float32", always_2d=True)
        data = data.mean(axis=1)
    except Exception:
        import librosa

        data, rate = librosa.load(path, sr=None, mono=True)
        data = np.asarray(data, dtype=np.float32)

    if rate != sr:
        import librosa

        data = librosa.resample(np.asarray(data, dtype=np.float32), orig_sr=rate, target_sr=sr)
    return np.ascontiguousarray(data, dtype=np.float32)


@lru_cache(maxsize=8)
def _harvest_f0(key: str, fs: int, f0max: float, f0min: float, frame_period: float) -> np.ndarray:
    import pyworld

    audio = _AUDIO_INPUT_CACHE[key]
    f0, t = pyworld.harvest(audio, fs=fs, f0_ceil=f0max, f0_floor=f0min, frame_period=frame_period)
    return pyworld.stonemask(audio, f0, t, fs)


def change_rms(data1: np.ndarray, sr1: int, data2: np.ndarray, sr2: int, rate: float) -> np.ndarray:
    """把输出波形的半秒 RMS 曲线按 rate 混回输入——RVC 原口径。"""
    import librosa

    rms1 = librosa.feature.rms(y=data1, frame_length=sr1 // 2 * 2, hop_length=sr1 // 2)
    rms2 = librosa.feature.rms(y=data2, frame_length=sr2 // 2 * 2, hop_length=sr2 // 2)
    rms1 = torch.from_numpy(rms1)
    rms1 = F.interpolate(rms1.unsqueeze(0), size=data2.shape[0], mode="linear").squeeze()
    rms2 = torch.from_numpy(rms2)
    rms2 = F.interpolate(rms2.unsqueeze(0), size=data2.shape[0], mode="linear").squeeze()
    rms2 = torch.max(rms2, torch.zeros_like(rms2) + 1e-6)
    data2 = data2 * (torch.pow(rms1, torch.tensor(1 - rate)) * torch.pow(rms2, torch.tensor(rate - 1))).numpy()
    return data2


class RvcPipeline:
    """一条变声链的数值参数（对齐 RVC 的 Config 默认值）。"""

    def __init__(self, tgt_sr: int, device: torch.device | str = "cpu", half: bool = False,
                 rmvpe_root: str = ""):
        self.sr = 16000
        self.window = 160
        self.x_pad = 3
        self.x_query = 10
        self.x_center = 60
        self.x_max = 65
        self.t_pad = self.sr * self.x_pad
        self.t_pad_tgt = tgt_sr * self.x_pad
        self.t_pad2 = self.t_pad * 2
        self.t_query = self.sr * self.x_query
        self.t_center = self.sr * self.x_center
        self.t_max = self.sr * self.x_max
        self.tgt_sr = tgt_sr
        self.device = device
        self.is_half = half
        self.rmvpe_root = rmvpe_root
        self._rmvpe = None

    # ---- F0 -------------------------------------------------------------

    def get_f0(self, input_audio_path: str, x: np.ndarray, p_len: int, f0_up_key: float,
               f0_method: str, filter_radius: int) -> tuple[np.ndarray, np.ndarray]:
        time_step = self.window / self.sr * 1000
        if f0_method == "pm":
            import parselmouth

            f0 = (
                parselmouth.Sound(x, self.sr)
                .to_pitch_ac(time_step=time_step / 1000, voicing_threshold=0.6,
                             pitch_floor=_F0_MIN, pitch_ceiling=_F0_MAX)
                .selected_array["frequency"]
            )
            pad_size = (p_len - len(f0) + 1) // 2
            if pad_size > 0 or p_len - len(f0) - pad_size > 0:
                f0 = np.pad(f0, [[pad_size, p_len - len(f0) - pad_size]], mode="constant")
        elif f0_method == "harvest":
            _AUDIO_INPUT_CACHE[input_audio_path] = x.astype(np.double)
            f0 = _harvest_f0(input_audio_path, self.sr, _F0_MAX, _F0_MIN, 10)
            if filter_radius > 2:
                f0 = signal.medfilt(f0, 3)
        elif f0_method == "crepe":
            import torchcrepe

            audio = torch.tensor(np.copy(x))[None].float()
            f0, pd = torchcrepe.predict(audio, self.sr, self.window, _F0_MIN, _F0_MAX, "full",
                                        batch_size=512, device=self.device, return_periodicity=True)
            pd = torchcrepe.filter.median(pd, 3)
            f0 = torchcrepe.filter.mean(f0, 3)
            f0[pd < 0.1] = 0
            f0 = f0[0].cpu().numpy()
        elif f0_method == "rmvpe":
            if self._rmvpe is None:
                from .rmvpe import RMVPE

                self._rmvpe = RMVPE(os.path.join(self.rmvpe_root, "rmvpe.pt"),
                                    is_half=self.is_half, device=self.device)
            f0 = self._rmvpe.infer_from_audio(x, thred=0.03)
        else:
            raise ValueError(f"未知 F0 取法：{f0_method}")

        f0 = f0 * pow(2, f0_up_key / 12)
        tf0 = self.sr // self.window
        f0bak = f0.copy()
        f0_mel = 1127 * np.log(1 + f0 / 700)
        f0_mel[f0_mel > 0] = (f0_mel[f0_mel > 0] - _F0_MEL_MIN) * 254 / (_F0_MEL_MAX - _F0_MEL_MIN) + 1
        f0_mel[f0_mel <= 1] = 1
        f0_mel[f0_mel > 255] = 255
        f0_coarse = np.rint(f0_mel).astype(np.int32)
        del tf0
        return f0_coarse, f0bak

    # ---- 单段变声 -------------------------------------------------------

    def vc(self, hubert, net_g, sid, audio0: np.ndarray, pitch, pitchf, index, big_npy,
           index_rate: float, version: str, protect: float) -> np.ndarray:
        feats = torch.from_numpy(audio0)
        feats = feats.half() if self.is_half else feats.float()
        if feats.dim() == 2:
            feats = feats.mean(-1)
        feats = feats.view(1, -1).to(self.device)
        padding_mask = torch.BoolTensor(feats.shape).to(self.device).fill_(False)

        with torch.no_grad():
            out = hubert(feats, output_layer=9 if version == "v1" else 12)
            feats = hubert.final_proj(out) if version == "v1" else out
        if protect < 0.5 and pitch is not None and pitchf is not None:
            feats0 = feats.clone()
        if index is not None and big_npy is not None and index_rate != 0:
            npy = feats[0].detach().cpu().numpy()
            if self.is_half:
                npy = npy.astype("float32")
            score, ix = index.search(npy, k=8)
            weight = np.square(1 / score)
            weight /= weight.sum(axis=1, keepdims=True)
            npy = np.sum(big_npy[ix] * np.expand_dims(weight, axis=2), axis=1)
            if self.is_half:
                npy = npy.astype("float16")
            feats = torch.from_numpy(npy).unsqueeze(0).to(self.device) * index_rate \
                + (1 - index_rate) * feats

        feats = F.interpolate(feats.permute(0, 2, 1), scale_factor=2).permute(0, 2, 1)
        if protect < 0.5 and pitch is not None and pitchf is not None:
            feats0 = F.interpolate(feats0.permute(0, 2, 1), scale_factor=2).permute(0, 2, 1)
        p_len = audio0.shape[0] // self.window
        if feats.shape[1] < p_len:
            p_len = feats.shape[1]
            if pitch is not None and pitchf is not None:
                pitch = pitch[:, :p_len]
                pitchf = pitchf[:, :p_len]

        if protect < 0.5 and pitch is not None and pitchf is not None:
            pitchff = pitchf.clone()
            pitchff[pitchf > 0] = 1
            pitchff[pitchf < 1] = protect
            pitchff = pitchff.unsqueeze(-1)
            feats = feats * pitchff + feats0 * (1 - pitchff)
            feats = feats.to(feats0.dtype)
        p_len = torch.tensor([p_len], device=self.device).long()
        with torch.no_grad():
            hasp = pitch is not None and pitchf is not None
            arg = (feats, p_len, pitch, pitchf, sid) if hasp else (feats, p_len, sid)
            audio1 = (net_g.infer(*arg)[0][0, 0]).data.cpu().float().numpy()
        return audio1

    # ---- 整段变声 -------------------------------------------------------

    def convert(self, hubert, net_g, sid: int, audio: np.ndarray, input_audio_path: str,
                f0_up_key: float, f0_method: str, index, big_npy, index_rate: float,
                if_f0: int, filter_radius: int, resample_sr: int, rms_mix_rate: float,
                version: str, protect: float) -> np.ndarray:
        """输入 16 kHz 单声道 float32，返回 int16 波形（RVC 原口径）。"""
        audio = signal.filtfilt(_BH, _AH, audio)
        audio_pad = np.pad(audio, (self.window // 2, self.window // 2), mode="reflect")
        opt_ts = []
        if audio_pad.shape[0] > self.t_max:
            audio_sum = np.zeros_like(audio)
            for i in range(self.window):
                audio_sum += np.abs(audio_pad[i: i - self.window])
            for t in range(self.t_center, audio.shape[0], self.t_center):
                opt_ts.append(
                    t - self.t_query
                    + np.where(audio_sum[t - self.t_query: t + self.t_query]
                               == audio_sum[t - self.t_query: t + self.t_query].min())[0][0]
                )

        s = 0
        audio_opt = []
        t = None
        audio_pad = np.pad(audio, (self.t_pad, self.t_pad), mode="reflect")
        p_len = audio_pad.shape[0] // self.window
        sid_t = torch.tensor(sid, device=self.device).unsqueeze(0).long()
        pitch, pitchf = None, None
        if if_f0 == 1:
            pitch, pitchf = self.get_f0(input_audio_path, audio_pad, p_len, f0_up_key,
                                        f0_method, filter_radius)
            pitch = pitch[:p_len]
            pitchf = pitchf[:p_len]
            pitchf = pitchf.astype(np.float32)
            pitch = torch.tensor(pitch, device=self.device).unsqueeze(0).long()
            pitchf = torch.tensor(pitchf, device=self.device).unsqueeze(0).float()

        for t in opt_ts:
            t = t // self.window * self.window
            if if_f0 == 1:
                audio_opt.append(self.vc(
                    hubert, net_g, sid_t, audio_pad[s: t + self.t_pad2 + self.window],
                    pitch[:, s // self.window: (t + self.t_pad2) // self.window],
                    pitchf[:, s // self.window: (t + self.t_pad2) // self.window],
                    index, big_npy, index_rate, version, protect)[self.t_pad_tgt: -self.t_pad_tgt])
            else:
                audio_opt.append(self.vc(
                    hubert, net_g, sid_t, audio_pad[s: t + self.t_pad2 + self.window],
                    None, None, index, big_npy, index_rate, version,
                    protect)[self.t_pad_tgt: -self.t_pad_tgt])
            s = t

        if if_f0 == 1:
            audio_opt.append(self.vc(
                hubert, net_g, sid_t, audio_pad[t:],
                pitch[:, t // self.window:] if t is not None else pitch,
                pitchf[:, t // self.window:] if t is not None else pitchf,
                index, big_npy, index_rate, version, protect)[self.t_pad_tgt: -self.t_pad_tgt])
        else:
            audio_opt.append(self.vc(
                hubert, net_g, sid_t, audio_pad[t:], None, None, index, big_npy,
                index_rate, version, protect)[self.t_pad_tgt: -self.t_pad_tgt])

        audio_opt = np.concatenate(audio_opt)
        if rms_mix_rate != 1:
            audio_opt = change_rms(audio, 16000, audio_opt, self.tgt_sr, rms_mix_rate)
        if resample_sr >= 16000 and self.tgt_sr != resample_sr:
            import librosa

            audio_opt = librosa.resample(audio_opt, orig_sr=self.tgt_sr, target_sr=resample_sr)
        audio_max = np.abs(audio_opt).max() / 0.99
        max_int16 = 32768
        if audio_max > 1:
            max_int16 /= audio_max
        audio_opt = (audio_opt * max_int16).astype(np.int16)
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
        return audio_opt
