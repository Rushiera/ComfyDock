# RVC 实时变声 — 说明书

> 本地生成板块第四个生成器（RVC）的**实时子签**——取本机麦克风，分块变声，直接送输出端点，边说边听。
> 与离线链共用同一份数值口径（`runtime\rvc\rvc_lite`），另起一条**流式 worker**（常驻且状态机常开）。
> 本文是这一路的唯一说明书：落点 / 形态 / 参数 / 延迟实测 / 排障 / 改功能入手点。
> 离线段（文件进、wav 出）见 `runtime/rvc/README.md`——那篇讲数值口径与验证数字，本篇讲实时链的进程、设备与参数面。

---

## 1. 一条链

```
面板「实时变声」子签
  → 选设备（类型 / 输入 / 输出 / WASAPI 独占 / 采样率）
  → 起流式 worker（python_embeded，常驻）
  → InputStream 持续收块（block_time）
  → 每块：前置缓存 extra_time → F0 重算 → 变声 → crossfade 拼接
  → OutputStream 持续放块（耳机 = 自己听；VB-CABLE 的 CABLE Input = 送给别的软件）
  → 参数热更走 update（不重开流）；干声 / 变声在块边界切换
```

**不做的事：** 不启 ComfyUI 服务、不开端口、不装运行时、不录音落盘、不改系统默认设备、不改音频路由。送声用的 VB-CABLE 驱动**由内置安装包一键装**（面板按钮 + UAC 授权，装完要重启电脑），装完不代管它、不 P/Invoke。

---

## 2. 落点

