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
#   事件：{"ev":"ready"|"start"|"chunk"|"pong"|"done"|"error", ...}
#   时长口径：seconds 缺省（null / 0）= 自动——按标点把长文本切成若干段逐段合成再拼回，
#   总时长没有上限，每段都落在模型的自然长度内（语速由模型自己定，不被压缩）；
#   seconds 给了数字 = 整段强制该秒数、不切段（用户明确要这个时长时才会走到这里）。
import os
import re
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
SAMPLE_RATE = int(RUNTIME.codec.sample_rate)
TEXT_MAX_TOKENS = int(getattr(RUNTIME, "default_text_max_len", 256) or 256)
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

# [段3b] 长文本切段——按句读聚合到预算内，每段各按自然时长合成，最后拼回一条 wav。
# 目的：总时长不再有上限，同时每段都落在模型的自然长度里，语速不被压缩（旧行为是整段夹到 30 秒 → 说得发急）。
CHUNK_CHARS = 60          # 每段字符预算：日语自然语速下约 22 秒，稳在模型训练长度（30 秒）以内
CHUNK_MAX_SECONDS = 300.0  # 单段时长兜底（秒）——只防预测跑飞，不是语速闸门（旧默认 30 才是闸门）
CHUNK_GAP_SECONDS = 0.20   # 段间静音——句读处留一口气，拼接处不贴太紧

_SENT_RE = re.compile(r"(?<=[。．！？!?…‥])|(?<=\n)")
_CLAUSE_RE = re.compile(r"(?<=[、，,；;])")


def split_text(text, budget=CHUNK_CHARS):
    # 先按句末标点切句，短句聚到预算内；超预算的长句先按逗号拆，仍超再硬切
    units = []
    for sentence in _SENT_RE.split(text):
        sentence = sentence.strip()
        if sentence == "":
            continue
        if len(sentence) <= budget:
            units.append(sentence)
            continue
        for clause in _CLAUSE_RE.split(sentence):
            clause = clause.strip()
            if clause == "":
                continue
            if len(clause) <= budget:
                units.append(clause)
            else:
                units.extend(clause[i:i + budget] for i in range(0, len(clause), budget))

    chunks = []
    cur = ""
    for unit in units:
        if cur != "" and len(cur) + len(unit) > budget:
            chunks.append(cur)
            cur = ""
        cur += unit
    if cur != "":
        chunks.append(cur)
    return chunks


def flatten(audio):
    # 运行时给的形状是 (声道, 样本)，单声道就是 (1, N)
    # 先搬到 CPU：拼接要与段间静音同设备，声码器在卡上时不能直接 cat
    piece = audio if audio.dim() == 1 else audio.reshape(-1)
    return piece.detach().to(device="cpu", dtype=torch.float32)


def join_audios(pieces, gap_seconds):
    gap = torch.zeros(int(round(gap_seconds * SAMPLE_RATE)), dtype=pieces[0].dtype)
    parts = []
    for idx, piece in enumerate(pieces):
        if idx:
            parts.append(gap)
        parts.append(piece)
    return torch.cat(parts)


def token_len(piece):
    # 模型单次文本上限：超了 tokenizer 会静默截断（尾部整段丢失）
    try:
        return int(RUNTIME.tokenizer.encode(piece).numel())
    except Exception:
        return -1


