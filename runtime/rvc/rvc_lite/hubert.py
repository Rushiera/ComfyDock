# -*- coding: utf-8 -*-
"""RVC 特征提取（HuBERT base）——纯 torch 前向，不依赖 fairseq。

权重直接读 RVC 自带的 fairseq 存档 `hubert_base.pt`（`torch.load(weights_only=False)`，
需要一个只为 unpickle 服务的 `Dictionary` 桩），按 fairseq 的结构逐层还原：
卷积特征提取（首层 GroupNorm）→ 512 维 LayerNorm → 512→768 投影 → 位置卷积 → 12 层 Transformer。

与 fairseq 原版的逐层数值一致性见 `runtime/rvc/README.md`（实测 maxdiff < 1e-5）。
"""
from __future__ import annotations

import sys
import types

import torch
import torch.nn as nn
import torch.nn.functional as F

__all__ = ["HubertLite", "load_hubert"]

_KERNELS = (10, 3, 3, 3, 3, 2, 2)
_STRIDES = (5, 2, 2, 2, 2, 2, 2)
_DIM = 512
_HIDDEN = 768
_HEADS = 12
_LAYERS = 12
_CONV_POS = 128
_CONV_POS_GROUPS = 16


class _FairseqDictionary(dict):
    """fairseq 存档里的 `fairseq.data.dictionary.Dictionary` 占位——只服务于 unpickle。"""


def _install_fairseq_stub() -> None:
    """hubert_base.pt 里 pickle 了 fairseq 的 Dictionary，缺它 torch.load 直接拒收。"""
    if "fairseq.data.dictionary" in sys.modules:
        return

    for name in ("fairseq", "fairseq.data", "fairseq.data.dictionary"):
        if name in sys.modules:
            continue

        mod = types.ModuleType(name)
        mod.Dictionary = _FairseqDictionary
        sys.modules[name] = mod


def _weight_norm(g: torch.Tensor, v: torch.Tensor) -> torch.Tensor:
    """fairseq 的位置卷积用 dim=2 的 weight_norm（g 形状 (1,1,k)）。"""
    return g * v / v.norm(dim=(0, 1), keepdim=True).clamp(min=1e-12)


class _Attention(nn.Module):
    """单头合批的多头自注意力——与 fairseq 的 q/k/v/out 四投影逐一对齐。"""

    def __init__(self, dim: int, heads: int):
        super().__init__()
        self.heads = heads
        self.head_dim = dim // heads
        self.scaling = self.head_dim ** -0.5
        self.k_proj = nn.Linear(dim, dim)
        self.v_proj = nn.Linear(dim, dim)
        self.q_proj = nn.Linear(dim, dim)
        self.out_proj = nn.Linear(dim, dim)

    def forward(self, x: torch.Tensor, mask: torch.Tensor | None = None) -> torch.Tensor:
        bsz, tgt, dim = x.shape
        src = tgt
        k = self.k_proj(x).view(bsz, src, self.heads, self.head_dim).transpose(1, 2)
        v = self.v_proj(x).view(bsz, src, self.heads, self.head_dim).transpose(1, 2)
        q = self.q_proj(x).view(bsz, tgt, self.heads, self.head_dim).transpose(1, 2)
        q = q * self.scaling
        attn = torch.matmul(q, k.transpose(-2, -1))
        if mask is not None:
            attn = attn.masked_fill(mask, 0.0)
        attn = F.softmax(attn, dim=-1)
        out = torch.matmul(attn, v)
        out = out.transpose(1, 2).reshape(bsz, tgt, dim)
        return self.out_proj(out)


class _Layer(nn.Module):
    """fairseq 的后置归一化 Transformer 层（normalize_before=False）。"""

    def __init__(self, dim: int, heads: int, ffn: int):
        super().__init__()
        self.attention = _Attention(dim, heads)
        self.layer_norm = nn.LayerNorm(dim)
        self.feed_forward_intermediate_dense = nn.Linear(dim, ffn)
        self.feed_forward_output_dense = nn.Linear(ffn, dim)
        self.final_layer_norm = nn.LayerNorm(dim)

    def forward(self, x: torch.Tensor, mask: torch.Tensor | None = None) -> torch.Tensor:
        residual = x
        x = self.attention(x, mask)
        x = residual + x
        x = self.layer_norm(x)

        residual = x
        x = self.feed_forward_intermediate_dense(x)
        x = F.gelu(x)
        x = self.feed_forward_output_dense(x)
        x = residual + x
        x = self.final_layer_norm(x)
        return x


