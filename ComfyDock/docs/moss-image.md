# MOSS-Image — 说明书（文生图 / 图生图 / 图像编辑）

> 本地生成板块第三个生成器（MOSS-Image）的唯一说明书。
> 两条推理通道（引擎在跑走 HTTP、没跑走内置 worker）、两套模型（单文件三件套 / diffusers 目录）、两种玩法（文生图 · 图像编辑）。
> 本文讲**怎么用**与**为什么这样用**：落点 / 参数逐条 / 实测数据 / 排障 / 改功能入手点。
> 参数细节的官方来源：`OpenMOSS-Team/MOSS-Image-SFT`（底模）、`Comfy-Org/MOSS-Image`（单文件权重）、`Chenkin-x/ComfyUI-Moss-Image-Edit`（图像编辑插件）。

---

## 1. 两条通道，面板自动选

```
面板「参数与出图」点「生成」
  → 探一次 127.0.0.1:8188
      ├─ 引擎在跑 → HTTP 通道：POST /prompt → 轮询 /history → GET /view 收图
      └─ 引擎没跑 → 内置通道：起常驻 worker（python_embeded），PromptExecutor 在进程内跑同一份节点图
  → 产物落 out\moss\<种子>[_序号].png
```

| 面 | HTTP 通道 | 内置通道 |
|:--|:--|:--|
| 前提 | ComfyUI 服务在跑（8188） | 只要 ComfyUI 目录在（不启服务、不开端口） |
| 首次耗时 | 引擎已加载模型，直接采样 | 起 worker 约 9–10 秒 + 首次加载模型 3–5 分钟 |
| 之后每张 | 采样时间 | 采样时间（模型缓存着，但每次生成仍要 CPU↔GPU 搬运 27 G） |
| 8 步加速叠编辑 | ❌ 环境变量进不去 | ✅ 唯一支持 |

回执会写明走的是哪条（「已提交」= HTTP，「已交给内置引擎」= 内置）。

---

## 2. 落点

