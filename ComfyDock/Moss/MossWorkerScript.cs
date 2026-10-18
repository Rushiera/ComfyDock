namespace ComfyDock.Moss;

/// <summary>
/// 内置推理 worker 的 Python 源码——编译进程序，运行时解到 <c>data\moss\moss_worker.py</c> 再执行。
/// <para>它不启动 ComfyUI 的 HTTP 服务（没有端口、没有界面），只复用 ComfyUI 的 Python 推理内核
/// （comfy 包 + custom_nodes 里的 MOSS 插件），所以「模型住在 ComfyUI 里、ComfyUI 不用跑」两件事同时成立。</para>
/// </summary>
internal static class MossWorkerScript
{
    /// <summary>worker 脚本正文。</summary>
    public const string Source = """
# -*- coding: utf-8 -*-
# MOSS 内置推理 worker——不起 ComfyUI 服务，只借它的 Python 推理内核。
# 协议：stdin 每行一个 JSON 请求，stdout 每行一个 JSON 事件（stdout 已被独占，节点打印全部改走 stderr）。
#   请求：{"cmd":"run","id":"...","prompt":{...},"outDir":"...","seed":123}
#         {"cmd":"ping"} / {"cmd":"quit"}
#   事件：{"ev":"ready"|"pong"|"done"|"error", ...}
import os
import sys
import json
import time
import uuid
import shutil
import traceback

# [段1] 协议通道独占 fd 1——自定义节点大量 print 到 stdout，不隔离就会污染行协议
_PROTO = os.fdopen(os.dup(1), "w", encoding="utf-8", buffering=1)
os.dup2(2, 1)
sys.stdout = sys.stderr


def emit(obj):
    _PROTO.write(json.dumps(obj, ensure_ascii=False) + "\n")
    _PROTO.flush()


def fail(stage, message):
    emit({"ev": "error", "stage": stage, "message": message,
          "traceback": traceback.format_exc()[-4000:]})
    sys.exit(5)


if len(sys.argv) < 2:
    emit({"ev": "error", "stage": "args", "message": "usage: moss_worker.py <comfy_root>"})
    sys.exit(2)

COMFY = os.path.abspath(sys.argv[1])
if not os.path.isdir(COMFY):
    emit({"ev": "error", "stage": "args", "message": "ComfyUI 根目录不存在: " + COMFY})
    sys.exit(2)

sys.path.insert(0, COMFY)
os.chdir(COMFY)

# [段2] 导入内核
try:
    import asyncio
    _LOOP = asyncio.new_event_loop()
    asyncio.set_event_loop(_LOOP)
    import torch
    import comfy.cli_args
    import folder_paths
    from app.assets.manager import NoAssets
    import server
    import execution
    import nodes
except Exception:
    fail("import", "导入 ComfyUI 内核失败")

# [段3] 建 server 对象（只建对象，不监听端口）与节点表
t0 = time.time()
try:
    _ASSETS = NoAssets(comfy.cli_args.args)
    _SERVER = server.PromptServer(_LOOP, _ASSETS)
    _LOOP.run_until_complete(nodes.init_extra_nodes(init_custom_nodes=True))
    _EXEC = execution.PromptExecutor(
        _SERVER,
        cache_type=execution.CacheType.CLASSIC,
        cache_args={"lru": 0, "ram": 10.0, "ram_inactive": 64.0},
        asset_manager=_ASSETS,
    )
except Exception:
    fail("init", "初始化推理内核失败")

emit({"ev": "ready",
      "seconds": round(time.time() - t0, 1),
      "torch": torch.__version__,
      "cuda": bool(torch.cuda.is_available()),
      "moss": "MOSSImageUNETLoader" in nodes.NODE_CLASS_MAPPINGS})


def output_node_id(graph):
    for nid in graph:
        node = graph[nid]
        if node.get("class_type") == "SaveImage":
            return nid
    return None


def collect_files(history, out_dir, seed):
    saved = []
    if not history:
        return saved
    outputs = history.get("outputs") or {}
    root = folder_paths.get_output_directory()
    os.makedirs(out_dir, exist_ok=True)
    index = 0
    for nid in outputs:
        out = outputs[nid] or {}
        for img in out.get("images") or []:
            index += 1
            src = os.path.join(root, img.get("subfolder") or "", img.get("filename") or "")
            if not os.path.isfile(src):
                continue
            name = str(seed) + (("_" + str(index)) if index > 1 else "") + ".png"
            shutil.copyfile(src, os.path.join(out_dir, name))
            saved.append(name)
    return saved


# [段4] 主循环——一条请求一条事件流
for _line in sys.stdin:
    _line = _line.strip()
    if not _line:
        continue
    try:
        req = json.loads(_line)
    except Exception:
        emit({"ev": "error", "stage": "parse", "message": "请求行不是合法 JSON"})
        continue

    cmd = req.get("cmd") or "run"
    if cmd == "quit":
        break
    if cmd == "ping":
        emit({"ev": "pong"})
        continue

    rid = req.get("id") or str(uuid.uuid4())
    graph = req.get("prompt") or {}
    seed = int(req.get("seed") or 0)
    out_dir = req.get("outDir") or ""

    nid = output_node_id(graph)
    if nid is None:
        emit({"ev": "error", "id": rid, "stage": "graph", "message": "节点图里没有 SaveImage 输出节点"})
        continue
    if not out_dir:
        emit({"ev": "error", "id": rid, "stage": "graph", "message": "缺 outDir"})
        continue

    t1 = time.time()
    try:
        _EXEC.reset()
        _EXEC.execute(graph, rid, execute_outputs=[nid])
        err = None
        for ev, data in _EXEC.status_messages:
            if ev == "execution_error":
                err = (data.get("exception_type") or "") + ": " + (data.get("exception_message") or "")
        if err:
            emit({"ev": "error", "id": rid, "stage": "execute", "message": err})
            continue
        files = collect_files(_EXEC.history_result, out_dir, seed)
        emit({"ev": "done", "id": rid, "files": files, "seconds": round(time.time() - t1, 1)})
    except Exception:
        emit({"ev": "error", "id": rid, "stage": "execute", "message": "执行失败",
              "traceback": traceback.format_exc()[-4000:]})
""";
}
