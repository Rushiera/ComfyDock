# Qwen3-Omni 转写 — 来源说明书

> 本地生成板块第五个生成器。音频 / 视频 → 逐字稿，整条喂、不切片，跑在 llama.cpp 的 `llama-mtmd-cli` 上。
> **引擎与模型都随 ComfyUI 走**（自持）：引擎 `ComfyUI\tools\llama-omni\bin`，权重 `ComfyUI\models\LLM`——软件本体只带面板与配置，不依赖任何外部项目目录。
> 本文是这一路的唯一说明书：落点 / 参数口径 / 输入形态 / 输出口径 / 面板用法 / 排障 / 改功能入手点。

---

## 1. 一条链

```
面板选音频或视频（或目录）
  → 探媒体（时长 + 有没有音轨，PyAV）
  → 视频：抽 16 kHz 单声道 wav 到 data\omni\tmp（跑完即删，原始文件不动）
  → 算生成上限 n = min(6.5×音频秒+512, 上下文−13.05×音频秒−456)
  → 起 llama-mtmd-cli（-m 主模型 --mmproj 音频编码器 --audio 音频 -p 提示词）
  → 读 stdout 正文 → 退化截断 → 落 out\omni\<媒体名>.txt + 面板正文框
```

**不做的事：** 不启 ComfyUI 服务、不开端口、不装运行时、不切片、不改模型权重、不写 ComfyUI 内部文件、不动原始素材（视频只读，抽出的临时 wav 用完即删）。

---

## 2. 落点

| 项 | 位置 | 说明 |
|:--|:--|:--|
| 引擎 | `ComfyUI\tools\llama-omni\bin\llama-mtmd-cli.exe`（同目录 55 个 DLL 一起，合计约 1.1 GB） | 默认值在 `AppOptions.OmniCli`；面板「环境与模型」可改。**自持**——不再指向协商知识库目录；换机把整个 `bin` 目录搬过去即可 |
| 主模型 | `ComfyUI\models\LLM\Qwen3-Omni-30B-A3B-Instruct-Q4_K_M.gguf`（18,557,053,952 B） | 与 ComfyUI 共用一份模型库——软件不复制、不搬家 |
| 音频编码器 | `ComfyUI\models\LLM\mmproj-Qwen3-Omni-30B-A3B-Instruct-bf16.gguf`（2,207,465,568 B） | 音频入口在这里（AuT）；主模型压 Q4 不影响音频编码质量 |
| 媒体探针 / 抽音轨 | `ComfyUI\python_embeded\python.exe`（PyAV 19.0.1 + numpy + soundfile） | 探时长与音轨、视频抽 wav 都靠它；缺了只能转音频，且按固定 16384 token 上限 |
| 配置 | `data\omni-config.json` | 输入 / 提示词 / 上下文 / 生成上限 / NCMOE / NGL / 落 txt / 四条路径 |
| 产物 | `out\omni\<媒体名>.txt` | 引擎输出原样（退化行已截断） |
| 日志 | `data\omni\logs\<时间戳>_<序号>.out.txt` / `.err.txt` | 每次调用一对；失败时 `.err.txt` 是排错第一入口 |
| 临时音轨 | `data\omni\tmp\omni_<时间戳>.wav` | 视频抽出的 16 kHz 单声道 wav；每条跑完即删 |

模型来源 `ggml-org/Qwen3-Omni-30B-A3B-Instruct-GGUF`（ModelScope / HuggingFace 同批产物）。
引擎版本 b11406 或更新；官方 release 只给 Windows / CPU 预编译包，CUDA 版需自行编译或整目录搬。

---

## 3. 参数口径

```
-c 65536 -ctk q8_0 -ctv q8_0 -fa on -ngl 99 -ncmoe 48 -n <按段给足> --perf
--temp 0.2 --top-p 0.9 --repeat-penalty 1.15 --repeat-last-n 256
--dry-multiplier 0.8 --dry-base 1.75
```