| 项 | 位置 | 说明 |
|:--|:--|:--|
| 依赖索引 | `ComfyDock/Moss/MossCatalog.cs` | 软件内唯一真相：要哪些文件 / 多大 / 从哪下 / 落哪；方案与显存推荐也在这里 |
| 下载器 | `ComfyDock/Moss/MossDownloader.cs` | 单线程串行、`.part` 断点续传、落盘字节对账 |
| 环境探针 | `ComfyDock/Moss/MossEnv.cs` | 内核 / 插件 / 显卡 / 文件在位 / 阻塞项 / `canGenerate` / `canEdit` |
| 插件安装 | `ComfyDock/Moss/MossPlugin.cs` | 主插件（moss-image）+ 编辑插件（ComfyUI-Moss-Image-Edit）+ 两条补丁 + 依赖安装 |
| 节点图 | `ComfyDock/Moss/MossRunner.cs` | `BuildGraph`（文生图 / 图生图）· `BuildEditGraph`（图像编辑） |
| 服务面 | `ComfyDock/Moss/MossService.cs` | 配置 / 提交 / 轮询 / 收图 / 任务状态 |
| 内置引擎 | `ComfyDock/Moss/MossLocalEngine.cs` + `MossWorkerScript.cs` | 起停 worker、行协议、产物回收、额外 LoRA 环境变量 |
| 面板 | `ComfyDock/Web/wwwroot/index.html`（`#genPanelMoss`） | 独占面：仓库根 `Web\wwwroot` 是发布副本，改了不生效 |
| 产物 | `out\moss\` | 文件名 = 种子 |
| 配置 | `data\moss-config.json` | 全部参数 + 参考图路径 |

**模型落点**（与 ComfyUI 共用一份，软件不复制不搬家）：

| 方案 | 文件 | 磁盘 | 峰值显存 |
|:--|:--|--:|--:|
| `int8`（默认推荐） | `models\diffusion_models\moss_image_sft_int8_convrot.safetensors` + `text_encoders\qwen3.5_9b_int8_convrot.safetensors` + `vae\flux2-vae.safetensors` | ≈15.5 G | ≈8.7 G |
| `bf16` | 主模型换 bf16 版，其余同上 | ≈20.3 G | ≈15 G |
| `base` | `moss_image_base_int8_convrot.safetensors` + 同上 | ≈15.5 G | ≈8.7 G |
| `diffusers` | `models\MOSS-Image\MOSS-Image-SFT\`（transformer / text_encoder / tokenizer / vae / scheduler / pipeline） | ≈27 G | ≈15 G |
| `accel` | `models\loras\MOSS\MOSS-SFT-8step-DMD.safetensors`（1.19 G，任何方案都能加） | ≈1.2 G | — |
| `edit` | diffusers 全量 + `models\Moss-Image-Edit\`（编辑 LoRA 284 MB） | ≈27.3 G | ≈7.1 G（实测） |

---

## 3. 第一次用：按这个顺序

1. **「环境与依赖」页**——看环境行：内核是否带 Flux.2、插件是否已加载、显卡与盘余。
2. **缺文件就下**：在「依赖」块点「下载本方案」（推荐 `int8`）。面板逐条列在位 / 缺，缺的才有「下载」按钮。
3. **插件**：点「安装插件」（装 `custom_nodes\moss-image` + 自动打 qkv 拆分补丁）。装完**重启 ComfyUI** 才会被加载。
4. **回来点「重新检测」**——确认「MOSS 插件：已加载」。
5. **回到「参数与出图」**——填提示词，点「生成」。

**不用开 ComfyUI 也能出图**（内置通道），但第一次仍建议先按上面顺序把插件与模型装齐。

---

## 4. 文生图：参数逐条

| 参数 | 默认 | 怎么用 |
|:--|--:|:--|
| 提示词 | 空 | 自然语言 / Danbooru 标签串 / 官方 Layout JSON 都能直接粘。写法：主体 + 动作 + 场景 + 光线 + 画风；堆太多互相冲突的描述会打架 |
| 负向提示词 | 空 | 官方推荐留空；**CFG = 1（8 步加速）时完全不生效** |
| 方案 | `int8` | 换方案要先把对应文件下齐（没下齐面板红着说话） |
| 显存模式 | 沿用引擎当前 | **这是引擎启动参数，不是模型参数**——改完要到「环境与依赖」点「按显存模式重启引擎」 |
| 宽 / 高 | 1024×1024 | 必须 16 的倍数（面板自动对齐，填 1000 → 992）。显存与耗时随**面积**涨；8 G 卡建议 ≤1024² |
| 步数 | 50 | 30 步以内提升快，40 与 50 几乎无差；**开 8 步加速就填 8** |
| CFG | 3 | 调高更贴提示词但易僵、颜色发死；调低更自由但可能不听指令；**8 步加速必须 1** |
| Shift | 5 | Flow 调度的步数分配，**最不该乱动**；调大构图更规整、边缘更硬（过头死黑发闷），调小更柔（过头结构松散）。它与 SLG 阈值共用，改它等于连带改 SLG |
| 种子 | -1（随机） | 填数字 = 同种子同图；产物文件名就是种子，看到喜欢的记文件名即可复现 |
| 批次 | 1 | 一次出几张（同一次采样出多张）；8 G 卡建议 1–2 |
| LoRA / 权重 | 不用 / 1.0 | 挂自己的风格或角色 LoRA；0.5–0.8 更轻，1.5–2 更浓（过头糊 / 崩坏） |
| 8 步加速（DMD） | 关 | 挂蒸馏 LoRA，50 步 → 8 步；**必须配套步数 8、CFG 1**；只配 SFT（含 int8），配 base 出色块 |
| 结构增强（SLG） | 开 | 负向分支跳过第 22 层，手与结构更稳，代价是慢一点；一般不动 |
| Hires 第二遍 | 关 | 先出小图 → Lanczos 像素放大 → 低重绘幅度再采样一遍。MOSS 的 latent 是 128 通道，**只能像素放大**；倍率 1.5 / 重绘 0.5 起。代价：总耗时 ≈ 两遍 |

**常用配方**

| 要什么 | 怎么填 |
|:--|:--|
| 官方质量档 | 方案 int8 · 1024×1024 · 步数 50 · CFG 3 · Shift 5 · SLG 开 |
| 快速试稿 | 方案 int8 · 512×512 · 8 步加速开 · 步数 8 · CFG 1 |
| 竖图 / 横图 | 1024×1536 / 1536×1024（宽高不必相等） |
| 更清晰 | Hires 开 · 倍率 1.5 · 重绘 0.5（时间按分钟算） |

**图生图**（当前无面板 UI）：配置里 `InputImage` 填本地图片完整路径、`ImgDenoise` 给重绘幅度（默认 0.6），走「输入图片 → VAE 编码 → KSampler（denoise < 1）」。节点图已支持，面板控件未做——需要时用配置文件或 API。

---

## 5. 图像编辑：给参考图 + 一句指令改图

**一句话：四格里放了参考图就是图像编辑，空着就是文生图——不用选模式。**

### 5.1 它和文生图的区别（决定你能怎么用）

编辑不是「在主模型上挂个 LoRA」，插件的做法是把**参考图编码成 latent，再以 KV 的形式插进注意力**：

```
参考图 × N → VAE 编码 → video_embedder → RoPE 时间轴打上 t = 图序号(1..4)
   → 所有参考 token 拼成前缀，在 t=0 做一次前向，逐层存下 (k, v) 缓存
   → body（文本 + 目标图）的 k/v 拼成 [text, ref1…refN, target] 再算注意力