def fit_tokens(piece, budget):
    # 字符预算之外的第二道闸：token 超预算就二分切开——长片假名 / 外文混排的字符数不代表 token 数
    n = token_len(piece)
    if n < 0 or n <= budget or len(piece) <= 1:
        return [piece]
    mid = len(piece) // 2
    return fit_tokens(piece[:mid], budget) + fit_tokens(piece[mid:], budget)


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

        # 每段共用的条件（音色 / 风格 / 步数），逐段只换文本
        base_kwargs = dict(
            caption=caption,
            no_ref=no_ref,
            ref_wav=(refs[0] if (not no_ref and len(refs) == 1) else None),
            ref_wavs=(refs if (not no_ref and len(refs) > 1) else None),
            num_steps=None if steps in (None, "", 0) else int(steps),
            duration_scale=1.0 if dur in (None, "") else float(dur),
            lora_adapter=(LORA if (bool(req.get("useLora", True)) and LORA != "") else None),
        )
        base_seed = None if seed in (None, "", -1) else int(seed)
        manual = None if secs in (None, "", 0) else float(secs)

        out_dir = os.path.dirname(out)
        if out_dir:
            os.makedirs(out_dir, exist_ok=True)

        emit({"ev": "start", "id": rid})
        _t1 = time.time()

        pieces = []
        messages = []
        used_seeds = []
        sample_rate = SAMPLE_RATE

        if manual is not None:
            # 用户点名了时长：整段一次合成（上限放宽到安全值，不再被 30 秒夹紧）
            n_tokens = token_len(text)
            if n_tokens > TEXT_MAX_TOKENS:
                raise ValueError(
                    "文本 %d 个 token，超过模型单次上限 %d——强制秒数只支持单次长度的文本；"
                    "要读长文请把强制秒数留空（自动按句切段，总时长不设上限）"
                    % (n_tokens, TEXT_MAX_TOKENS))
            sreq = SamplingRequest(
                text=text,
                seconds=manual,
                max_seconds=max(manual, CHUNK_MAX_SECONDS),
                seed=base_seed,
                **base_kwargs,
            )
            result = RUNTIME.synthesize(sreq)
            sample_rate = int(result.sample_rate)
            pieces.append(flatten(result.audio))
            messages = list(result.messages)
            used_seeds = [int(result.used_seed)]
        else:
            # 自动：切段逐段合成，段内各自按模型预测的自然时长出声
            chunks = split_text(text)
            if not chunks:
                # 整段只有标点 / 换行时，切段结果为空——回落整段一次合成，别让产物链断在拼接处
                chunks = [text]
            total = len(chunks)
            emit({"ev": "plan", "id": rid, "chunks": total})
            for idx, chunk in enumerate(chunks):
                for piece_text in fit_tokens(chunk, TEXT_MAX_TOKENS - 8):
                    seed_i = None if base_seed is None else base_seed + len(used_seeds)
                    sreq = SamplingRequest(
                        text=piece_text,
                        seconds=None,
                        max_seconds=CHUNK_MAX_SECONDS,
                        seed=seed_i,
                        **base_kwargs,
                    )
                    result = RUNTIME.synthesize(sreq)
                    sample_rate = int(result.sample_rate)
                    pieces.append(flatten(result.audio))
                    messages.extend(result.messages)
                    used_seeds.append(int(result.used_seed))
                    emit({
                        "ev": "chunk",
                        "id": rid,
                        "index": idx + 1,
                        "total": total,
                        "text": piece_text[:24],
                        "audioSeconds": round(pieces[-1].numel() / float(sample_rate), 2),
                    })

        audio = join_audios(pieces, CHUNK_GAP_SECONDS)
        save_wav(out, audio.unsqueeze(0), sample_rate)

        samples = int(audio.numel())
        emit({
            "ev": "done",
            "id": rid,
            "file": os.path.basename(out),
            "path": out,
            "seconds": round(time.time() - _t1, 2),
            "audioSeconds": round(samples / float(sample_rate), 2),
            "sampleRate": int(sample_rate),
            "seed": int(used_seeds[0]) if used_seeds else -1,
            "chunks": len(used_seeds),
            # 只回 warning（info 行是每次加载 LoRA / 参考音频的常规日志，拼到面板上没有信息量）
            "warnings": [str(m) for m in messages if str(m).lstrip().lower().startswith("warning")][-5:],
        })
    except Exception as exc:
        emit({"ev": "error", "id": rid, "stage": "run", "message": str(exc),
              "traceback": traceback.format_exc()[-4000:]})
""";
}