| 参数 | 值 | 说明 |
|:--|:--|:--|
| `ctx` | 65536 | 模型原生上限；音频 token 与输出 token 共用 |
| `-ctk/-ctv` | `q8_0` | KV 量化（需 `-fa on`） |
| `-ngl` | 99 | 层放显存 |
| `-ncmoe` | 48 | MoE 专家权重留 CPU——30B 装进 8 G 显存的打法；显存越大可调小提速 |
| `-n` | 自动 | `min(6.5×音频秒+512, ctx−13.05×音频秒−456)`；给少了转写被静默截断 |
| 输入上限 | 5000 秒（约 83 分钟） | 超限报错，不静默截断；更长的媒体先切片 |

实测（RTX 4060 Laptop 8 GB）：
- 77 秒音频整链约 49 秒（含模型冷加载），输出 110 字；
- 168.6 秒视频（mp4 / aac 48 kHz 立体声）→ 抽音轨 + 转写 + 落 txt 全程通过，输出 703 字。

**每条都重新加载模型**——批量跑比单条更划算（加载一次、连续转多条）。

---

## 4. 输入形态

| 形态 | 处理 |
|:--|:--|
| 音频（mp3 / wav / m4a / aac / flac / ogg / opus / wma / amr） | 原样喂引擎（不重编码） |
| 视频（mp4 / mkv / mov / avi / webm / flv / wmv / m4v / ts / mpg / mpeg / rmvb） | **先抽音轨**：PyAV 解码 → 16 kHz 单声道 PCM16 wav → 喂引擎；临时 wav 跑完即删 |
| 目录 | 只取一层（不递归），音频与视频都收，按文件名排序逐条跑 |
| 视频没有音轨 | 该条直接报错「视频没有音轨——无法转写」，不静默跳过 |

抽音轨是**无损语义**的（只做采样率与声道转换，不做降噪 / 增益），原始视频文件只读。

---

## 5. 提示词与输出口径

**默认提示词 = 按时间 + 内容的逐字稿**，开箱即用（不用每次写）：

```
【任务】请把这段音频按时间顺序逐字转写成文字稿，要求：
1. 一行一句，按实际说话顺序，不合并、不概括、不省略；
2. 每行格式为：[发音开始时间]内容；
3. 发音开始时间为这句话开始说的时刻，格式[mm:ss]；
4. 只输出转写正文，不要标题、不要总结、不要说话人标签。
```

要换形态时才展开「提示词」改它：
- **要说话人**：把第 4 条改成「说话人用 A / B / C 区分」，并把第 2 条格式改成 `[发音开始时间][说话人]内容`。
- **要纯正文**：去掉第 2 条的时间要求，写成「只输出正文，一行一句，不要时间」。
- **场景化**（例如电话录音）：可在前面加「【前文】…」说明场景。

**后处理（与本地链一致）：** 去空行；连续同句重复超过 5 次判退化——保留前 5 行 + 一行标注，其后丢弃。

---

## 6. 面板用法

- **输入**：音频或视频，也可以给一个目录（只取一层）。视频自动抽音轨，面板会显示「抽音轨中…」。
- **提示词**：默认折叠（默认值就是逐字稿）；「恢复默认」一键回到内置口径。
- **参数**：上下文 / 生成上限（0 = 自动）/ NCMOE / 落 txt。
- **结果**：单条跑完正文直接显示在下方；批量时列表逐条显示状态与字数，点「查看」载入正文。
- **停止**：结束在跑的引擎进程（这一条作废，显存还回去）。
- **环境与模型**：引擎 / 主模型 / 音频编码器 / 解释器逐项在位情况（按磁盘实读字节），路径可改。

---

## 7. 自检 CLI

```
ComfyDock.exe --omni-selftest <音频或视频完整路径>
```

走与面板完全同一条引擎代码路径（含视频抽音轨），打印引擎 / 模型 / 输入 / 结果 / 产物 / 正文后退出。
退出码：`0` 成功 · `2` 前置不足 · `3` 转写失败。

---

