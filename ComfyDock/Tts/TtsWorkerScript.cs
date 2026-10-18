namespace ComfyDock.Tts;

/// <summary>
/// IrodoriTTS worker 的 Python 源码——编译进程序，运行时解到 <c>data\tts\tts_worker.py</c> 再执行。
/// <para>它常驻一个进程把 Irodori-TTS 的推理运行时（基座 + 声码器 + LoRA）加载一次，之后每句只跑采样：
/// 首次加载约 30~60 秒，之后单句十几秒。stdin 每行一个 JSON 请求，stdout 每行一个 JSON 事件。</para>
/// </summary>
internal static class TtsWorkerScript
{
    /// <summary>worker 脚本正文。</summary>
    public const string Source = """
# -*- coding: utf-8 -*-
# Irodori-TTS worker——常驻进程，运行时只加载一次。
# 协议：stdin 每行一个 JSON 请求，stdout 每行一个 JSON 事件（stdout 被独占，库里的 print 全部改走 stderr）。
#   请求：{"cmd":"run","id":"..","text":"..","caption":"..","refs":[".."],"noRef":false,
#          "steps":40,"seed":2026,"durationScale":1.0,"seconds":null,"useLora":true,"out":"D:\\..\\a.wav"}
#         {"cmd":"ping"} / {"cmd":"quit"}
#   事件：{"ev":"ready"|"start"|"pong"|"done"|"error", ...}
import os
import sys
import json
import time
import traceback

# [段1] 协议通道独占 fd 1——torch / 声码器会往 stdout 打日志，不隔离就污染行协议
_PROTO = os.fdopen(os.dup(1), "w", encoding="utf-8", buffering=1)
os.dup2(2, 1)
sys.stdout = sys.stderr

# stdin 显式按 UTF-8 解码：Windows 上默认走本地代码页 / surrogateescape，日文会被解成代理字符，
# 下游 tokenizers 直接整批拒收（TextEncodeInput must be Union[...]）。
try:
    sys.stdin.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def emit(obj):
    try:
        line = json.dumps(obj, ensure_ascii=False)
        _PROTO.write(line + "\n")
    except UnicodeEncodeError:
        # 文本里万一混进代理字符，退回 ascii 转义，别让协议行本身炸掉
        _PROTO.write(json.dumps(obj, ensure_ascii=True) + "\n")
    _PROTO.flush()


def clean(value):
    # 去掉解码残留的代理字符——tokenizers 遇到它们会整批拒收
    s = "" if value is None else str(value)
    try:
        s.encode("utf-8")
    except UnicodeEncodeError:
        s = s.encode("utf-8", "ignore").decode("utf-8", "ignore")
    return s.strip()


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
BASE_MODEL = arg("--base-model")
CODEC = arg("--codec")
LORA = arg("--lora")
DEVICE = arg("--device", "cuda")
CODEC_DEVICE = arg("--codec-device", DEVICE)
PRECISION = arg("--precision", "bf16")

if REPO == "" or BASE_MODEL == "" or CODEC == "":
    emit({"ev": "error", "stage": "args",
          "message": "用法: tts_worker.py --repo <irodori-tts 目录> --base-model <safetensors> --codec <weights.pth> [--lora <适配器目录>] [--device cuda] [--precision bf16]"})
    sys.exit(2)

# 水印权重（sony/silentcipher）走镜像；huggingface.co 直连在部分网络下不通
os.environ.setdefault("HF_ENDPOINT", "https://hf-mirror.com")
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")
sys.path.insert(0, REPO)

# [段2] 导入运行时
try:
    import torch
    from irodori_tts.inference_runtime import (
        InferenceRuntime,
        RuntimeKey,
        SamplingRequest,
        save_wav,
    )
except Exception:
    fail("import", "导入 irodori_tts 失败（检查 --repo 是否指向 Irodori-TTS 仓库根）")

# [段3] 加载基座 + 声码器（只做一次）
_t0 = time.time()
try:
    _key = RuntimeKey(
        checkpoint=BASE_MODEL,
        model_device=DEVICE,
        codec_repo=CODEC,
        model_precision=PRECISION,
        codec_device=CODEC_DEVICE,
        codec_precision="fp32",
        codec_deterministic_encode=True,
        codec_deterministic_decode=True,
        compile_model=False,
        compile_dynamic=False,
    )
    RUNTIME = InferenceRuntime.from_key(_key)
except Exception:
    fail("load", "加载基座 / 声码器失败（检查 --base-model 与 --codec 路径）")

_cuda = bool(torch.cuda.is_available())
emit({
    "ev": "ready",
    "torch": str(torch.__version__),
    "cuda": _cuda,
    "device": torch.cuda.get_device_name(0) if _cuda else "cpu",
    "lora": LORA,
    "base": os.path.basename(BASE_MODEL),
    "precision": PRECISION,
    "codecDevice": CODEC_DEVICE,
    "seconds": round(time.time() - _t0, 1),
})

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

    rid = str(req.get("id", ""))
    try:
        text = clean(req.get("text", ""))
        if text == "":
            raise ValueError("文本为空")

        refs = [clean(p) for p in (req.get("refs") or []) if clean(p) != ""]
        no_ref = bool(req.get("noRef")) or len(refs) == 0

        caption = clean(req.get("caption"))
        caption = None if caption == "" else caption

        steps = req.get("steps")
        seed = req.get("seed")
        dur = req.get("durationScale")
        secs = req.get("seconds")

        out = str(req.get("out", "")).strip()
        if out == "":
            raise ValueError("缺输出路径")

        sreq = SamplingRequest(
            text=text,
            caption=caption,
            no_ref=no_ref,
            ref_wav=(refs[0] if (not no_ref and len(refs) == 1) else None),
            ref_wavs=(refs if (not no_ref and len(refs) > 1) else None),
            num_steps=None if steps in (None, "", 0) else int(steps),
            seed=None if seed in (None, "", -1) else int(seed),
            duration_scale=1.0 if dur in (None, "") else float(dur),
            seconds=None if secs in (None, "", 0) else float(secs),
            lora_adapter=(LORA if (bool(req.get("useLora", True)) and LORA != "") else None),
        )

        out_dir = os.path.dirname(out)
        if out_dir:
            os.makedirs(out_dir, exist_ok=True)

        emit({"ev": "start", "id": rid})
        _t1 = time.time()
        result = RUNTIME.synthesize(sreq)
        save_wav(out, result.audio, result.sample_rate)

        samples = int(result.audio.shape[-1])
        emit({
            "ev": "done",
            "id": rid,
            "file": os.path.basename(out),
            "path": out,
            "seconds": round(time.time() - _t1, 2),
            "audioSeconds": round(samples / float(result.sample_rate), 2),
            "sampleRate": int(result.sample_rate),
            "seed": int(result.used_seed),
        })
    except Exception as exc:
        emit({"ev": "error", "id": rid, "stage": "run", "message": str(exc),
              "traceback": traceback.format_exc()[-4000:]})
""";
}