```

由此得出三条硬边界：

1. **只吃 diffusers 目录**（编辑方案 = 27.3 G）——单文件主模型没有这条通路，别想着把编辑 LoRA 转成 ComfyUI 格式挂上去
2. **参考图是「条件」不是「底图」**——输出尺寸与参考图尺寸无关（输出按宽 / 高走，参考图另按「参考长边」缩）
3. **参考图的 KV 只算一次、跨采样步复用**——多一张图只多一次前缀前向，不按步数乘

### 5.2 第一次用编辑：四步

1. 「环境与依赖」页 → **装编辑插件**（`Chenkin-x/ComfyUI-Moss-Image-Edit`，装完自动打「DMD 并入」补丁）
2. 同页 → **装编辑依赖**（`diffusers≥0.38` / `accelerate≥1.13` / `optimum-quanto==0.2.7`；**不动 transformers**——插件 requirements 钉 5.5.3，本机 5.20.1，降级会砸音频链）
3. 「依赖」块 → 下 `edit` 方案（27.3 G：diffusers 全量 + 编辑 LoRA 284 MB）
4. 回「参数与出图」→ 四格参考图点第一格选图 → 填提示词 → 生成

**前置判据**（面板「环境与依赖」逐条列，缺什么红着说话）：内核带 Flux.2 · 编辑插件已加载 · diffusers 底模在位（`pipeline_moss_image.py` + `scheduler/`）· 编辑 LoRA 两件在位。四项齐了 `canEdit = true`。

### 5.3 四格参考图

| 行为 | 结果 |
|:--|:--|
| 点整格 | 弹系统对话框选图 → 显示缩略图 |
| 鼠标移到已选的格子 | 右上角出现 **×**，点它清空这一格 |
| 放 1–4 张 | 顺序即编号：第 1 格 = image 1，第 2 格 = image 2……中间空着不影响后面编号 |
| 第一次放图 | 自动把文生图默认的 50 步 / CFG 3 切成编辑口径 **30 / 4** |

参考图的处理：**长边缩到「参考长边」（默认 512）→ 16 对齐 → Lanczos → 不裁剪**，各图宽高比可以不同。

### 5.4 提示词怎么写

写「要改什么」，用 `image 1` / `image 2` 指代第几张：

| 想要的效果 | 提示词 |
|:--|:--|
| 换背景、保留人物 | `Change the background to a sunny beach, preserving the person.` |
| 换穿搭 | `Dress the person in image 1 in the outfit from image 2.` |
| 换表情 | `Change her expression to a gentle smile.` |
| 加物体 | `Put a red umbrella in her hand.` |

负向提示词一般留空；CFG 1 时不生效。

### 5.5 参数（编辑与文生图共用一套）

| 参数 | 编辑推荐 | 说明 |
|:--|--:|:--|
| 宽 / 高 | 512×512 | 输出尺寸，与参考图尺寸无关（16 对齐） |
| 步数 | **30** | 官方口径；比文生图少，因为结构已由参考图给足 |
| CFG | **4.0** | 编辑靠它听指令；调到 1 等于关掉指令引导 |
| Shift | 5.0 | 官方口径，一般不动 |
| 参考长边 | 512 | 调大保留更多细节，但更慢更吃显存 |
| 种子 | -1 | 同种子同图 |

### 5.6 参考图比例：这条最容易被忽略

**实测**（同一组参数，只换参考图）：

| 参考图 | 缩到 512 长边后 | 结果 |
|:--|:--|:--|
| 竖幅方图（约 3:4） | 512×384 | ✅ 人物、姿势、发饰、服装全保留，只换背景 |
| 极端宽幅 2608×1200（2.17:1） | 512×240 | ❌ 背景换了，但人变了（参考信息损失太多） |

**结论：参考图尽量接近方形，别用极端宽幅。** 要用宽幅原图，先把主体裁成方形再喂，或把「参考长边」调到 768–1024。

### 5.7 8 步加速能叠（只在内置通道）

编辑插件**没有第二个 LoRA 输入口**（节点只有 `snapshot_dir` + `adapter_dir`），所以要叠 DMD 只能改插件：ComfyDock 在装插件时自动打补丁（`merge_extra_lora`，把 DMD 按 `lora_down/lora_up` 并进 Linear 权重），内置 worker 通过环境变量 `MOSS_EDIT_EXTRA_LORA` 触发。

**实测三档**（512×512，同一组参考图）：

| 档 | 耗时 | 效果 |
|:--|--:|:--|
| 30 步 / CFG 4（官方） | 172–176 s | 基线 |
| 8 步 + DMD / **CFG 1** | 129.5 s | 构图同基线（人物、场景、服装融合都在），**细节略糊、整体偏闷** |
| 8 步 + DMD / **CFG 4** | 128.2 s | 构图同上，但**天空偏紫、高光过曝、对比过强** |

**怎么选**：要质量用 30 步 / CFG 4；要速度用 8 步 + CFG 1（接受降一档细节）；CFG 4 + DMD 别用（过曝）。

**为什么只快 40%**：耗时大头不在采样，而在 27 G 权重的 CPU↔GPU 搬运——8 步把采样从 100 次前向压到 8–16 次，但搬运时间不变。

**为什么只在内置通道**：环境变量只有自己起的 worker 能收到；ComfyUI 服务在跑时走 HTTP API，勾加速等于没生效。

---

## 6. 实测数据（RTX 4060 Laptop · 8 G 显存 · 2026-10-22）

| 场景 | 参数 | 耗时 | 峰值显存 |
|:--|:--|--:|--:|
| 编辑（内置通道，首次含加载） | 512×512 · 30 步 · CFG 4 | 176.3 s | 7085 / 8188 MiB |
| 编辑（模型已缓存） | 同上 | 171.8 s | 同档 |
| 编辑 | 512×512 · 50 步 · CFG 3 | 211.6 s | 同档 |
| 编辑 + DMD | 512×512 · 8 步 · CFG 1 | 129.5 s | 同档 |
| 编辑 + DMD | 512×512 · 8 步 · CFG 4 | 128.2 s | 同档 |

**8 G 卡结论：能跑，峰值吃到 7.1 / 8.0 G（86%）**——跑的时候别同时开游戏 / 其它吃显存的程序。1024×1024 起步会显著更慢且更容易换出。

---

## 7. 排障

| 现象 | 原因 | 处置 |
|:--|:--|:--|
| 面板「前置条件不足」 | 环境行里某条红了 | 看下面逐条阻塞项（面板直接写原因） |
| 「内核缺 Flux.2 支持」 | ComfyUI 内核太旧（旧版 0.3.64 没有 `EmptyFlux2LatentImage`） | 换内核（本机已升 v0.41.0，git 分支 `moss-kernel`；旧版留在 `pre-moss-kernel` 可切回） |
| 「MOSS 插件文件在位但内核没加载」 | 装完没重启 / 插件 import 失败 | 重启 ComfyUI；若仍失败看它的控制台报错（常见是 qkv 补丁没打） |
| 生成报 `value_not_in_list` | 模型名写法不一致（Windows 上子目录是反斜杠） | 面板已自动按引擎的写法归一；手工改配置时注意用反斜杠 |
| 编辑：`canEdit = false` | 四项前置任一缺 | 逐条看「编辑插件 / 编辑 LoRA / diffusers 底模」三行的红字 |
| 编辑插件文件在位但没加载 | Python 依赖没装（diffusers / optimum-quanto） | 点「装编辑依赖」，装完重启 ComfyUI |
| 编辑出图但**人变了** | 参考图是极端宽幅，缩完信息太少 | 把主体裁成方形再喂，或把「参考长边」调到 768–1024 |
| 勾了 8 步加速，编辑没变快也没变糊 | 走的是 HTTP 通道（ComfyUI 服务在跑） | DMD 叠加只在内置通道生效——关掉 ComfyUI 再生成 |
| 第一次生成特别慢（3–5 分钟） | 内置通道首次要加载 27 G 权重 + 量化 | 正常；之后每张只付采样 + 搬运时间 |
| 报显存不足 / 跑一半换出卡死 | 8 G 卡 + 大分辨率 / Hires | 降到 512–768 边长；或「环境与依赖」里把显存模式改「逐层搬运」并重启引擎 |

**自检顺序**（怀疑链路时按这个顺序，能定位到底是哪一段坏）：

1. `GET /api/moss` → 看 `env.canGenerate` / `env.canEdit` 与逐条阻塞项
2. 看 `local.state`：`stopped` / `starting` / `ready` / `busy` / `error`
3. worker 起不来 → `local.message` 带 stderr 尾巴（自定义节点的 print 全在这里）
4. 出图但产物没落到 `out\moss` → 看 `job.message`（HTTP 通道的收图失败会具名）

---

## 8. 改功能入手点

| 要改什么 | 动哪个文件 |
|:--|:--|
| 加 / 改一个模型文件、加一个方案 | `MossCatalog.cs` 的 `Files` / `Plans`（`Target` 决定落哪个模型目录，`SubDir` 决定子目录） |
| 改文生图节点图 | `MossRunner.BuildGraph`（节点 id 按段编号，`Node()` 造节点） |
| 改编辑节点图 | `MossRunner.BuildEditGraph`（新增参考图在 40+index 号段） |
| 加一个生成参数 | `MossConfig`（字段）→ `MossService.BuildParams`（折算）→ `MossService.SnapshotAsync`（回给面板）→ `UpdateConfig`（落盘）→ 面板 `mossCollect` / `fillMossForm` |
| 加一条环境探针 / 阻塞判据 | `MossEnv.ProbeAsync`（磁盘侧在 [段1]、服务侧 [段3]、阻塞项 [段5/段6]） |
| 换 / 加插件 | `MossPlugin.cs`（下载 → 解包 → 打补丁三段式；补丁要幂等） |
| 改内置通道行为 | `MossLocalEngine.cs`（起停 / 行协议 / 环境变量）+ `MossWorkerScript.cs`（Python 侧，改完记得 `py_compile`） |
| 改面板 | `ComfyDock/Web/wwwroot/index.html`（`#genPanelMoss` 段 + `moss*` JS 段；**仓库根 `Web\wwwroot` 是发布副本**） |