## 8. 已知限制

- **时间列是模型推的**——不是真实时间戳：段内时间可能溢出段长（77 秒录音出现过 `[01:41]`），长段会被并成一行。
- **内容列有幻觉倾向**——人名 / 机构名偶有出入，关键字段与来源核对。
- **输出长度有波动**——同一条媒体不同次跑可能停在不同的行数（模型自行判 EOS）；这是模型侧行为，不是链路丢音频。
- **密集文本（歌词 / 快速日语）可能撞生成上限**——默认 `6.5 token/音频秒` 是按中文对话实测定的；遇到明显没转完的，把「生成上限」手填成 `时长 × 10` 左右再跑。
- **每次重新加载模型**——冷加载数十秒（20 G 从盘读入），批量比单条划算。
- **`--perf` 的 token 统计读不到**——本机引擎把后半段日志写到别处，stderr 只留初始化部分；面板的 token 数因此常为 0，不影响正文。

---

## 9. 排障

| 现象 | 先看什么 | 处置 |
|:--|:--|:--|
| 「引擎不在位」 | `ComfyUI\tools\llama-omni\bin` 是否整份搬过来 | 引擎 exe 同目录的 DLL 缺一个都起不来；面板路径指向 `llama-mtmd-cli.exe` |
| 「抽音轨失败」 | 解释器 / PyAV 是否在位（环境页「解释器」一行） | 装回 `python_embeded`（PyAV 随 ComfyUI 自带）；容器格式怪异时先转成 mp4 / mkv 再试 |
| 「视频没有音轨」 | 视频本身是否无声 | 换带音轨的源；本面板不合成音轨 |
| 引擎退出 rc≠0 | `data\omni\logs\<...>.err.txt` 尾部三行会进面板消息 | 常见：显存不够（OOM）→ 调大 NCMOE；模型路径写错 → 面板改路径 |
| 转写只有几行 / 明显没完 | 是否撞生成上限 | 手填「生成上限」≈ 时长 × 10；或把媒体切片重跑 |
| 正文出现「同句连续重复 N 次」 | 模型跑飞 | 保留前 5 行是设计行为；换提示词或切片重跑 |
| 媒体超过 5000 秒 | 该条直接报错 | 先切片（本面板不做自动切片） |
| 面板点「查看」没反应 | `out\omni` 里 txt 是否在 | 关掉「落 txt」时批量产物不落盘，只能看当次正文 |

---

## 10. 改功能入手点

| 想改什么 | 动哪里 |
|:--|:--|
| 参数定档（ctx / KV / NCMOE / 采样） | `ComfyDock/Omni/OmniService.cs` 的 `OmniConfig` 默认值 + `OmniEngine.RunAsync` 的命令行 |
| 生成上限公式 | `OmniService.NFor` |
| 默认提示词 | `OmniService.DefaultPrompt` |
| 退化截断规则 | `OmniEngine.DedupeLoop` / `NormLine` |
| 媒体探针 / 抽音轨 | `OmniEngine.ProbeMedia` / `ExtractAudio`（PyAV 脚本内联） |
| 支持的扩展名 / 批量范围 | `OmniService.AudioExtensions` / `VideoExtensions` / `InputFiles` |
| 选择框过滤器 | `RunningHub/NativePicker.cs` 的 `MediaFilter` |
| 面板 | `Web/wwwroot/index.html`：`LOCAL_GENS` 加项 + `genPanelOmni` + `loadOmni/renderOmni/omniGenerate`（改完记得同步 `ComfyDock/Web/wwwroot/index.html`——发布源在那份） |
| 端点 | `ComfyDock/Program.cs` 的 `/api/omni*` 一组 |
| 默认路径 | `ComfyDock/AppOptions.cs` 的 `OmniCli/OmniModel/OmniMmproj/OmniPython/OmniOutDir` |

改完的自检顺序：`cs-check` → `cs-build` → `--omni-selftest <一段音频或视频>` → 面板点一次「开始转写」并看 `out\omni`。
