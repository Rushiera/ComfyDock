# runtime/rvc — RVC 变声推理链（自持实现）

> 这是 ComfyDock「本地生成 → RVC 变声」这一路的推理代码本体，随软件入库（`.gitignore` 里只有 `runtime/irodori-tts/` 被排除）。
> 模型权重不在这里——权重住本机 ComfyUI 的 `models\RVC\`，解释器用 ComfyUI 的 `python_embeded`。

## 这是什么

把 RVC（Retrieval-based-Voice-Conversion）的**变声推理**搬进 ComfyDock 自己的一份实现：

- **不用 RVC 的 WebUI**，也不依赖它的 `infer/` 目录与 py3.9 运行时——原整合包只作素材来源，跑起来不碰它。
- **不装 fairseq**：RVC 的 `hubert_base.pt` 是 fairseq 存档，原版靠 fairseq 加载；这里用纯 torch 复刻了同一份 HuBERT 前向（见 `rvc_lite/hubert.py`），逐层数值与原版对齐（见下「验证」）。
- 只保留**推理**：训练、UVR5 人声分离、实时变声 GUI 都不在范围内。

## 目录

```
runtime/rvc/
└── rvc_lite/
    ├── hubert.py          # HuBERT base 纯 torch 前向 + fairseq 存档加载（含 Dictionary 桩）
    ├── rmvpe.py           # RMVPE 基频提取（取自 RVC，去掉 jit 导出那条支线）
    ├── pipeline.py        # 变声主链：F0 四取法 / 检索混合 / protect / rms 混合 / 重采样
    ├── synth.py           # 音色模型（.pth）加载 + 索引查找（faiss 读中文路径的绕法）
    └── infer_pack/        # 生成器四实现（v1/v2 × 有 f0 / 无 f0），取自 RVC 的 infer_pack
