# Qwen3-Omni 转写 — 来源说明书

> 本地生成板块第五个生成器。音频 → 文字，整条喂、不切片，跑在 llama.cpp 的 `llama-mtmd-cli` 上。
> 与「协商知识库」本地转写链同源：同模型、同参数定档、同提示词口径、同后处理（退化截断）。
> 本文是这一路的唯一说明书：落点 / 参数口径 / 输出形态 / 面板用法 / 排障 / 改功能入手点。

---

## 1. 一条链

```
面板选音频（或目录）
  → 探时长（ComfyUI 的 python_embeded + PyAV）
  → 算生成上限 n = min(6.5×音频秒+512, 上下文−13.05×音频秒−456)
  → 起 llama-mtmd-cli（-m 主模型 --mmproj 音频编码器 --audio 音频 -p 提示词）
  → 读 stdout 正文 → 退化截断 → 落 out\omni\<音频名>.txt + 面板正文框
```

**不做的事：** 不启 ComfyUI 服务、不开端口、不装运行时、不切片、不改模型权重、不写 ComfyUI 内部文件。

---

## 2. 落点

| 项 | 位置 | 说明 |
|:--|:--|:--|
| 引擎 | `WorkSpace:协商知识库\tools\llama\bin\llama-mtmd-cli.exe`（同目录 DLL 一起） | 默认值在 `AppOptions.OmniCli`；面板「环境与模型」可改。换机器时把整个 `bin` 目录搬过去即可 |
| 主模型 | `ComfyUI\models\LLM\Qwen3-Omni-30B-A3B-Instruct-Q4_K_M.gguf`（18,557,053,952 B） | 与 ComfyUI 共用一份模型库——软件不复制、不搬家 |
| 音频编码器 | `ComfyUI\models\LLM\mmproj-Qwen3-Omni-30B-A3B-Instruct-bf16.gguf`（2,207,465,568 B） | 音频入口在这里（AuT）；主模型压 Q4 不影响音频编码质量 |
| 时长探针 | `ComfyUI\python_embeded\python.exe`（PyAV 19.0.1） | **只用来探时长**；缺了仍能转写，按固定 16384 token 上限 |
| 配置 | `data\omni-config.json` | 输入 / 提示词 / 上下文 / 生成上限 / NCMOE / NGL / 落 txt / 四条路径 |
| 产物 | `out\omni\<音频名>.txt` | 引擎输出原样（退化行已截断） |
| 日志 | `data\omni\logs\<时间戳>_<序号>.out.txt` / `.err.txt` | 每次调用一对；失败时 `.err.txt` 是排错第一入口 |

模型来源 `ggml-org/Qwen3-Omni-30B-A3B-Instruct-GGUF`（ModelScope / HuggingFace 同批产物）。
引擎版本 b11406 或更新；官方 release 只给 Windows / CPU 预编译包。

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
| 输入上限 | 5000 秒（约 83 分钟） | 超限报错，不静默截断；更长的音频先切片 |

实测（RTX 4060 Laptop 8 GB）：77 秒音频整链约 49 秒（含模型冷加载），输出 110 字。
**每条都重新加载模型**——批量跑比单条更划算（加载一次、连续转多条）。

---

## 4. 提示词与输出形态

提示词是**唯一的格式开关**：

- 默认（与 `协商知识库\tools\llama\prompt.txt` 同口径）产出每行 `[发音开始时间][说话人][说话内容]`，说话人固定「经办 / 客户」。
- 想要纯正文：把任务写成「只输出正文，不要时间与说话人」。
- 换场景：改前文（这段录音是什么场景）+ 任务（怎么分段 / 什么格式 / 说话人怎么标）。

**后处理（与本地链一致）：** 去空行；连续同句重复超过 5 次判退化——保留前 5 行 + 一行标注，其后丢弃。

---

## 5. 面板用法