class HubertLite(nn.Module):
    """HuBERT base 的推理前向——只需要 `features_only` 那条路（训练相关的头一律不带）。"""

    def __init__(self):
        super().__init__()
        self.conv_layers = nn.ModuleList()
        for i, (k, s) in enumerate(zip(_KERNELS, _STRIDES)):
            block = nn.Module()
            block.conv = nn.Conv1d(_DIM if i else 1, _DIM, k, stride=s, bias=False)
            block.layer_norm = nn.GroupNorm(_DIM, _DIM, affine=True) if i == 0 else None
            self.conv_layers.append(block)

        self.feature_layer_norm = nn.LayerNorm(_DIM)
        self.post_extract_proj = nn.Linear(_DIM, _HIDDEN)
        self.pos_conv_weight_g: torch.Tensor
        self.pos_conv_weight_v: torch.Tensor
        self.pos_conv_bias: torch.Tensor
        self.register_buffer("pos_conv_weight_g", torch.zeros(1, 1, _CONV_POS))
        self.register_buffer("pos_conv_weight_v", torch.zeros(_HIDDEN, _HIDDEN // _CONV_POS_GROUPS, _CONV_POS))
        self.register_buffer("pos_conv_bias", torch.zeros(_HIDDEN))
        self.encoder_layer_norm = nn.LayerNorm(_HIDDEN)
        self.layers = nn.ModuleList([_Layer(_HIDDEN, _HEADS, _HIDDEN * 4) for _ in range(_LAYERS)])
        self.final_proj: nn.Linear | None = None

    # ---- 权重装载 -------------------------------------------------------

    def load_fairseq(self, state: dict) -> None:
        """把 fairseq 存档的 `model` 子字典搬进本实现（键名一一对应）。"""
        sd: dict[str, torch.Tensor] = {}
        for i in range(len(_KERNELS)):
            sd[f"conv_layers.{i}.conv.weight"] = state[f"feature_extractor.conv_layers.{i}.0.weight"]
        sd["conv_layers.0.layer_norm.weight"] = state["feature_extractor.conv_layers.0.2.weight"]
        sd["conv_layers.0.layer_norm.bias"] = state["feature_extractor.conv_layers.0.2.bias"]
        sd["feature_layer_norm.weight"] = state["layer_norm.weight"]
        sd["feature_layer_norm.bias"] = state["layer_norm.bias"]
        sd["post_extract_proj.weight"] = state["post_extract_proj.weight"]
        sd["post_extract_proj.bias"] = state["post_extract_proj.bias"]
        sd["pos_conv_weight_g"] = state["encoder.pos_conv.0.weight_g"]
        sd["pos_conv_weight_v"] = state["encoder.pos_conv.0.weight_v"]
        sd["pos_conv_bias"] = state["encoder.pos_conv.0.bias"]
        sd["encoder_layer_norm.weight"] = state["encoder.layer_norm.weight"]
        sd["encoder_layer_norm.bias"] = state["encoder.layer_norm.bias"]
        for i in range(_LAYERS):
            p = f"encoder.layers.{i}."
            q = f"layers.{i}."
            for name in ("k_proj", "v_proj", "q_proj", "out_proj"):
                sd[q + f"attention.{name}.weight"] = state[p + f"self_attn.{name}.weight"]
                sd[q + f"attention.{name}.bias"] = state[p + f"self_attn.{name}.bias"]
            sd[q + "layer_norm.weight"] = state[p + "self_attn_layer_norm.weight"]
            sd[q + "layer_norm.bias"] = state[p + "self_attn_layer_norm.bias"]
            sd[q + "feed_forward_intermediate_dense.weight"] = state[p + "fc1.weight"]
            sd[q + "feed_forward_intermediate_dense.bias"] = state[p + "fc1.bias"]
            sd[q + "feed_forward_output_dense.weight"] = state[p + "fc2.weight"]
            sd[q + "feed_forward_output_dense.bias"] = state[p + "fc2.bias"]
            sd[q + "final_layer_norm.weight"] = state[p + "final_layer_norm.weight"]
            sd[q + "final_layer_norm.bias"] = state[p + "final_layer_norm.bias"]

        missing, unexpected = self.load_state_dict(sd, strict=False)
        if missing or unexpected:
            raise RuntimeError(f"hubert 权重装载不完整：missing={missing} unexpected={unexpected}")

        if "final_proj.weight" in state:
            proj = nn.Linear(state["final_proj.weight"].shape[1], state["final_proj.weight"].shape[0])
            with torch.no_grad():
                proj.weight.copy_(state["final_proj.weight"])
                proj.bias.copy_(state["final_proj.bias"])
            self.final_proj = proj

    # ---- 前向 -----------------------------------------------------------

    def forward_features(self, x: torch.Tensor) -> torch.Tensor:
        x = x.unsqueeze(1)
        for block in self.conv_layers:
            x = block.conv(x)
            if block.layer_norm is not None:
                x = block.layer_norm(x)
            x = F.gelu(x)
        return x.transpose(1, 2)

    def forward(self, x: torch.Tensor, output_layer: int | None = None) -> torch.Tensor:
        """x: (B, T) 16 kHz 原始波形；返回 (B, T', 768)，`output_layer` 为 1 起的层号。"""
        h = self.forward_features(x)
        h = self.feature_layer_norm(h)
        h = self.post_extract_proj(h)

        # 位置卷积（fairseq 是「加到输入上」；SamePad 去掉末尾 1 帧，再过 GELU）
        w = _weight_norm(self.pos_conv_weight_g, self.pos_conv_weight_v)
        pad = _CONV_POS // 2
        pos = F.conv1d(h.transpose(1, 2), w, self.pos_conv_bias, padding=pad, groups=_CONV_POS_GROUPS)
        pos = F.gelu(pos[:, :, : h.shape[1]])
        h = h + pos.transpose(1, 2)
        h = self.encoder_layer_norm(h)

        last = _LAYERS if output_layer is None else min(int(output_layer), _LAYERS)
        for i in range(last):
            h = self.layers[i](h)
        return h

    def features(self, x: torch.Tensor, output_layer: int, use_final_proj: bool) -> torch.Tensor:
        """RVC 口径的特征：v1 取第 9 层再过 final_proj（256 维），v2 取第 12 层（768 维）。"""
        h = self.forward(x, output_layer)
        if use_final_proj and self.final_proj is not None:
            h = self.final_proj(h)
        return h


def load_hubert(path: str, device: torch.device | str = "cpu", half: bool = False) -> HubertLite:
    """从 fairseq 存档 `hubert_base.pt` 建一个可推理的 HubertLite。"""
    _install_fairseq_stub()
    ckpt = torch.load(path, map_location="cpu", weights_only=False)
    state = ckpt["model"] if isinstance(ckpt, dict) and "model" in ckpt else ckpt
    model = HubertLite()
    model.load_fairseq(state)
    model.eval()
    model = model.to(device)
    if half:
        model = model.half()
    return model