| 项 | 位置 | 说明 |
|:--|:--|:--|
| worker 脚本 | `ComfyDock/Rvc/RvcRealtimeWorkerScript.cs`（C# 常量）→ 运行时解到 `data\rvc\rvc_realtime_worker.py` | 与离线 worker 分立：离线是「一请求一响应」，实时是「流式状态机」 |
| C# 引擎 | `ComfyDock/Rvc/RvcRealtimeEngine.cs` | 设备枚举 / 起停 / 参数热更 / 事件泵 |
| 服务面 | `ComfyDock/Rvc/RvcService.cs` 实时段（文件尾「===== 实时段 =====」之后，只追加） | 配置读写 / 起停 / 热更 / 互斥判定 / 自检 |
| 端点 | `ComfyDock/Program.cs`（`/api/rvc/realtime*`） | GET `realtime` · GET `live` · POST `start` / `stop` / `update` / `devices` / `selftest` · GET `vbcable` · POST `vbcable/install` |
| 送声驱动 | `ComfyDock/Rvc/VbCableService.cs` + 内置包 `runtime\vbcable\VBCABLE_Driver_Pack*.zip`（原样不修改，来源 www.vb-cable.com · donationware） | 检测（`System32\drivers\vbaudio_cable*.sys` + 包在位 + 解压后的安装器）· 解压到 `data\vbcable` · `runas -i -h` 提权安装；**装完要重启电脑** |
| 配置 | `data\rvc-realtime-config.json` | 设备存「索引 + 名字 + 是否虚拟声卡」三份；**整对象语义**（见 §5 坑一） |
| 面板 | `ComfyDock/Web/wwwroot/index.html`（`#genPanelRvc` → `#rvcViewRt` + `rvcRt*` JS 段） | 独占面：仓库根 `Web\wwwroot` 是发布副本，改了不生效 |
| 降噪 | `runtime\rvc\torchgate.py` | 纯 torch，零新依赖；输入 / 输出两个开关 |
| 数值口径 | `runtime\rvc\rvc_lite\` | 与离线共用——**改它必须跑 `--rvc-selftest` 回归** |
| 自检 | `--rvc-rt-selftest <输出设备索引\|auto>` | 枚举设备 → 3 秒 440 Hz 正弦进链 → 打印延迟分解与每块推理；不动麦克风、不落音频 |

---

## 3. 形态与协议

| 面 | 定案 |
|:--|:--|
| 进程 | 独立常驻 worker：`python_embeded\python.exe -s <data>\rvc\rvc_realtime_worker.py` |
| 音频 IO | `sounddevice` 全双工（`InputStream` + `OutputStream`，同进程内环形缓冲） |
| 协议 | stdin/stdout 行协议——请求 `devices` / `start` / `stop` / `update`；事件 `ready` · `level` · `latency` · `error`。fd1 让给 stderr，日志不污染回执 |
| 状态 | `stopped` / `starting` / `running` / `error` |
| 显存 | 与离线引擎**互斥**——离线在跑时点实时，面板给明确阻塞提示，不静默抢卡（不自动杀进程） |
| 干声 / 变声 | 干声 = 输入块直通输出（旁路推理，延迟降到 IO 级）；切换在块边界生效、不重启引擎 |

---

## 4. 参数面

### 4.1 设备（改了要重开流）

| 控件 | 语义 |
|:--|:--|
| 设备类型（hostapi） | 下拉按 hostapi 分组显示（MME / DirectSound / WASAPI / WDM-KS）——重名端点靠分组分辨；蓝牙耳机只出现在 WDM-KS 组 |
| 输入设备 | 麦克风端点（自己选；「（不选）」= 系统默认） |
| 输出设备 | 变声送去哪：耳机 / 扬声器 = 自己听；切到「提交」时自动选 VB-CABLE 的 CABLE Input 并**锁住这一栏**（切回变声 / 干声自动还原进提交前的端点） |
| WASAPI 独占 | 延迟更低，但独占期间别的软件拿不到该端点 |
| 采样率 | 48000 / 44100 / 跟随音色模型 |
| 音色 / F0 取法 / 推理设备 / 半精度 | 同离线链口径；**fcpe 不接**（`torchfcpe` 未装）。改了就自动重开流（见 §4.3）——换音色会重新加载权重 |

### 4.2 延迟旋钮（改了要重开流）

| 控件 | 默认 | 说明 |
|:--|--:|:--|
| 块时长 block_time | 0.25 | 越小延迟越低、越吃显卡——主旋钮 |
| 淡入淡出 crossfade | 0.05 | 块间拼接的淡入淡出，消爆音 |
| 额外推理时长 extra_time | 2.5 | 多给模型一点上下文，换更稳的音高；也是抗抖动的缓冲 |
| harvest 进程 | 4 | 只有 harvest 取法用得到 |

### 4.3 热更参数（改了立即生效，不重开流）

变调 · formant · 检索混合 · 响应阈值 · 输入降噪 · 输出降噪 · 相位声码器。

**要重开流的：** 设备 / 采样率 / F0 取法 / 音色 / 推理设备 / 半精度 / 延迟旋钮——`update` 回执里给 `needRestart`。流在跑时**后端自动按新配置重开**（`stop` + `start` 在服务面完成，面板与外部调用都不用自己拼两步）：换音色要重新加载权重（约 5~15 秒），设备 / 精度类只重建链（秒级）。重开失败时回执带失败原因，配置仍已落盘（下次手动开流按新配置）。

**没开流时改参数也落盘**——热更组控件（含降噪勾选）松手即推一次 `update`，回执「配置已保存，下次开流生效」，滑块值不会在切页签后丢掉。流处在 `starting`（正在加载模型）时改动，同样按新配置再重开一次——请求在服务面串行排队，不会互相覆盖。

---

## 5. 延迟实测（8 G 卡 · RTX 4060 Laptop · rmvpe · 半精度 · 48 kHz）

`--rvc-rt-selftest` 三档实测（正弦 3 秒，不开设备）：

| 档 | block_time | crossfade | extra_time | 算法延迟 | 每块推理 |
|:--|--:|--:|--:|--:|--:|
| 低延迟 | 0.15 | 0.03 | 1.0 | 1180 ms | 324 ms |
| 基准 | 0.25 | 0.05 | 2.5 | 2800 ms | 445 ms |
| 高稳定 | 0.5 | 0.1 | 5.0 | 5600 ms | 711 ms |

**算法延迟 = 块 + crossfade + 额外推理 + 设备延迟**（面板状态块逐项显示，对账口径同此）。

**8 G 卡的上限档：** rmvpe 每块推理 **324–711 ms**，三档**都超过块时长**——块时长低于推理耗时时实时链会逐渐落后。
→ 8 G 卡建议 **block_time ≥ 0.5**（或把 F0 取法换 `pm` / `crepe` 换速度）；`extra_time` 是主要缓冲，宁可给大。
→ 显存更紧时先「停止实时」再跑 ComfyUI 出图（互斥提示只提醒，不代管进程）。

---

## 6. 排障

| 现象 | 先看哪 |
|:--|:--|
| 设备栏空 | 点「刷新设备」；首次枚举要起 worker（冷启动较慢，约 1~2 秒，期间 toast 停在「枚举音频设备…」） |
| 开流失败 | 面板状态块的「worker 输出」末几行（即 worker 的 stderr 尾部）；自检 CLI 可复现同一条路径 |
| 开流被拒且提到显卡 | 离线引擎在跑——先去「参数与变声」子签点「停止引擎」，再开实时 |
| 听不到变声 | 输出设备选的是不是当前在听的端点；再看「干声 / 变声」按钮是不是停在干声 |
| 声音断续 / 爆音 | 调大 block_time 与 crossfade；显存不足时换 pm 取法或停掉别的占卡程序 |
| 音调怪 / 电音 | F0 取法换 rmvpe；检查变调（男→女约 +12） |
| VB-CABLE 显示未装 | 到「环境与模型」子签的「送声驱动（VB-CABLE）」块点安装（内置包在 `runtime\vbcable`，不需要联网）；装完**重启电脑**，再回实时子签点「刷新设备」 |
| 切到提交但别的软件没声 | 别的软件里选「CABLE Output」当麦克风；面板输出栏是否锁在 CABLE Input（状态块末行「送声端点」会点明）；提交模式下变声必须是真的变声（干声不会送出去） |
| 端点显示「不在位」 | 配置里存的端点已消失（卸载了旧虚拟声卡 / 换了设备）——点「刷新设备」重选一个再开流；开流也会按名字校验并拒绝 |

---

## 7. 改功能入手点

| 要改什么 | 动哪 |
|:--|:--|
| 加一个热更参数 | ① `RvcRealtimeConfig` 加字段 → ② 热更白名单（`RvcService` 实时段的 diff 判定）→ ③ worker 的 `update` 处理 → ④ 面板控件 + `rvcRtCollect` |
| 加一个「改了要重开流」的控件 | ① `RealtimeNeedsRestart` 加字段比对 → ② 面板控件 onchange 绑 `rvcRtAutoRestart`（stop + start 归后端 `RealtimeUpdateAsync`，面板不自己拼两步） |
| 加一个事件（如电平包络） | ① worker `emit` → ② 引擎事件泵解析 → ③ `RvcRealtimeState` 加字段 → ④ 面板 `renderRvcRt` |
| 换音色后说话人索引越界 | worker `start()` 已按 `model.n_spk` 收敛（越界归 0）并在 `started` 回执带 `sid` / `nSpk`；引擎 `StartAsync` 据此在状态消息里出声 |
| 改 worker 脚本（`RvcRealtimeWorkerScript`）后没生效 | 脚本按**内容比对**落盘：`EnsureScriptOnDisk` 发现内容换代就停掉在跑的 worker（`EnsureWorkerReady`），下次开流按新脚本起——不必重启软件 |
| 改数值口径（F0 / 检索混合 / protect） | `runtime\rvc\rvc_lite`（离线实时共用）——改后必跑 `--rvc-selftest`，整链 maxdiff 2.2e-5 档不退化 |
| 换送声驱动 / 加别的虚拟声卡 | `VbCableService.IsCable`（端点名判定）+ `runtime\vbcable` 里的安装包 + 面板 `rvcRtCableInput` / `rvcRtSetMode`（提交态锁输出栏）/ 状态块「设备校验」——四处同源 |
| 加设备筛选 / 默认设备 | `renderRvcRtHostApis` / `renderRvcRtDevices` / `rvcRtCollect`（三处同源：渲染与收集必须一致；端点按**名字**匹配，不按索引） |
| 二期：双输出（干声走 A、变声走 B） | worker 加第二路 `OutputStream` + `outputs: [a, b]` 参数 → 引擎透传 → 面板两路选择 |
| 二期：预设保存 / 切换 | 端点 `GET/POST /api/rvc/realtime/presets` + 配置落盘；面板加下拉 |

---

## 8. 边界（明确不做）

fcpe 取法 · JIT 加速 · 训练 · UVR5 人声分离 · 模型转换 · 音频驱动安装 · 完整混音台控制 · 录音落盘 · 双输出（二期）· 预设（二期）。