**改完的自检顺序**：`cs-check` → `cs-build` → `dotnet publish`（落仓库根）→ 重启 ComfyDock → 面板目检 → 真跑一张。

---

## 9. 已知限制（明确不做 / 待做）

| 项 | 状态 |
|:--|:--|
| 图生图面板控件 | 节点图已支持（`InputImage` + `ImgDenoise`），**面板无选图 UI**——当前只能改配置文件或走 API |
| 四参考图 | 节点图已接 `reference_3` / `reference_4`，真机只跑过 1 张与 2 张 |
| HTTP 通道叠 DMD | 不支持（环境变量进不去 ComfyUI 进程） |
| 极端宽幅参考图 | 不做自动裁剪（会改用户图），靠提示 + 手动裁 |
| 局部重绘（蒙版） | 不做——需要蒙版编辑面，用 ComfyUI 原生界面（示例工作流已放进它的 workflows 目录） |
| GGUF「制作压缩版」 | 不做（要跑 Python 转换，与整合包环境耦合） |
| 显存常驻 | worker 跑完不释放模型（下次更快，但与别的任务抢显存）——是否加空闲超时待定 |
| `huggingface_hub` 降级 | 装编辑依赖时 accelerate 1.15 把它从 2.2.0 降到 1.33.0 → `gradio 5.44.1` 不满足；IrodoriTTS 的 gradio 旁路受影响，ComfyUI 主链与 MOSS 编辑链实测正常 |

---

## 10. 一句话复盘

- **文生图**：模型住 ComfyUI，软件只带索引 + 节点图 + 面板；引擎在不在都能出图。
- **图像编辑**：参考图以 KV 形式进注意力，所以必须吃 diffusers 目录（27 G）；面板不设模式开关——放图即编辑。
- **8 步加速**：能叠（只在内置通道），但只快 40%，质量降一档；CFG 1 是唯一可用档。
