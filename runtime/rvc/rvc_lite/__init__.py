# -*- coding: utf-8 -*-
"""RVC 推理链（自持实现）——HuBERT 特征 + F0 + 生成器 + 检索索引。

对外只暴露三件事：`RvcModel`（一个音色模型的加载与变声）、`load_hubert`、`RvcPipeline`。
模型文件读本机 ComfyUI 的 `models\RVC\`，不依赖 RVC 的 WebUI 与它的运行时。
"""
from __future__ import annotations

from .hubert import HubertLite, load_hubert
from .pipeline import RvcPipeline, load_audio
from .synth import RvcModel, load_model

__all__ = ["HubertLite", "load_hubert", "RvcPipeline", "load_audio", "RvcModel", "load_model"]
