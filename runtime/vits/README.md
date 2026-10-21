# VITS 推理链（MoeGoe 血统 · ComfyDock 自持）

> 定位：ComfyDock「本地生成」页签里 **VITS 语音合成（TTS）** 与 **语音转换（VC）** 的 Python 侧。
> 来源：`MoeGoe`（MIT · https://github.com/cjangcjengh/MoeGoe，协议文本见 `LICENSE-MoeGoe`）。
> 形态：常驻子进程 + 行协议；解释器 = 本机 ComfyUI 的 `python_embeded`；不启 ComfyUI 服务、不开端口。

---

## 一、落点

| 路径 | 内容 |
|:--|:--|
| `runtime\vits\vits_worker.py` | worker 主入口：模型生命周期 + 行协议 + 落盘 + `--selftest` |
| `runtime\vits\vits_lite\models.py` · `modules.py` · `attentions.py` · `transforms.py` · `commons.py` · `mel_processing.py` · `utils.py` | VITS 模型定义与工具——**原样搬入**，便于与上游逐文件对账 |
| `runtime\vits\vits_lite\text\__init__.py` · `japanese.py` | 文本 → 音素序列（`text_to_sequence` / `_clean_text` / 日语前端） |
| `runtime\vits\vits_lite\text\cleaners.py` | **裁剪版**：只留 `japanese_cleaners` / `japanese_cleaners2` / `identity_cleaner`（对应原版 `[CLEANED]` 直通） |

模型根：`ComfyUI\models\VITS\<分组>\<名字>.pth` + 同名 `.json`。当前分组 `346\`：
`346lab_S1`（386 spk · 22.05k）· `346lab_v3`（85 spk · 22.05k）· `346star_for44100`（16 spk · 44.1k）· `346star_v3`（16 spk · 22.05k）。

## 二、依赖（装进 `python_embeded`）

| 包 | 来源 | 用途 |
|:--|:--|:--|
| `torch` / `numpy` / `scipy` / `librosa` | ComfyUI 自带 | 推理与 STFT |
| `unidecode` | pip | 标点替换（原 `text/japanese.py` 依赖） |
| `pyopenjtalk-plus`（0.4.1.post10，含 `sudachipy` + `sudachidict_core`） | pip | **日语 g2p 关键件**——官方 `pyopenjtalk` / `openjtalk` 在 py3.12 Windows 无预编译（要 CMake + MSVC），本机无 VS；VOICEVOX fork 提供 cp312 win_amd64 wheel，模块名仍是 `pyopenjtalk`，`extract_fullcontext` 输出格式与上游一致 |

## 三、用法

```bat
:: 自检（模型名 = 文件名去后缀）
python_embeded\python.exe runtime\vits\vits_worker.py ^
  --models-root ..\..\ComfyUI\models\VITS --selftest 346lab_v3 ^
  [--text "こんにちは、世界。"] [--sid 0] [--out-dir <目录>] ^
  [--vc-audio <输入wav> --vc-src 0 --vc-dst 5]
```

行协议（stdin 一行 JSON → stdout 一行 JSON）：

| op | 参数 | 回执 |
|:--|:--|:--|
| `models` | — | `root` + `models[]`（group / name / pth / json / ok） |
| `load` | `model` | `sampling_rate` / `n_speakers` / `symbols` / `cleaners` / `speakers[]` / `device` |
| `clean` | `text` | `cleaned`（清理后的音素串，供面板预览） |
| `tts` | `text` `sid` `length_scale`(1.0) `noise_scale`(0.667) `noise_scale_w`(0.8) `cleaned`(false) `out` | `out` / `seconds` / `tokens` |
| `tts_batch` | 与 `tts` 同参数，外加 `items[{sid, out}]` | 逐条 `{"stage":"item","sid","out","seconds","tokens","ms","error"}`，收尾 `{"stage":"done","total","ok_count","fail_count","ms"}` |
| `vc` | `audio` `src` `dst` `out` | `out` / `seconds` |
| `ping` / `stop` | — | 设备与当前模型 / 退出 |

`cleaned=true` = 文本已是音素串（符号输入），跳过后端 cleaners——对应原版 `[CLEANED]` 标记。

`tts_batch` 是面板「批量合成」走的通道：**同一条文本、一次模型加载、一次文本前端**，然后逐说话人前向并各落一个 wav；单条失败只回该条 `error`，不中断整批（回执是**多行**，C# 侧要读到 `stage=done` 才算收口）。

## 四、与原版的差异（三处，其余逐文件一致）

1. **cleaners 裁剪**——只留日语链：韩/中/粤/泰/梵/上海话等 cleaner 及其依赖（jieba / pypinyin / jamo / eng_to_ipa / ko_pron / num_thai / opencc / indic_transliteration）不随包，346 模型的 `text_cleaners` 只声明 `japanese_cleaners`。
2. **`load_checkpoint` 改本地实现**——上游 `torch.load(path, map_location='cpu')` 在 torch 2.6+ 会因 `weights_only=True` 默认值拒载含 dict 的 checkpoint；worker 里补 `weights_only=False` 并保留「缺键用模型自身初始化」的旧语义。
3. **不支持 emotion_embedding 模型**——需要 w2v2 情感向量与 `audonnx`，不在本次移植面；遇到直接具名报错。

## 五、验收（2026-10-21 · RTX 4060 Laptop · cuda）

| 模型 | 加载 | TTS | VC |
|:--|:--|:--|:--|
| `346lab_v3`（85 spk 22.05k） | 1.54~1.94 s · 缺键 0 | 39 tokens → 1.92 s wav | sid 0→5 通过（1.916 s） |
| `346lab_S1`（**386 spk** 22.05k · `data.n_speakers=386`，`speakers[]` 只给 86 个名字，其余回落 `spk<索引>`） | 1.50 s · 缺键 0 | sid 0~385 | — |
| `346lab_S1` 批量（86 个带名字的 · 同一条文本） | 复用已加载模型 | **13.2 s / 86 条产物**（一次 `tts_batch`，文本前端只算一次；面板实测；全量 86 人一次 19.4 s） | — |
| `346star_for44100`（16 spk 44.1k） | 1.53 s · 缺键 0 | sid 3 → 2.22 s wav | — |

产物落 `selftest_tts.wav` / `selftest_vc.wav`，均 16-bit PCM。

## 六、排障

| 现象 | 原因 |
|:--|:--|
| `缺 config` | 模型目录里 `.pth` 与同名 `.json` 必须成对 |
| `清理后无有效符号` | 文本与 symbols 表不匹配（非日语文本 / 符号输入里有表外字符） |
| `说话人索引越界` | `sid >= n_speakers`——面板下拉按 `speakers[]` 长度生成即可避免 |
| 首次调用慢 1~2 s | 模型加载；同一进程内切模型才重载，同模型复用 |
