# ComfyDock

ComfyUI 本地 / 线上运行支持的管理软件——把「本地引擎」与「线上账号」收进同一块控制台。

- **本地**——一眼看到本地 ComfyUI 跑没跑；没跑就一键启动并打开界面
- **线上**——一屏看全 RunningHub 多账号的 RH 币余额、API 密钥、工作流；红灯行一键唤起对应浏览器去登录
- **线上识别 / 线上生成**——把「音频转写」与「图片换姿势」送上 RunningHub 云端工作流跑，产物收回本地
- **本地生成（MOSS-Image）**——能力板块第三页签：在软件内跑 OpenMOSS 的 MOSS-Image 文生图条线。**模型仍住在本机 ComfyUI**（地址 + 模型目录），软件只带**依赖索引 + 下载方式 + 出图面板**——ComfyUI 哪天没了，掉的只是要重新下载的大文件依赖
- **本地生成（依田芳乃 TTS）**——同一页签下的第二个生成器：在软件内跑 Irodori-TTS（RF-DiT 日语 TTS + 依田芳乃 LoRA），常驻 worker 只加载一次模型，之后单句十几秒。**「怎么个跑法」可选**：小模型全 GPU / 大模型声码器挪 CPU / 大模型全 GPU / 自定义。来源、环境重建、对上游打的补丁都在 [`ComfyDock/docs/irodori-tts.md`](ComfyDock/docs/irodori-tts.md)

## 形态

C# / .NET 8 · 单文件 exe（framework-dependent，依赖 .NET 8 运行时）+ Kestrel 内嵌 + `wwwroot` 单页（原生 JS，无前端构建链）。

```
ComfyDock.exe      # 单文件可执行（发布产物，落仓库根）
ComfyDock/         # 源码
Web/wwwroot/       # 面板页面（发布落点）
data/              # 运行时数据（不入库）
```

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

- [`ComfyDock/docs/irodori-tts.md`](ComfyDock/docs/irodori-tts.md)——本地生成 · 依田芳乃 TTS 的来源说明书：上游仓库与模型清单、venv 重建步骤、对上游打的补丁、worker 行协议、跑法对照表、排障表、改功能的入手点
