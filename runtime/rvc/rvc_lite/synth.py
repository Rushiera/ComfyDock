# -*- coding: utf-8 -*-
"""音色模型（.pth）的加载与变声入口——对齐 RVC 的 `VC.get_vc` / `vc_single` 口径。"""
from __future__ import annotations

import os

import numpy as np
import torch

from .infer_pack.models import (
    SynthesizerTrnMs256NSFsid,
    SynthesizerTrnMs256NSFsid_nono,
    SynthesizerTrnMs768NSFsid,
    SynthesizerTrnMs768NSFsid_nono,
)
from .pipeline import RvcPipeline, load_audio

__all__ = ["RvcModel", "load_model", "find_index"]

_CLASSES = {
    ("v1", 1): SynthesizerTrnMs256NSFsid,
    ("v1", 0): SynthesizerTrnMs256NSFsid_nono,
    ("v2", 1): SynthesizerTrnMs768NSFsid,
    ("v2", 0): SynthesizerTrnMs768NSFsid_nono,
}


def find_index(model_path: str, index_dir: str = "") -> str:
    """按模型名找同名 `.index` 检索文件——找不到返回空串（对齐 RVC：名字对不上就不挂索引）。"""
    stem = os.path.splitext(os.path.basename(model_path))[0].lower()
    roots = [index_dir, os.path.join(os.path.dirname(model_path), "..", "indices"),
             os.path.dirname(model_path)]

    for root in roots:
        if not root or not os.path.isdir(root):
            continue

        for name in sorted(os.listdir(root)):
            if not name.lower().endswith(".index"):
                continue

            key = os.path.splitext(name)[0].lower()
            if stem in key or key in stem:
                return os.path.join(root, name)
    return ""


def _load_index(path: str):
    """读 faiss 索引——读不了就返回 (None, None)（变声照跑，只是不挂检索）。

    走 `np.fromfile` + `deserialize_index`：faiss 的 C++ 文件读取器打不开中文路径。
    """
    if not path or not os.path.exists(path):
        return None, None

    try:
        import faiss

        raw = np.fromfile(path, dtype="uint8")
        index = faiss.deserialize_index(raw)
        big_npy = index.reconstruct_n(0, index.ntotal)
        return index, big_npy
    except Exception:
        return None, None


class RvcModel:
    """一个音色模型 + 它那条变声链。"""

    def __init__(self, model_path: str, device: torch.device | str = "cpu", half: bool = False,
                 index_dir: str = ""):
        self.model_path = model_path
        self.device = device
        self.half = half

        cpt = torch.load(model_path, map_location="cpu", weights_only=True)
        config = list(cpt["config"])
        self.tgt_sr = config[-1]
        config[-3] = cpt["weight"]["emb_g.weight"].shape[0]
        self.n_spk = config[-3]
        self.if_f0 = cpt.get("f0", 1)
        self.version = cpt.get("version", "v1")

        net_g = _CLASSES[(self.version, self.if_f0)](*config, is_half=half)
        if hasattr(net_g, "enc_q"):
            del net_g.enc_q
        missing, unexpected = net_g.load_state_dict(cpt["weight"], strict=False)
        if missing or unexpected:
            raise RuntimeError(f"音色模型权重不匹配：missing={missing} unexpected={unexpected}")
        net_g.eval().to(device)
        net_g = net_g.half() if half else net_g.float()
        self.net_g = net_g
        self.config = config
        self.index_path = find_index(model_path, index_dir)
        self.index, self.big_npy = _load_index(self.index_path)

    def pipeline(self, rmvpe_root: str) -> RvcPipeline:
        """按本模型的采样率建一条变声链。"""
        return RvcPipeline(self.tgt_sr, device=self.device, half=self.half, rmvpe_root=rmvpe_root)

    def convert(self, hubert, audio_path: str, pipeline: RvcPipeline, sid: int = 0,
                f0_up_key: float = 0.0, f0_method: str = "rmvpe", index_rate: float = 0.75,
                filter_radius: int = 3, resample_sr: int = 0, rms_mix_rate: float = 0.25,
                protect: float = 0.33) -> np.ndarray:
        """把一个音频文件变声成这个音色——返回 int16 波形（采样率见 `pipeline.tgt_sr`）。"""
        audio = load_audio(audio_path, 16000)
        audio_max = np.abs(audio).max() / 0.95
        if audio_max > 1:
            audio /= audio_max

        return pipeline.convert(
            hubert, self.net_g, sid, audio, audio_path,
            f0_up_key, f0_method, self.index, self.big_npy, index_rate,
            self.if_f0, filter_radius, resample_sr, rms_mix_rate, self.version, protect)


def load_model(model_path: str, device: torch.device | str = "cpu", half: bool = False,
               index_dir: str = "") -> RvcModel:
    """加载一个音色模型。"""
    return RvcModel(model_path, device=device, half=half, index_dir=index_dir)