- **输入**：一段音频，或一个目录（只取一层、不递归）。目录按文件名排序逐条跑，每条落一个同名 txt。
- **提示词**：「恢复默认」回到内置口径；改完点「保存参数」生效。
- **参数**：上下文 / 生成上限（0 = 自动）/ NCMOE / 落 txt。
- **结果**：单条跑完正文直接显示在下方；批量时列表逐条显示状态与字数，点「查看」载入正文。
- **停止**：结束在跑的引擎进程（这一条作废，显存还回去）。
- **环境与模型**：引擎 / 主模型 / 音频编码器 / 解释器逐项在位情况（按磁盘实读字节），路径可改。

---

## 6. 自检 CLI

```
ComfyDock.exe --omni-selftest <音频完整路径>
```

走与面板完全同一条引擎代码路径，打印引擎 / 模型 / 输入 / 结果 / 产物 / 正文后退出。
退出码：`0` 成功 · `2` 前置不足 · `3` 转写失败。

---

## 7. 已知限制

- **时间与说话人两列是模型推的**——不是真实时间戳、不是声纹分离：段内时间戳可能溢出段长（77 秒录音出现过 `[01:41]`），说话人按语义判、长段会被并成一行。
- **内容列有幻觉倾向**——人名 / 机构名偶有出入，关键字段与来源核对。
- **输出长度有波动**——同一条音频不同次跑可能停在不同的行数（模型自行判 EOS）；这是模型侧行为，不是链路丢音频。要稳定复跑请固定提示词并做切片。
- **每次重新加载模型**——冷加载数十秒（20 G 从盘读入），批量比单条划算。
- **`--perf` 的 token 统计读不到**——本机引擎把后半段日志写到别处，stderr 只留初始化部分；面板的 token 数因此常为 0，不影响正文。

---

## 8. 排障

| 现象 | 先看什么 | 处置 |
|:--|:--|:--|
| 「引擎不在位」 | `bin` 目录是否整份搬过来 | 引擎 exe 同目录的 DLL 缺一个都起不来；面板路径指向 `llama-mtmd-cli.exe` |
| 引擎退出 rc≠0 | `data\omni\logs\<...>.err.txt` 尾部三行会进面板消息 | 常见：显存不够（OOM）→ 调大 NCMOE；模型路径写错 → 面板改路径 |
| 转写只有几行 | 是否模型自行判 EOS | 换提示词强调「完整转写」；或把音频切片重跑；对照 `out\omni` 里同名的旧结果 |
| 正文出现「同句连续重复 N 次」 | 模型跑飞 | 保留前 5 行是设计行为；换提示词或切片重跑 |
| 音频超过 5000 秒 | 该条直接报错 | 先切片（本面板不做自动切片） |
| 面板点「查看」没反应 | `out\omni` 里 txt 是否在 | 关掉「落 txt」时批量产物不落盘，只能看当次正文 |

---

## 9. 改功能入手点

| 想改什么 | 动哪里 |
|:--|:--|
| 参数定档（ctx / KV / NCMOE / 采样） | `ComfyDock/Omni/OmniService.cs` 的 `OmniConfig` 默认值 + `OmniEngine.RunAsync` 的命令行 |
| 生成上限公式 | `OmniService.NFor` |
| 默认提示词 | `OmniService.DefaultPrompt` |
| 退化截断规则 | `OmniEngine.DedupeLoop` / `NormLine` |
| 时长探针 | `OmniEngine.ProbeDuration`（PyAV 脚本内联） |
| 批量范围 / 音频扩展名 | `OmniService.InputFiles` / `AudioExtensions` |
| 面板 | `Web/wwwroot/index.html`：`LOCAL_GENS` 加项 + `genPanelOmni` + `loadOmni/renderOmni/omniGenerate` |
| 端点 | `ComfyDock/Program.cs` 的 `/api/omni*` 一组 |
| 默认路径 | `ComfyDock/AppOptions.cs` 的 `OmniCli/OmniModel/OmniMmproj/OmniPython/OmniOutDir` |

改完的自检顺序：`cs-check` → `cs-build` → `--omni-selftest <一段音频>` → 面板点一次「开始转写」并看 `out\omni`。
