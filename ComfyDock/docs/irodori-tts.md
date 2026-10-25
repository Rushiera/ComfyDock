# 本地生成 · IrodoriTTS（Irodori-TTS）

> 这份文档是这一路的**来源说明书**：上游是谁、模型哪来的、环境怎么装的、改了上游什么、面板怎么跑、每个参数什么效果。
> 以后要动这个功能，先读这里，别凭印象改。

> **命名口径**：面板页签名固定为 **IrodoriTTS**（引擎/框架名），不绑死某个角色。角色由「跑法 + LoRA 目录 + 参考音频」三样决定——后续加别的角色模型，只需加一个 LoRA 目录（必要时再加一个跑法），页签名不动。

## 1. 这是什么

面板「本地生成」页签下的第二个生成器（`LOCAL_GENS` 里的 `tts`）。用 [Aratako/Irodori-TTS](https://github.com/Aratako/Irodori-TTS) 做日语 TTS：RF-DiT（整流流扩散 Transformer）+ DACVAE 连续隐变量，48 kHz 单声道输出，支持零样本音色克隆（参考音频）、caption 风格控制、自动时长预测。

当前挂的是「依田芳乃」的角色 LoRA——权重来自用户提供的 `yoshino_lora` 包（**只含 LoRA，不含基座**）。换角色 = 换一个 LoRA 目录 + 换参考音频，面板页签与引擎都不动。

## 2. 上游与模型清单（全部住在 ComfyUI 目录下）

**布局总览**

```
D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\
├─ python_embeded\                          ← 直接用它的解释器（见 §3）
└─ ComfyUI\
   ├─ irodori-tts\                          ← 上游代码 + 本项目的启动脚本（§5）
   └─ models\TTS\yoshino\                   ← 依田芳乃全套权重
      ├─ base-v4.1-Small\      model.safetensors + tokenizer/   （小模型基座 2.85 G）
      ├─ base-v4-Large\        model.safetensors + tokenizer/   （大模型基座 bf16 12.25 G）
      ├─ base-v4-Large-int8\int8-weight-only\model.safetensors + tokenizer/（int8 3.58 G）
      ├─ codec-dacvae-32dim\   weights.pth                      （声码器 410 MB）
      ├─ lora-small\           adapter_config + adapter_model   （rank 16 / alpha 32 / 500 步）
      ├─ lora-large\           adapter_config + adapter_model   （rank 32 / alpha 64 / 400 步）
      ├─ references\           3 段芳乃参考音频 mp3（训练时用的那三段）
      ├─ retrieval_manifest.json（原始包清单：文件大小 + SHA-256 + 训练步数）
      └─ _package-README.txt / _package-原始说明（原始包自带的说明，留档）
```

| 项目 | 来源 | 本机落点 |
| :-- | :-- | :-- |
| 推理/训练代码 | `https://github.com/Aratako/Irodori-TTS`（`main`，取回时 HEAD `89f9d8f`） | `ComfyUI\irodori-tts` |
| 小模型基座 | `Aratako/Irodori-TTS-v4.1-Small`（500M，2.85 GB + `tokenizer/`） | `ComfyUI\models\TTS\yoshino\base-v4.1-Small` |
| 大模型基座 | `Aratako/Irodori-TTS-v4-Large`（3.29B，fp32 12.25 GB + `tokenizer/`） | `...\base-v4-Large` |
| 大模型 int8 | `Aratako/Irodori-TTS-v4-Large-Quantized` 的 `int8-weight-only/`（W8A16，3.58 GB；**需要 torchao**） | `...\base-v4-Large-int8\int8-weight-only`（`tokenizer/` 复制自 v4-Large 目录——上游该仓库只有根级 tokenizer，运行时要与 safetensors 同目录） |
| 声码器 | `Aratako/Semantic-DACVAE-Japanese-32dim`（`weights.pth` 410 MB） | `...\codec-dacvae-32dim` |
| 小模型 LoRA | 用户包 `yoshino_lora\small`（rank 16 / alpha 32 / 500 步，PEFT 0.18.1 训练） | `...\lora-small` |
| 大模型 LoRA | 用户包 `yoshino_lora\large`（rank 32 / alpha 64 / 400 步） | `...\lora-large` |
| 参考音频 | 原始包 `small\voice_profile.json` 里记录的链接（biligame 立绘语音 mp3） | `...\references` |

LoRA 适配器只带 `adapter_config.json` + `adapter_model.safetensors`（`modules_to_save` 含 `duration_predictor`，`target_modules` 是 DiT attention 的正则），**必须配对应基座**：small↔v4.1-Small、large↔v4-Large，不能交叉。

## 3. 环境（直接用 ComfyUI 的 python_embeded——**不装独立运行时**）

从 v0.35 起这一路**不随软件带 venv**，直接用本机 ComfyUI 的 `python_embeded`。它的现有依赖已经够用（2026-10-18 实测）：

```
D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\python_embeded\python.exe   ← Python 3.12
  torch 2.10.0+cu130 · torchaudio 2.10.0+cu130（CUDA 可用）
  transformers 5.20.1 · peft 0.21.2 · huggingface_hub 2.2.0
  dacvae（facebookresearch）· silentcipher · soundfile 0.13 · safetensors 0.8 · gradio 5.44
  torchao 0.16.0（2026-10-18 由本轮补装——仅 int8 跑法需要）
```

上游 `pyproject.toml` 要求的 `torch>=2.10` / `transformers>=5.12` / `peft>=0.18` 在这个环境里全部满足；`torchcodec` 没有也不需要（见 §4 的 soundfile 补丁）。

**重装 / 升级 ComfyUI 后要补的只有一件事**：`pip install torchao==0.16.0`（只有 `large-int8` 跑法会用到；不装则报 `This checkpoint uses torchao quantization`）。装法：

```
D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\python_embeded\python.exe -m pip install torchao==0.16.0
```

早期用独立 venv 时踩过的坑（`uv sync --extra cu128` 卡 managed python、`sentencepiece==0.1.99` 无 cp312 轮子、uv 缓存权限、git dubious ownership、hf-mirror）现在**都不需要再处理**——ComfyUI 环境里这些都是现成的。若哪天真要重建独立环境，照这些坑的顺序走。

## 4. 对上游代码做的补丁（重装后必须重打）

以下三处补丁在 `ComfyUI\irodori-tts\` 里，**上游代码升级 / 重新 clone 后必须重打**：

| 文件 | 改了什么 | 为什么 |
| :-- | :-- | :-- |
| `irodori_tts/inference_runtime.py` `_load_audio` / `save_wav` | 优先 `soundfile`，失败才回落 `torchaudio` | torchaudio 2.10 的 `load/save` 走 torchcodec，Windows 缺 FFmpeg 共享库直接 `ImportError`。**本次迁到 ComfyUI 环境后仍然需要**（该环境同样没有 torchcodec） |
| `irodori_tts/codec.py` `encode_file` | 同上 | 同上 |
| `gradio_app.py` | 默认 checkpoint / codec 指向 `ComfyUI\models\TTS\yoshino\` 下的本地权重 | 免得每次开 UI 都联网下载 410 MB |

另外两个本地启动脚本（非上游文件，重装时会被覆盖，需保留副本）：`yoshino_say.py` / `yoshino_say.bat`（命令行合成，已指向 `python_embeded`）、`yoshino_ui.bat`（起 Gradio，同样指向 `python_embeded`）。

## 5. ComfyDock 侧结构

```
ComfyDock/Tts/TtsWorkerScript.cs    worker 的 Python 源码（编译进 exe，运行时解到 data\tts\tts_worker.py；用 ComfyUI 的 python_embeded 跑）
ComfyDock/Tts/IrodoriTtsEngine.cs   常驻 worker 进程管理（参数变更即重载 / 就绪等待 / stderr 尾巴）
ComfyDock/Tts/TtsService.cs         配置落盘 + 磁盘探针 + 跑法解析（TtsPlan）+ 后台合成 + 产物清单
ComfyDock/docs/irodori-tts.md       本文件
```

端点：`GET /api/tts`（快照：env + config + plan + job + engine + refs + outputs）· `GET /api/tts/live`（轻量轮询）· `POST /api/tts/config` · `POST /api/tts/generate` · `POST /api/tts/stop` · `GET /api/tts/audio?name=` · `POST /api/tts/open-out`。

配置落 `data\tts-config.json`；产物落 `out\tts\yoshino_<时间戳>.wav`。
路径可用 `--tts-python` / `--tts-repo` / `--tts-base` / `--tts-large-base` / `--tts-large-int8-base` / `--tts-codec` / `--tts-lora` / `--tts-large-lora` / `--tts-refs` / `--tts-out` 覆盖。

**运行时不随软件走**：解释器 = ComfyUI 的 `python_embeded`，代码 = `ComfyUI\irodori-tts`，权重 = `ComfyUI\models\TTS\yoshino`。ComfyDock 侧只留「进程管理 + 配置 + 面板」三件套；软件整体搬目录时只要面板里的路径指得对就照常工作。

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
| `small`（默认）·「依田芳乃 · v4.1-Small」 | v4.1-Small 2.85 G | lora-small (r16) | bf16 | cuda | 8 G 卡日常，最快 |
| `large-int8` ·「依田芳乃 · v4-Large int8」 | v4-Large **int8** 3.58 G | lora-large (r32) | bf16 | cuda | **8 G 卡跑大模型**（权重省 4 倍，实测可用） |
| `large` ·「依田芳乃 · v4-Large bf16 · 声码器在 CPU」 | v4-Large 12.25 G | lora-large (r32) | bf16 | **cpu** | 显存吃紧时的退路（详见下） |
| `large-gpu` ·「依田芳乃 · v4-Large bf16 · 全 GPU」 | v4-Large 12.25 G | lora-large (r32) | bf16 | cuda | ≥12 G 显存，解码更快 |
| `custom` | 面板填的「基座模型」 | 面板填的「LoRA 目录」 | 面板选的精度 | 面板选的「声码器设备」 | 换别的角色 / 别的权重 / 实验 |

**四个内置档都是依田芳乃**（同一套角色 LoRA，区别只在基座大小与放置方式）；页签叫 IrodoriTTS 是引擎名，角色靠 LoRA + 参考音频决定。

大模型 3.29B 权重是 **fp32 存盘（12.25 G）**，加载时按 bf16 上卡约 6.6 G——8 G 卡上把声码器挪到 CPU 仍会在采样阶段吃满显存（实测一台 4060 Laptop 8 G：采样时 `memory.used` 顶到 7859/8188 MiB、GPU 100%、单句 2 分钟以上才出 15.6 s 音频，且 `nvidia-smi` 一度报出异常显存读数）。**8 G 卡请用 `large-int8`**。

实测（RTX 4060 Laptop 8 G，40 步，文本「こんにちは、わたくしは依田芳乃と申します。今は大モデルの…で喋っております。」）：

| 跑法 | 引擎就绪 | 单句合成 | 产物 | 备注 |
| :-- | --: | --: | :-- | :-- |
| small | 19.2 s | 19.8 s | 13.08 s / 48 kHz 单声道 | bf16，声码器在 GPU |
| large-int8 | 48.4 s / 52.7 s / 47.8 s | 38.3 s / 30.4 s / 32.2 s | 15.6 s / 48 kHz 单声道 | bf16 + int8 权重，声码器在 GPU；峰值显存 ~7.7 G |
| large | 70.6 s | >120 s（中止） | —（中断） | bf16，声码器在 CPU，8 G 卡显存仍不够 |

`large-int8` 三次跑同一请求（三种不同的引擎重启组合），产物 **SHA-256 完全一致**（`8d6bc8df…5b530b`）——量化路径是确定性的，可复现。

## 7. 参数说明（每个选项是什么效果 / 什么时候该改）

> 面板「参数与出音」里每一项的口径都在这里。写改动前先看这一节，别凭印象加参数。

### 7.1 跑法

一次定死四件事：**基座 + LoRA + 精度 + 声码器放哪**。它不是四个独立开关——面板上「精度」「LoRA 目录」「声码器设备」这些字段在非 `custom` 跑法下**只显示不生效**，真正的来源是跑法。

| 跑法 | 基座 | LoRA | 精度 | 声码器 | 8 G 卡实测 | 什么时候选 |
| :-- | :-- | :-- | :-- | :-- | :-- | :-- |
| `small` | v4.1-Small 2.85 G | small (r16) | bf16 | cuda | 首句 19.2 s / 单句 19.8 s | 日常默认。500M 模型，显存 4~5 G，质量够用 |
| `large-int8` | v4-Large int8 3.58 G | large (r32) | bf16（固定） | cuda | 首句 47.8~52.7 s / 单句 30.4~38.3 s | **8 G 卡要质量就选它**。3.29B 压到 int8，峰值显存约 7.7 G |
| `large` | v4-Large 12.25 G | large (r32) | bf16 | cpu | 首句 70.6 s / 单句 >120 s（中止） | 显存紧张的退路。8 G 卡实测会把显存吃到 7859/8188 MiB 且迟迟不出音，**不建议** |
| `large-gpu` | v4-Large 12.25 G | large (r32) | bf16 | cuda | —（无 ≥12 G 卡可测） | ≥12 G 显存，bf16 全 GPU 解码最快 |
| `custom` | 面板「基座模型」 | 面板「LoRA 目录」 | 面板「精度」 | 面板「声码器设备」 | — | 换别的权重 / 做实验。**只有这一档会让那几个字段生效** |

切换跑法会**立即落盘并重启常驻引擎**（引擎按「基座 + LoRA + 精度 + 声码器设备」判断要不要重载），所以切一次要等一次加载。

### 7.2 文本

必填，日语。标点保留（模型按标点断句、按时长预测估总长），一行一句最稳。假名 / 汉字混写都行，读法由模型自己判——生僻人名地名容易读错，写成假名可强制读音。

**emoji 内联控制**：把特定 emoji 插进文本里可以让模型在该处改语气或加非语言音（笑い / 吐息 / 囁き / 泣き声 / 早口 / 電話越し 等，共 48 个，表在 `irodori_tts/gradio_emoji_palette.py` 的 `EMOJI_PALETTE_ITEMS`）。面板文本框下的 emoji 行就是这张表的快捷插入。

### 7.3 风格描述（caption）

**它是自由文本，不是下拉选项**——理由是模型这一路本身就是 caption 条件：训练时喂的是自然语言描述（「穏やかで柔らかい女性の声。ゆったりと…」），推理时也是拿这句话当条件去引导生成。给一个固定枚举反而会把模型的可用空间切窄，而且新写法（新的情绪、新的距离感）永远得先改代码才能试。

参考音频与风格描述的分工：

| 条件 | 管什么 | 换它等于 |
| :-- | :-- | :-- |
| 参考音频 | **谁在说**（音色 / 说话人身份） | 换角色 |
| 风格描述 | **怎么说**（嗓音质地 / 语速 / 情绪 / 距离感） | 换语气 |
| 文本 | **说什么** + 局部非语言音（emoji） | 换台词 |

两者冲突时音色归参考音频、语气归风格描述。留空 = 只用参考音频自带的语气。

面板下方那排 chip 是**常用写法模板**（点了填进框，可再改、已有内容会追加不是覆盖）。当前 10 条：

| 模板 | 完整文本 |
| :-- | :-- |
| 穏やかで柔らかい女性 | 穏やかで柔らかい女性の声。ゆったりと、語尾をやさしく伸ばして話す。 |
| 落ち着いた高めの女性 | 落ち着いた高めの女性の声。自然な間を取り、やわらかく話す。 |
| 明るく元気 | 明るく元気な女性の声。はきはきと、テンポよく話す。 |
| 囁くように | 近い距離感で、囁くように静かに話す。 |
| 照れながら | 少し照れながら、控えめに、自信なさげに話す。 |
| 眠そう | 眠そうで気だるげに、ゆっくりと話す。 |
| 可愛らしい高音 | 可愛らしい高めの女性の声。甘えた口調で話す。 |
| 凛とした大人 | 落ち着いた大人の女性。凛とした口調で、丁寧に話す。 |
| 嬉しそう | 嬉しそうに、楽しげに、声を弾ませて話す。 |
| 寂しげ | 寂しげで、少し悲しげな声で、ぽつりぽつりと話す。 |

写法建议：一条 1~2 句就够；越具体越可控；互相打架的描述（既「囁き」又「元気いっぱい」）会两头不靠。前两条是 `yoshino_lora\small\voice_profile.json` 里训练时实际用的 caption，最稳。

### 7.4 步数

默认 40（官方口径）。收益递减明显：40 以上几乎无差别，只烧时间。降到 6~10 能快，但**这条链路没开 Sway 采样**，单纯降步数就是掉质量（官方那条 6 步的写法要配 `--t-schedule-mode sway`，面板没暴露）。

### 7.5 种子

默认 -1 = 每次随机。固定数字 + 同样参数 = 同样的音频（int8 档实测三次复跑 SHA-256 完全一致）。挑到喜欢的一条，记下种子就能复现；只改一个参数对比效果时尤其有用。注意换跑法 / 换 LoRA / 换参考音频后同种子也会出不同结果。

### 7.6 时长倍率

默认 1.0。v4 系列会自己预测输出长度，这个数在预测结果上再乘一遍：1.1 = 长 10%（更从容），0.9 = 紧 10%。整体语速偏快偏慢时微调，别当「精确时长」用。

### 7.7 强制秒数

默认空 = 自动，这是正常用法：长文本会按句读自动切成若干段（每段约 60 字，按 `。！？…` 与 `、，` 断句；超长段还会按 token 预算再切一次），每段各自按模型预测的时长出声，段间留 0.2 秒静音，最后拼回一条 wav。总时长没有上限，语速也不受压——旧版把整段夹在模型默认的 `max_seconds=30` 里，长句会被压得明显发急（同一段文本：自动档 34.8 秒 / 夹到 30 秒 → 语速 3.2 字每秒，听感发急）。

填了数字 = 整段一次合成、按这个秒数出声（单段上限放宽到 120 秒，不再被 30 秒夹紧），只在「必须卡进某个时长」时用：填得比文本需要的短会把话压扁甚至吞字，填长了尾巴拖静音。文本超过模型单次上限（256 token，约 300+ 字日语）时这一档直接报错并提示改用自动档。

### 7.8 精度

默认 bf16（8 G 卡必选）；fp32 数值最稳但显存翻倍，一般只在排查数值异常时试。**`large-int8` 跑法下这一项被忽略**（权重已 int8，计算固定 bf16）。

### 7.9 挂 LoRA

默认开。走当前跑法对应的角色 LoRA（small 线 = rank 16 / alpha 32 / 500 步；large 线 = rank 32 / alpha 64 / 400 步）。关掉 = 只用基座通用音色，用来对比「LoRA 到底改了多少」。**跑法与 LoRA 必须配对**（small↔small、large↔large），错配会加载失败。

加新角色：新建一个 LoRA 目录（结构同 `yoshino_lora\small`：`adapter_config.json` + `adapter_model.safetensors`），在「环境与依赖」把「LoRA 目录」指过去即可；要和小模型/大模型两套并存，就在 `TtsPlan.Resolve` 里加跑法分支。

### 7.10 参考音频

零样本音色克隆的输入。多选按勾选顺序拼接，**30 秒左右收益最大**（训练时就是随机拼短句，v4-Small 训练上限 120 秒）；只给一段也能用，相似度略低。一段都不勾 = 用目录里全部。

质量要点：干净、单一说话人、无音乐无噪声；参考里的语气会顺带被学走（想换语气用风格描述压）。目录里的音频要 mp3 / wav / flac 均可（走 soundfile 解码，见 §4）。

### 7.11 停止引擎

常驻引擎一直占显存（small 约 4~5 G，int8 大模型约 7.7 G）。要跑 ComfyUI 出图、或今天不再合成，点它把显存还回去；下次「生成」自动重新加载（small 约 20 秒、int8 约 50 秒）。

## 8. 训练 / 后处理 / 换角色：该看哪些东西

权重和代码都在 `ComfyUI\irodori-tts\`（代码）与 `ComfyUI\models\TTS\yoshino\`（权重）下，训练与转换脚本**上游全带**，不用另外找。

### 8.1 想再训一个角色 LoRA（最常见）

| 要做的事 | 看这个 | 说明 |
| :-- | :-- | :-- |
| 总流程 | `irodori-tts\README.md` §Training | 「Prepare Manifest → LoRA Fine-Tuning → Convert」三段 |
| 训练配置模板 | `irodori-tts\configs\train_v4_small_lora.yaml` | 照抄改 `lora_r` / `lora_alpha` / `learning_rate` / `max_steps`；本次依田芳乃 small 是 rank 16 / alpha 32 / 500 步 |
| 大模型 LoRA | `irodori-tts\configs\train_v4_large.yaml` + `train_v4_small_lora.yaml` | large 那份是 LoRA rank 32 / alpha 64 / 400 步的来源 |
| 训练数据准备 | `irodori-tts\prepare_manifest.py` | 把音频 → DACVAE latent + JSONL manifest；参数见 `docs/parameters.md` §训练 |
| 全部参数逐条口径 | `irodori-tts\docs\parameters.md` | 模型 / 训练 / 推理三段全量英文说明，**最权威的参数手册** |
| 训完转推理格式 | `irodori-tts\convert_checkpoint_to_safetensors.py` | LoRA checkpoint 目录可直接转（会自动合并进基座），也可不合并、直接给 `--lora-adapter` |
| 参考 caption 怎么写 | `ComfyUI\models\TTS\yoshino\lora-small\voice_profile.json` | 本次训练实际用的 3 段参考音频 + 各自的 caption 原文（前两条就是面板 chip 里的前两条） |
| 训练数据来源清单 | `ComfyUI\models\TTS\yoshino\retrieval_manifest.json` | 每份权重的字节数与 SHA-256、训练步数、基座 checkpoint 路径 |
| 本次训练的选择依据 | `lora-small\selection.json` / `lora-large\selection.json` | 为什么选第 500 步 / 第 400 步：验证损失、说话人相似度、假名 CER；两份都写着 `subjective_naturalness_verified: false` |

训练需要的额外依赖（上游 `pyproject.toml` 里 `datasets` / `wandb` / `torchdata`）ComfyUI 环境里已具备；DDP 多卡用 `torchrun`，单卡直接 `python train.py`。

### 8.2 想加一个新的角色模型（不重训，直接用别人的 LoRA）

1. 拿一个 PEFT 适配器目录（`adapter_config.json` + `adapter_model.safetensors`），放到 `ComfyUI\models\TTS\<角色名>\lora-small\`（结构照 `yoshino\lora-small`）
2. 参考音频放 `ComfyUI\models\TTS\<角色名>\references\`
3. ComfyDock 面板「环境与依赖」里把「LoRA 目录」「参考音频目录」指过去；
4. 要和小模型/大模型两套并存，就在 `TtsPlan.Resolve` 加一个跑法分支 + `TtsConfig` 加路径字段 + 面板下拉加一项（见 §10）

`base_model_name_or_path` 在 `adapter_config.json` 里是 `null`，所以不校验基座路径——靠「跑法与 LoRA 配对」这条纪律保证不错配。

### 8.3 其他后处理

| 想做的事 | 脚本 / 文档 |
| :-- | :-- |
| 量化成 int8 / int4 / fp8 | `irodori-tts\quantize_checkpoint.py`；现成的量化版也可直接从 `Aratako/Irodori-TTS-v4-Large-Quantized` 取 |
| 蒸馏成少步数（MeanFlow） | `irodori-tts\docs\meanflow.md` + `configs/train_v4_small_meanflow.yaml` |
| Speaker Inversion（学一个可复用的说话人嵌入，推理时不用参考音频） | `irodori-tts\README.md` §Speaker Inversion + `configs/train_v4_small_speaker_inversion.yaml`；推理用 `--ref-embed` |
| 全模型微调（不是 LoRA） | `configs/train_v4_small.yaml`，`--init-checkpoint` 指向基座 safetensors |
| emoji 内联控制（哪些 emoji 有效） | `irodori-tts\irodori_tts\gradio_emoji_palette.py` 的 `EMOJI_PALETTE_ITEMS`（48 条：謎囁き / 吐息 / 笑い / 泣き声 / 早口 / 電話越し …） |

## 9. 排障

| 现象 | 原因 / 处理 |
| :-- | :-- |
| `TextEncodeInput must be Union[TextInputSequence, Tuple[...]]` | 子进程 stdin 编码不对，日文被解成代理字符（`\udcab`）。worker 已 `sys.stdin.reconfigure(utf-8)` + 文本清洗 + 协议行 ascii 兜底；引擎侧 `StandardInputEncoding=UTF8` + `PYTHONIOENCODING/PYTHONUTF8` |
| 引擎 5 分钟没就绪 | 看 `data\tts\tts_worker.py` 直接跑一遍；常见是基座路径不对或显存不够（大模型 + 声码器在 GPU 时） |
| 采样阶段 CUDA OOM | 大模型换 `large-int8`（int8 权重 3.58 G，8 G 卡实测可用）；或减参考音频段数、降 `--max-ref-seconds` |
| 水印加载失败（`SilentCipher model could not be loaded`） | 只是水印没打，音频照出；权重缓存在 `C:\Users\<用户>\.cache\huggingface`，走 hf-mirror 下载 |
| 面板显示「基座模型不在位」 | 看「环境与依赖」里的生效行——探针按**跑法解析后的路径**查，不是面板上那三个输入框 |
| 首句慢 | 正常：基座 2.85 G（大模型 12.25 G / int8 3.58 G）+ 声码器 0.4 G 从磁盘读进来 |
| `This checkpoint uses torchao quantization` | python_embeded 里没装 torchao → `python_embeded\python.exe -m pip install torchao==0.16.0`（见 §3） |
| 大模型跑得像卡死（GPU 100% 但迟迟不出音） | 8 G 卡跑 bf16 大模型会显存抖动 → 换 `large-int8` 跑法 |
| `ModuleNotFoundError: dacvae / silentcipher / peft` | ComfyUI 环境被降级或重装 → 装回上游要求（`dacvae @ git+https://github.com/facebookresearch/dacvae`、`silentcipher`、`peft>=0.18`） |
| `ModuleNotFoundError: soundfile` | 同上，`pip install soundfile`（§4 的补丁依赖它） |

## 10. 想改功能从哪下手

- **换音色 / 加角色**：新建一个 LoRA 目录（`adapter_config.json` + `adapter_model.safetensors`）→「环境与依赖」把「LoRA 目录」指过去即可；要和小模型/大模型两套并存，就在 `TtsPlan.Resolve` 加一个跑法分支 + `TtsConfig` 加路径字段 + 面板下拉加一项
- **加生成参数**（CFG 尺度、sway、多候选、`--max-ref-seconds`）：`TtsWorkerScript.cs` 的 `SamplingRequest` 构造 + `TtsConfig` + 面板 → 三处对齐；加完在 §7 补一条口径
- **加风格描述模板**：`index.html` 的 `TTS_CAPTION_PRESETS` 数组（文案 + 完整 description），同步在 §7.3 补一行
- **并行的另一个生成器**（别的 TTS / 别的模型）：照 `ComfyDock/Tts/` 三件套复制一份，`LOCAL_GENS` 加一项 + 一个面板，`Program.cs` 加一组端点
- **改上游推理行为**：改 `D:\done\ComfyUI_Wan22_Ultra_GGUFv2.33\ComfyUI\irodori-tts\irodori_tts\*`，注意 §4 的补丁别被覆盖；改完不用重装，worker 每次启引擎重新 import

## 11. 本功能的文档地图（改之前先看这里）

| 想改动 | 文件 |
| :-- | :-- |
| 跑法 / 生效参数解析 | `ComfyDock/Tts/TtsService.cs`（`TtsConfig` + `TtsPlan.Resolve`） |
| 面板「参数与出音」「环境与依赖」 | `ComfyDock/Web/wwwroot/index.html`（`genPanelTts` 段 + TTS JS 块） |
| 风格描述模板（chip） | `index.html` 的 `TTS_CAPTION_PRESETS`（与 §7.3 表同步） |
| 默认路径 / 命令行开关 | `ComfyDock/AppOptions.cs`（`Tts*` 字段 + `--tts-*`） |
| 常驻进程与行协议（C# 侧） | `ComfyDock/Tts/IrodoriTtsEngine.cs` |
| worker（Python 侧，编译进 exe） | `ComfyDock/Tts/TtsWorkerScript.cs` |
| 端点装配 | `ComfyDock/Program.cs`（`/api/tts*` 一组） |
| 上游代码 | `ComfyUI\irodori-tts\`（`README.md` / `docs/parameters.md` / `docs/meanflow.md`） |
| 权重与训练资料 | `ComfyUI\models\TTS\yoshino\`（含 `retrieval_manifest.json`、`_package-原始说明`、各 `selection.json`） |
| emoji 内联控制表 | `ComfyUI\irodori-tts\irodori_tts\gradio_emoji_palette.py`（`EMOJI_PALETTE_ITEMS`，48 条） |
