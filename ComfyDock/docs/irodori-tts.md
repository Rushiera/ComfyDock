# 本地生成 · 依田芳乃 TTS（Irodori-TTS）

> 这份文档是这一路的**来源说明书**：上游是谁、模型哪来的、环境怎么装的、改了上游什么、面板怎么跑。
> 以后要动这个功能，先读这里，别凭印象改。

## 1. 这是什么

面板「本地生成」页签下的第二个生成器（`LOCAL_GENS` 里的 `tts`）。用 [Aratako/Irodori-TTS](https://github.com/Aratako/Irodori-TTS) 做日语 TTS：RF-DiT（整流流扩散 Transformer）+ DACVAE 连续隐变量，48 kHz 单声道输出，支持零样本音色克隆（参考音频）、caption 风格控制、自动时长预测。

音色是「依田芳乃」的 LoRA 微调版——LoRA 权重来自用户提供的 `yoshino_lora` 包，**不含基座**。

## 2. 上游与模型清单

| 项目 | 来源 | 本机落点 |
| :-- | :-- | :-- |
| 推理/训练代码 | `https://github.com/Aratako/Irodori-TTS`（`main`，取回时 HEAD `89f9d8f`） | `D:\Mau\WorkSpace\irodori-tts` |
| 小模型基座 | `Aratako/Irodori-TTS-v4.1-Small`（500M，`model.safetensors` 2.85 GB + `tokenizer/`） | `D:\Mau\WorkSpace\models\Irodori-TTS-v4.1-Small` |
| 大模型基座 | `Aratako/Irodori-TTS-v4-Large`（3.29B，fp32 `model.safetensors` 13.2 GB + `tokenizer/`） | `D:\Mau\WorkSpace\models\Irodori-TTS-v4-Large` |
| 声码器 | `Aratako/Semantic-DACVAE-Japanese-32dim`（`weights.pth` 410 MB） | `D:\Mau\WorkSpace\models\Semantic-DACVAE-Japanese-32dim` |
| 小模型 LoRA | 用户包 `yoshino_lora\small`（rank 16 / alpha 32 / 500 步，PEFT 0.18.1 训练） | `D:\Mau\WorkSpace\yoshino_lora\small` |
| 大模型 LoRA | 用户包 `yoshino_lora\large`（rank 32 / alpha 64 / 400 步） | `D:\Mau\WorkSpace\yoshino_lora\large` |
| 参考音频 | 用户包 `small\voice_profile.json` 里记录的原始链接（biligame 立绘语音 mp3） | `D:\Mau\WorkSpace\yoshino_lora\references` |

LoRA 适配器只带 `adapter_config.json` + `adapter_model.safetensors`（`modules_to_save` 含 `duration_predictor`，`target_modules` 是 DiT attention 的正则），**必须配对应基座**：small↔v4.1-Small、large↔v4-Large，不能交叉。

`yoshino_lora\README_使用说明.md` 是用户侧的原始说明（目录结构、命令行用法、large 基座缺口），本文件是 ComfyDock 侧的实现说明。

## 3. 环境（venv）

独立 venv，**不要用 ComfyUI 的 `python_embeded`**（它 torch 2.8 + transformers 4.x，与上游要求 `torch>=2.10` / `transformers>=5.12` 冲突，硬装会把 ComfyUI 搞坏）。

```
D:\Mau\WorkSpace\irodori-tts\.venv        ← Python 3.12.10
  torch 2.10.0+cu128 / torchaudio 2.10.0+cu128
  transformers 5.20.1 · peft 0.21.2 · accelerate
  dacvae(git facebookresearch) · silentcipher(git SesameAILabs@d46d7d0)
  soundfile 0.14 · sentencepiece 0.2.1 · safetensors 0.8
```

重建要点（踩过的坑都在这里）：

1. `uv sync --extra cu128` 在 Windows 上会卡在下载 managed python / 大轮子超时——**直接用系统 Python 建 venv**，torch 轮子单独下：
   `https://download.pytorch.org/whl/cu128/torch-2.10.0%2Bcu128-cp312-cp312-win_amd64.whl`（2.67 GB）
2. `uv` 默认缓存目录写不进去（`拒绝访问 os error 5`）→ 加 `--cache-dir D:\Mau\WorkSpace\uvcache`，并用 `--link-mode copy`
3. `sentencepiece==0.1.99`（上游锁 `<0.2`）**没有 cp312 轮子**，源码编译缺 cmake 必失败 → 装 0.2.1。代码里没有直接 import 它，不影响
4. `torchcodec` 上游是硬依赖，但 Windows 轮子要 **FFmpeg 共享库**（`avcodec-*.dll`），本机没有 → 见 §4 的 soundfile 补丁；`torchcodec` 装不装都不影响运行
5. git 依赖拉取报 `dubious ownership` → `git config --global --add safe.directory D:/Mau/WorkSpace/uvcache/*`
6. huggingface.co 直连不通 → 全程 `HF_ENDPOINT=https://hf-mirror.com`（worker 里已经 `setdefault` 了）
7. pypi 慢 → 用清华源 `https://pypi.tuna.tsinghua.edu.cn/simple`

## 4. 对上游代码做的补丁（重装后必须重打）

| 文件 | 改了什么 | 为什么 |
| :-- | :-- | :-- |
| `irodori_tts/inference_runtime.py` `_load_audio` / `save_wav` | 优先 `soundfile`，失败才回落 `torchaudio` | torchaudio 2.10 的 `load/save` 走 torchcodec，Windows 缺 FFmpeg 共享库直接 `ImportError` |
| `irodori_tts/codec.py` `encode_file` | 同上 | 同上 |
| `gradio_app.py` | 默认 checkpoint 指向本机小模型、默认 codec 指向本机 `weights.pth` | 免得每次开 UI 都联网下载 410 MB |

其余上游文件未改。上游升级后重打这三处即可。

## 5. ComfyDock 侧结构

```
ComfyDock/Tts/TtsWorkerScript.cs    worker 的 Python 源码（编译进 exe，运行时解到 data\tts\tts_worker.py）
ComfyDock/Tts/IrodoriTtsEngine.cs   常驻 worker 进程管理（参数变更即重载 / 就绪等待 / stderr 尾巴）
ComfyDock/Tts/TtsService.cs         配置落盘 + 磁盘探针 + 跑法解析（TtsPlan）+ 后台合成 + 产物清单
ComfyDock/docs/irodori-tts.md       本文件
```

端点：`GET /api/tts`（快照：env + config + plan + job + engine + refs + outputs）· `GET /api/tts/live`（轻量轮询）· `POST /api/tts/config` · `POST /api/tts/generate` · `POST /api/tts/stop` · `GET /api/tts/audio?name=` · `POST /api/tts/open-out`。

配置落 `data\tts-config.json`；产物落 `out\tts\yoshino_<时间戳>.wav`。
路径可用 `--tts-python` / `--tts-repo` / `--tts-base` / `--tts-large-base` / `--tts-codec` / `--tts-lora` / `--tts-large-lora` / `--tts-refs` / `--tts-out` 覆盖。

**常驻 worker**：基座 + 声码器 + LoRA 只加载一次，之后每句只跑采样。父进程消失（含被强杀）→ worker 读到 stdin EOF 自行退出，不留占显存的僵尸。

### worker 协议（stdin 一行一个 JSON 请求，stdout 一行一个 JSON 事件）

```
请求 {"cmd":"run","id":"..","text":"..","caption":"..","refs":[".."],"noRef":false,
      "steps":40,"seed":2026,"durationScale":1.0,"seconds":null,"useLora":true,"out":"D:\\..\\a.wav"}
     {"cmd":"ping"} / {"cmd":"quit"}
事件 {"ev":"ready","torch":"..","cuda":true,"device":"..","base":"model.safetensors","precision":"bf16","codecDevice":"cpu","lora":"..","seconds":19.2}
     {"ev":"start","id":".."} / {"ev":"done","file":"..","path":"..","audioSeconds":13.08,"sampleRate":48000,"seed":2026} / {"ev":"error","stage":"..","message":"..","traceback":".."}
```

stdout 被协议独占（`os.dup` 保住 fd1，库里的 print 全部改走 stderr）。

## 6. 跑法（怎么个跑法）

面板「参数与出音」顶部下拉，落到 `TtsConfig.RunMode`；`TtsPlan.Resolve` 把它解析成一套生效参数（基座 / LoRA / 精度 / 声码器设备），面板上的路径字段只做**覆盖来源**。

| 跑法 | 基座 | LoRA | 精度 | 声码器 | 适合 |
| :-- | :-- | :-- | :-- | :-- | :-- |
| `small`（默认） | v4.1-Small 2.85 G | small (r16) | bf16 | cuda | 8 G 卡日常，最快 |
| `large` | v4-Large 13.2 G | large (r32) | bf16 | **cpu** | 8 G 卡跑大模型（显存让给模型） |
| `large-gpu` | v4-Large 13.2 G | large (r32) | bf16 | cuda | ≥12 G 显存，解码更快 |
| `custom` | 面板填的「基座模型」 | 面板填的「LoRA 目录」 | 面板选的精度 | 面板选的「声码器设备」 | 换别的权重/实验 |

大模型 3.29B 权重是 **fp32 存盘（13.2 G）**，加载时按 bf16 上卡约 6.6 G；8 G 卡上必须把声码器挪到 CPU，否则采样阶段容易 OOM。

实测（RTX 4060 Laptop 8 G，40 步）：

| 跑法 | 引擎就绪 | 单句合成 | 备注 |
| :-- | --: | --: | :-- |
| small | 19.2 s | 19.8 s（出 13.08 s 音频） | bf16，声码器在 GPU |
| large | （待填） | （待填） | bf16，声码器在 CPU |

## 7. 排障

| 现象 | 原因 / 处理 |
| :-- | :-- |
| `TextEncodeInput must be Union[TextInputSequence, Tuple[...]]` | 子进程 stdin 编码不对，日文被解成代理字符（`\udcab`）。worker 已 `sys.stdin.reconfigure(utf-8)` + 文本清洗 + 协议行 ascii 兜底；引擎侧 `StandardInputEncoding=UTF8` + `PYTHONIOENCODING/PYTHONUTF8` |
| 引擎 5 分钟没就绪 | 看 `data\tts\tts_worker.py` 直接跑一遍；常见是基座路径不对或显存不够（大模型 + 声码器在 GPU 时） |
| 采样阶段 CUDA OOM | 大模型换 `large`（声码器挪 CPU）、减参考音频段数、或降 `--max-ref-seconds` |
| 水印加载失败（`SilentCipher model could not be loaded`） | 只是水印没打，音频照出；权重缓存在 `C:\Users\<用户>\.cache\huggingface`，走 hf-mirror 下载 |
| 面板显示「基座模型不在位」 | 看「环境与依赖」里的生效行——探针按**跑法解析后的路径**查，不是面板上那三个输入框 |
| 首句慢 | 正常：基座 2.85 G（大模型 13.2 G）+ 声码器 0.4 G 从磁盘读进来 |

## 8. 想改功能从哪下手

- **换音色 / 换模型**：`TtsPlan.Resolve` 加一个跑法分支 + `TtsConfig` 加路径字段 + 面板下拉加一项，别动 worker
- **加生成参数**（CFG 尺度、sway、多候选）：`TtsWorkerScript.cs` 的 `SamplingRequest` 构造 + `TtsConfig` + 面板 → 三处对齐
- **并行的另一个生成器**（别的 TTS / 别的模型）：照 `ComfyDock/Tts/` 三件套复制一份，`LOCAL_GENS` 加一项 + 一个面板，`Program.cs` 加一组端点
- **改上游推理行为**：改 `D:\Mau\WorkSpace\irodori-tts\irodori_tts\*`，注意 §4 的补丁别被覆盖
