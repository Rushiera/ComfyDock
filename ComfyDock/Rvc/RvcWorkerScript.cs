namespace ComfyDock.Rvc;

/// <summary>
/// RVC worker 的 Python 源码——编译进程序，运行时解到 <c>data\rvc\rvc_worker.py</c> 再执行。
/// <para>常驻一个进程把 HuBERT 特征提取器加载一次，音色模型与检索索引按需切换并缓存；
/// 之后每段音频只跑一遍前向。stdin 每行一个 JSON 请求，stdout 每行一个 JSON 事件。</para>
/// </summary>
internal static class RvcWorkerScript
{
    /// <summary>worker 脚本正文。</summary>
    public const string Source = """
# -*- coding: utf-8 -*-
# RVC worker——常驻进程，HuBERT 只加载一次；音色模型与索引按需切换。
# 协议：stdin 每行一个 JSON 请求，stdout 每行一个 JSON 事件（stdout 被独占，库里的 print 全部改走 stderr）。
#   请求：{"cmd":"run","id":"..","input":"D:\\..\\in.wav","model":"雾雾40k.pth","sid":0,
#          "f0UpKey":0,"f0Method":"rmvpe","indexRate":0.75,"filterRadius":3,
#          "resampleSr":0,"rmsMixRate":0.25,"protect":0.33,"out":"D:\\..\\out.wav"}
#         {"cmd":"ping"} / {"cmd":"models"} / {"cmd":"quit"}
#   事件：{"ev":"ready"|"start"|"pong"|"models"|"done"|"error", ...}
import os
import sys
import json
import time
import traceback

# [段1] 协议通道独占 fd 1——torch / librosa 会往 stdout 打日志，不隔离就污染行协议
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


REPO = arg("--repo")
MODELS = arg("--models")
DEVICE = arg("--device", "cuda")
HALF = arg("--half", "1") not in ("0", "false", "False", "")

if REPO == "" or MODELS == "":
    emit({"ev": "error", "stage": "args",
          "message": "用法: rvc_worker.py --repo <rvc_lite 上级目录> --models <ComfyUI\\models\\RVC> [--device cuda] [--half 1]"})
    sys.exit(2)

sys.path.insert(0, REPO)

# [段2] 导入推理链
try:
    import numpy as np
    import soundfile as sf
    import torch
    from rvc_lite import load_hubert, load_model
except Exception:
    fail("import", "导入 rvc_lite 失败（检查 --repo 是否指向含 rvc_lite 包的目录）")

WEIGHTS_DIR = os.path.join(MODELS, "weights")
INDICES_DIR = os.path.join(MODELS, "indices")
HUBERT_PATH = os.path.join(MODELS, "hubert", "hubert_base.pt")
RMVPE_DIR = os.path.join(MODELS, "rmvpe")

if DEVICE.startswith("cuda") and not torch.cuda.is_available():
    DEVICE = "cpu"

# [段3] 加载 HuBERT（只做一次）
_t0 = time.time()
try:
    HUBERT = load_hubert(HUBERT_PATH, DEVICE, half=(HALF and DEVICE != "cpu"))
except Exception:
    fail("load", "加载 HuBERT 失败（检查 %s）" % HUBERT_PATH)

_cuda = bool(torch.cuda.is_available())
emit({
    "ev": "ready",
    "torch": str(torch.__version__),
    "cuda": _cuda,
    "device": torch.cuda.get_device_name(0) if _cuda else "cpu",
    "half": bool(HALF and DEVICE != "cpu"),
    "models": MODELS,
    "seconds": round(time.time() - _t0, 1),
})

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

    model = load_model(path, DEVICE, half=(HALF and DEVICE != "cpu"), index_dir=INDICES_DIR)
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
        p = os.path.join(WEIGHTS_DIR, name)
        out.append({"name": name, "bytes": os.path.getsize(p)})
    return out


# [段4] 请求循环
for _line in sys.stdin:
    _line = _line.strip()
    if _line == "":
        continue

    try:
        req = json.loads(_line)
    except Exception:
        emit({"ev": "error", "stage": "parse", "message": "请求不是合法 JSON"})
        continue

    cmd = str(req.get("cmd", "run"))
    if cmd == "quit":
        break
    if cmd == "ping":
        emit({"ev": "pong"})
        continue
    if cmd == "models":
        emit({"ev": "models", "models": list_models()})
        continue

    rid = str(req.get("id", ""))
    try:
        src = str(req.get("input", "")).strip()
        if src == "" or not os.path.exists(src):
            raise ValueError("输入音频不在位：%s" % src)

        out = str(req.get("out", "")).strip()
        if out == "":
            raise ValueError("缺输出路径")

        model_path = resolve_model(str(req.get("model", "")).strip())
        model, pipeline = ensure_model(model_path)

        emit({"ev": "start", "id": rid, "model": os.path.basename(model_path),
              "index": os.path.basename(model.index_path or ""),
              "version": model.version, "sr": model.tgt_sr})

        _t1 = time.time()
        audio = model.convert(
            HUBERT, src, pipeline,
            sid=int(req.get("sid", 0) or 0),
            f0_up_key=float(req.get("f0UpKey", 0) or 0),
            f0_method=str(req.get("f0Method", "rmvpe") or "rmvpe"),
            index_rate=float(req.get("indexRate", 0.75)),
            filter_radius=int(req.get("filterRadius", 3) or 3),
            resample_sr=int(req.get("resampleSr", 0) or 0),
            rms_mix_rate=float(req.get("rmsMixRate", 0.25)),
            protect=float(req.get("protect", 0.33)),
        )

        out_dir = os.path.dirname(out)
        if out_dir:
            os.makedirs(out_dir, exist_ok=True)

        sr = int(req.get("resampleSr", 0) or 0)
        if sr < 16000:
            sr = pipeline.tgt_sr
        sf.write(out, audio, sr, subtype="PCM_16")

        emit({
            "ev": "done",
            "id": rid,
            "file": os.path.basename(out),
            "path": out,
            "seconds": round(time.time() - _t1, 2),
            "audioSeconds": round(len(audio) / float(sr), 2),
            "sampleRate": sr,
            "index": os.path.basename(model.index_path or ""),
        })
    except Exception as exc:
        emit({"ev": "error", "id": rid, "stage": "run", "message": str(exc),
              "traceback": traceback.format_exc()[-4000:]})
""";
}