```

worker 脚本（stdin/stdout 行协议）编译在 C# 里：`ComfyDock/Rvc/RvcWorkerScript.cs`，运行时解到 `data\rvc\rvc_worker.py`。

## 模型与依赖落点

| 项 | 位置 |
|:--|:--|
| 音色模型 `.pth` | `ComfyUI\models\RVC\weights\` |
| 检索索引 `.index` | `ComfyUI\models\RVC\indices\` |
| HuBERT（`hubert_base.pt`） | `ComfyUI\models\RVC\hubert\` |
| RMVPE（`rmvpe.pt`） | `ComfyUI\models\RVC\rmvpe\` |
| 解释器 | `ComfyUI\python_embeded\python.exe` |
| 依赖 | 装进 `python_embeded` 的 site-packages |

依赖（装到 ComfyUI 的 `python_embeded`，不从别的环境借）：

```
python_embeded\python.exe -m pip install -i https://pypi.tuna.tsinghua.edu.cn/simple faiss-cpu pyworld praat-parselmouth torchcrepe
```

其余（torch / torchaudio / librosa / soundfile / numpy / scipy）ComfyUI 自带。**fairseq 不装**（py3.12 + torch 2.x 装不上，也不需要）。

## 验证（实测口径）

1. **HuBERT 对齐**：同一段 16 kHz 音频，本实现与原版 fairseq 逐层对比——第 1/5/9/12 层 maxdiff ≤ 7e-6（float32 精度级）。
2. **整链对齐**：把原版 RVC 的中间量（音频 pad、F0、特征）喂给两侧，固定随机种子后段输出 maxdiff ≈ 2.2e-5（相对 2.9e-5）——即实现与 RVC 原链数值等价，残差只来自 NSF 声码器的随机噪声。
3. **内容保真**：输入 2.78 s → 输出 2.76 s；输出与输入的 F0 曲线相关 0.78、中位基频 327 Hz → 321 Hz；mel 谱相关 0.81（同内容、换音色）。
4. **端到端**：`ComfyDock.exe --rvc-selftest <音频> --rvc-model <音色.pth>` —— RTX 4060 Laptop、半精度，HuBERT 就绪 0.9 秒，2.8 秒音频耗时 7.0 秒。

## 已知取舍

- **faiss 打不开中文路径**（C++ 文件读取器的编码问题，原版同样报错）：改用 `np.fromfile` + `faiss.deserialize_index` 绕过。索引读不出来时不报错、静默降级为「不挂检索」——这一点由面板上的「无索引」标注暴露，不静默。
- **NSF 声码器带随机噪声**：不固定种子时两次运行结果不会逐位相同（原版同样如此）。
- `rmvpe.py` 里删掉了 jit 导出与 ipex 分支（那是原版为导出/加速留的支线，推理不需要）。

## 改动入手点（要改某处该动哪个文件）

| 想改什么 | 动这里 |
|:--|:--|
| 面板上某个变声参数（默认值 / 取值范围 / 说明文案） | 默认值与夹取：`ComfyDock/Rvc/RvcService.cs` 的 `RvcConfig` + `UpdateConfig`；说明文案与控件：`ComfyDock/Web/wwwroot/index.html` 的 `genPanelRvc` 块（**改这份源文件**，仓库根 `Web/wwwroot/` 是发布副本） |
| 变声链的数值口径（pad 时长 / F0 归一化 / protect / rms 混合 / 重采样） | `runtime/rvc/rvc_lite/pipeline.py`——`RvcPipeline`（原版对应 `infer/modules/vc/pipeline.py`，逐行可对） |
| HuBERT 前向（结构 / 层数 / 位置卷积） | `runtime/rvc/rvc_lite/hubert.py`——改完**必须重跑对齐**（见「验证」第 1 条），否则音色会悄悄漂 |
| 生成器结构（v1/v2 / 有 f0 / 无 f0 四实现） | `runtime/rvc/rvc_lite/infer_pack/models.py`（取自 RVC，改前先确认权重键名与 `config` 口径） |
| 基频提取（加一种取法 / 改 RMVPE 阈值） | `runtime/rvc/rvc_lite/pipeline.py` 的 `get_f0` + `rmvpe.py`；新取法还要在 `RvcService.NormalizeF0` 与面板下拉里加一档 |
| 音色模型加载 / 索引查找与读取 | `runtime/rvc/rvc_lite/synth.py`（`find_index` 的名字匹配规则、`_load_index` 的中文路径绕法） |
| worker 协议（请求 / 事件字段、缓存策略） | `ComfyDock/Rvc/RvcWorkerScript.cs`（Python 源常量）+ `ComfyDock/Rvc/RvcEngine.cs`（C# 侧协议与超时）；**两侧字段必须同步改** |
| 引擎生命周期（就绪判据 / 超时 / 停止 / 换参重载） | `ComfyDock/Rvc/RvcEngine.cs`（`Prepare` 判变更、`EnsureStartedAsync`、`RunAsync`、`Stop`） |
| 环境探针（多一个依赖 / 改在位判据） | `ComfyDock/Rvc/RvcService.cs` 的 `Deps` 表 + `ModulePresent`（三种形态都认）+ `Probe` |
| 端点（面板调用的 API） | `ComfyDock/Program.cs` 的 `/api/rvc*` 段；服务方法在 `RvcService.cs` |
| 加一个生成器页签（第五个） | 先读 `design-ComfyDock.md` §0.11（常驻 worker 范式），照 `Rvc/` 目录抄一份：`XxxService` + `XxxEngine` + `XxxWorkerScript` + `XxxCli` + `LOCAL_GENS` 加一项 + 一个 `genPanelXxx` 面板 |
| 单机验收 / 排错 | `ComfyDock.exe --rvc-selftest <音频> [--rvc-model <音色.pth>] [--rvc-out <目录>]`——走与面板同一条引擎路径 |

**改完的自检顺序**（本地生成三路通用）：`cs-check` → `cs-build` → `--rvc-selftest` 跑一段真音频 → 面板点一次。
