namespace ComfyDock.Tagger;

/// <summary>
/// WD Tagger worker 的 Python 源码——编译进程序，运行时解到 <c>data\tagger\wd_tagger_worker.py</c> 再执行。
/// <para>推理口径严格照官方实现（SmilingWolf/wd-tagger 的 app.py）：RGBA 合白底 → 补正方 → BICUBIC 缩放
/// → float32 → RGB 转 BGR → 加 batch 维；输出按 category 拆成 rating（9）/ general（0）/ character（4）三组，
/// rating 取 argmax，其余按阈值过滤，可选 MCut 自适应阈值（角色组下限 0.15）。
/// 产出的通用标签串按概率降序、括号转义为 <c>\(</c> / <c>\)</c>——与官方一致，可直接回灌提示词。</para>
/// <para>常驻进程把 onnxruntime 会话加载一次（按模型路径缓存），之后每张图只跑一次前向。</para>
/// </summary>
internal static class TaggerWorkerScript
{
    /// <summary>worker 脚本正文。</summary>
    public const string Source = """"
# -*- coding: utf-8 -*-
# WD Tagger worker —— 常驻进程，onnxruntime 会话按模型路径缓存，一次加载多次推理。
# 协议：stdin 每行一个 JSON 请求，stdout 每行一个 JSON 事件（stdout 被独占，库里的 print 全部改走 stderr）。
#   请求：{"cmd":"run","id":"..","modelId":"eva02-large","onnx":"D:\\..\\model.onnx","labels":"D:\\..\\selected_tags.csv",
#          "images":["D:\\..\\a.png"],"generalThreshold":0.35,"characterThreshold":0.85,
#          "generalMcut":false,"characterMcut":false,"device":"auto","writeText":true}
#         {"cmd":"ping"} / {"cmd":"quit"}
#   事件：{"ev":"ready"|"start"|"item"|"pong"|"done"|"error", ...}
import os
import sys
import json
import time
import traceback
import csv

# [段1] 协议通道独占 fd 1——onnxruntime / PIL 会往 stdout 打日志，不隔离就污染行协议
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


# 颜文字白名单——这些标签里的下划线是字面量，不能换成空格（官方同表）
KAOMOJIS = {
    "0_0", "(o)_(o)", "+_+", "+_-", "._.", "<o>_<o>", "<|>_<|>", "=_=", ">_<",
    "3_3", "6_9", ">_o", "@_@", "^_^", "o_o", "u_u", "x_x", "|_|", "||_||",
}

# [段2] 依赖
try:
    import numpy as np
    import onnxruntime as rt
    from PIL import Image
except Exception:
    fail("import", "导入 numpy / onnxruntime / PIL 失败（这几样要装在 worker 用的解释器里）")


def pick_providers(want):
    """按请求挑执行提供器——want=auto 时有 CUDA 就用 CUDA，否则 CPU。"""
    available = set(rt.get_available_providers())
    want = (want or "auto").lower()
    if want == "cpu":
        return ["CPUExecutionProvider"]
    if "CUDAExecutionProvider" in available:
        return ["CUDAExecutionProvider", "CPUExecutionProvider"]
    return ["CPUExecutionProvider"]


def load_labels(csv_path):
    """读标签表——名字里的下划线换成空格，颜文字除外；同时按 category 分出三组下标。"""
    names = []
    ratings = []
    general = []
    characters = []
    with open(csv_path, "r", encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            raw = row.get("name", "")
            name = raw if raw in KAOMOJIS else raw.replace("_", " ")
            idx = len(names)
            names.append(name)
            try:
                category = int(row.get("category", "0"))
            except Exception:
                category = 0
            if category == 9:
                ratings.append(idx)
            elif category == 4:
                characters.append(idx)
            elif category == 0:
                general.append(idx)
    return names, ratings, general, characters


def mcut_threshold(probs):
    """MCut（Maximum Cut Thresholding）——按排好序的概率最大间隔切一刀。"""
    if len(probs) == 0:
        return 0.0
    sorted_probs = probs[np.argsort(probs)[::-1]]
    if len(sorted_probs) < 2:
        return float(sorted_probs[0])
    difs = sorted_probs[:-1] - sorted_probs[1:]
    t = int(difs.argmax())
    return float((sorted_probs[t] + sorted_probs[t + 1]) / 2.0)


def prepare_image(path, target_size):
    """官方预处理：合白底 → 转 RGB → 补正方 → BICUBIC 缩放 → float32 → BGR → batch 维。"""
    with Image.open(path) as raw:
        img = raw.convert("RGBA")
    canvas = Image.new("RGBA", img.size, (255, 255, 255, 255))
    canvas.alpha_composite(img)
    img = canvas.convert("RGB")

    width, height = img.size
    max_dim = max(width, height)
    pad_left = (max_dim - width) // 2
    pad_top = (max_dim - height) // 2
    padded = Image.new("RGB", (max_dim, max_dim), (255, 255, 255))
    padded.paste(img, (pad_left, pad_top))

    if max_dim != target_size:
        padded = padded.resize((target_size, target_size), Image.BICUBIC)

    array = np.asarray(padded, dtype=np.float32)
    array = array[:, :, ::-1]
    return np.expand_dims(array, axis=0)


# [段3] 会话缓存——同一个 onnx 只加载一次
_SESSIONS = {}


def get_session(onnx_path, device):
    key = (os.path.abspath(onnx_path), (device or "auto").lower())
    if key in _SESSIONS:
        return _SESSIONS[key]
    providers = pick_providers(device)
    options = rt.SessionOptions()
    options.log_severity_level = 3
    session = rt.InferenceSession(onnx_path, sess_options=options, providers=providers)
    _SESSIONS[key] = session
    return session


emit({
    "ev": "ready",
    "python": sys.version.split()[0],
    "numpy": np.__version__,
    "onnxruntime": rt.__version__,
    "providers": rt.get_available_providers(),
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
        onnx_path = str(req.get("onnx", "")).strip()
        labels_path = str(req.get("labels", "")).strip()
        if onnx_path == "" or not os.path.isfile(onnx_path):
            raise ValueError("权重不在位：" + onnx_path)
        if labels_path == "" or not os.path.isfile(labels_path):
            raise ValueError("标签表不在位：" + labels_path)

        images = [str(p) for p in (req.get("images") or []) if str(p).strip() != ""]
        if len(images) == 0:
            raise ValueError("没有待打标的图片")

        gen_thresh = float(req.get("generalThreshold", 0.35))
        char_thresh = float(req.get("characterThreshold", 0.85))
        gen_mcut = bool(req.get("generalMcut", False))
        char_mcut = bool(req.get("characterMcut", False))
        device = str(req.get("device", "auto"))
        write_text = bool(req.get("writeText", True))

        emit({"ev": "start", "id": rid, "total": len(images)})

        _t0 = time.time()
        session = get_session(onnx_path, device)
        input_meta = session.get_inputs()[0]
        _, height, width, _ = input_meta.shape
        target_size = int(height if height else 448)
        _ = width

        names, rating_idx, general_idx, character_idx = load_labels(labels_path)

        for path in images:
            _ti = time.time()
            try:
                batch = prepare_image(path, target_size)
                preds = session.run(None, {input_meta.name: batch})[0]
                probs = np.asarray(preds[0], dtype=np.float32)

                rating_list = [{"name": names[i], "score": float(probs[i])} for i in rating_idx]
                rating = ""
                if rating_list:
                    best = max(rating_list, key=lambda kv: kv["score"])
                    rating = best["name"]

                general_scores = np.array([float(probs[i]) for i in general_idx], dtype=np.float32)
                g_thresh = mcut_threshold(general_scores) if gen_mcut else gen_thresh
                general_hits = [
                    {"name": names[i], "score": float(probs[i])}
                    for i in general_idx if float(probs[i]) > g_thresh
                ]
                general_hits.sort(key=lambda kv: kv["score"], reverse=True)

                char_scores = np.array([float(probs[i]) for i in character_idx], dtype=np.float32)
                c_thresh = mcut_threshold(char_scores) if char_mcut else char_thresh
                if char_mcut:
                    c_thresh = max(0.15, c_thresh)
                char_hits = [
                    {"name": names[i], "score": float(probs[i])}
                    for i in character_idx if float(probs[i]) > c_thresh
                ]
                char_hits.sort(key=lambda kv: kv["score"], reverse=True)

                general_text = ", ".join(kv["name"] for kv in general_hits)
                general_text = general_text.replace("(", "\\(").replace(")", "\\)")

                text_file = ""
                if write_text:
                    text_file = os.path.splitext(path)[0] + ".txt"
                    with open(text_file, "w", encoding="utf-8") as handle:
                        handle.write("rating: " + rating + "\n")
                        handle.write("general: " + general_text + "\n")
                        handle.write("characters: "
                                     + ", ".join(kv["name"] for kv in char_hits) + "\n")

                emit({
                    "ev": "item",
                    "id": rid,
                    "file": os.path.basename(path),
                    "path": path,
                    "rating": rating,
                    "ratings": rating_list,
                    "general": general_hits,
                    "characters": char_hits,
                    "generalText": general_text,
                    "textFile": os.path.basename(text_file) if text_file else "",
                    "seconds": round(time.time() - _ti, 3),
                })
            except Exception as exc:
                emit({"ev": "itemerror", "id": rid, "file": os.path.basename(path),
                      "message": str(exc), "traceback": traceback.format_exc()[-2000:]})

        emit({"ev": "done", "id": rid, "seconds": round(time.time() - _t0, 2)})
    except Exception as exc:
        emit({"ev": "error", "id": rid, "stage": "run", "message": str(exc),
              "traceback": traceback.format_exc()[-4000:]})
"""";
}
