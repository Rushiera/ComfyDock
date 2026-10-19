# ComfyDock

ComfyUI 本地 / 线上运行支持的管理软件——把「本地引擎」与「线上账号」收进同一块控制台。

- **本地**——一眼看到本地 ComfyUI 跑没跑；没跑就一键启动并打开界面
- **线上**——一屏看全 RunningHub 多账号的 RH 币余额、API 密钥、工作流；红灯行一键唤起对应浏览器去登录
- **线上识别 / 线上生成**——把「音频转写」与「图片换姿势」送上 RunningHub 云端工作流跑，产物收回本地
- **本地生成（MOSS-Image）**——能力板块第三页签：在软件内跑 OpenMOSS 的 MOSS-Image 文生图条线。**模型仍住在本机 ComfyUI**（地址 + 模型目录），软件只带**依赖索引 + 下载方式 + 出图面板**——ComfyUI 哪天没了，掉的只是要重新下载的大文件依赖
- **本地生成（IrodoriTTS）**——同一页签下的第二个生成器：在软件内跑 Irodori-TTS（RF-DiT 日语 TTS），常驻 worker 只加载一次模型，之后单句十几秒。**「怎么个跑法」可选**：依田芳乃 · v4.1-Small 全 GPU / v4-Large int8 全 GPU（8 G 卡推荐）/ v4-Large bf16 声码器挪 CPU / v4-Large bf16 全 GPU / 自定义。角色靠 LoRA + 参考音频决定（当前挂「依田芳乃」），日后加别的角色模型只换 LoRA 目录。**不装独立运行时**——直接用本机 ComfyUI 的 `python_embeded`；代码在 `ComfyUI\irodori-tts`，权重在 `ComfyUI\models\TTS\yoshino`。来源、环境重建、对上游打的补丁、逐参数说明、**训练/后处理该看哪些文件**都在 [`ComfyDock/docs/irodori-tts.md`](ComfyDock/docs/irodori-tts.md)

- **本地生成（RVC 变声）**——同一页签下的第四个生成器：把一段人声的**音色**换成另一个音色（内容与时长保留）。音色模型 / 检索索引 / HuBERT / RMVPE 都住本机 ComfyUI 的 `models\RVC\`，推理代码**随软件走**（`runtime/rvc/rvc_lite`，自持实现）——**不跑 RVC 的 WebUI、不依赖它的整合包、不装 fairseq**（HuBERT 为纯 torch 复刻，逐层与原版对齐 ≤ 7e-6）。依赖（faiss-cpu / pyworld / praat-parselmouth / torchcrepe）装进 ComfyUI 的 `python_embeded`。口径、验证数字与**改动入手点**见 [`runtime/rvc/README.md`](runtime/rvc/README.md)

- **本地生成（Qwen3-Omni 转写）**——同一页签下的第五个生成器：**音频 / 视频 → 文字**。Qwen3-Omni-30B-A3B（GGUF Q4_K_M）经 llama.cpp 的 `llama-mtmd-cli` **整条转写（不切片）**；模型住本机 ComfyUI 的 `models\LLM`（主模型 18.56 GB + mmproj 2.21 GB），**引擎也随 ComfyUI 走**（`ComfyUI\tools\llama-omni\bin`，自持）。**一次性子进程**——每条重新加载模型、跑完即退，不启 ComfyUI 服务、不开端口、不装运行时。**视频自动抽音轨**（16 kHz 单声道，临时 wav 用完即删，原始视频不动）；默认提示词就是「按时间 + 内容的逐字稿」（`[mm:ss]内容`），开箱即用、不用每次写。单文件或目录（一层）批量，产物落 `out\omni\`。口径、参数定档、排障与**改动入手点**见 [`ComfyDock/docs/qwen3-omni.md`](ComfyDock/docs/qwen3-omni.md)

## 形态

C# / .NET 8 · 单文件 exe（framework-dependent，依赖 .NET 8 运行时）+ Kestrel 内嵌 + `wwwroot` 单页（原生 JS，无前端构建链）。

```
ComfyDock.exe      # 单文件可执行（发布产物，落仓库根）
ComfyDock/         # 源码
Web/wwwroot/       # 面板页面（发布落点）
data/              # 运行时数据（不入库）
```

> TTS 这一路不占本目录：解释器与权重都在本机 ComfyUI 下（`ComfyUI\python_embeded` · `ComfyUI\irodori-tts` · `ComfyUI\models\TTS\yoshino`），见文档。

## 构建

```
dotnet publish ComfyDock/ComfyDock.csproj -c Release
```

发布落点 = 仓库根（csproj `PublishDir`），产物覆盖 `ComfyDock.exe` + `Web/wwwroot/`。

## 运行

双击 `ComfyDock.exe` → 面板监听 `http://127.0.0.1:8971/`。

命令行：

```
ComfyDock.exe --rh-status            # 线上识别状态
ComfyDock.exe --rh-submit <音频>     # 提交识别任务
ComfyDock.exe --rh-poll              # 查询任务
```

## 边界

- 本地侧只看状态 + 一键启动——不做模型 / 队列 / 任务管理
- 不做登录——不代填账号密码、不处理验证码
- 不碰凭据——不读 cookie 内容、不落盘密码
- `data/` 为运行时数据（含账号密钥），不入库

## 更新记录

见 `CHANGELOG.md`。

## 文档

- [`ComfyDock/docs/irodori-tts.md`](ComfyDock/docs/irodori-tts.md)——本地生成 · IrodoriTTS 的来源说明书：上游仓库与模型清单、venv 重建步骤、对上游打的补丁、worker 行协议、**逐参数说明**、跑法对照表、排障表、改功能的入手点、文档地图
