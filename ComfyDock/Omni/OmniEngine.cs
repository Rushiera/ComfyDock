using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Omni;

/// <summary>
/// 一次 Qwen3-Omni 转写调用的请求——引擎路径 + 模型路径 + 音频 + 提示词 + 参数。
/// </summary>
public sealed class OmniRunRequest
{
    /// <summary>引擎可执行文件（llama-mtmd-cli.exe）完整路径。</summary>
    public string CliPath { get; set; } = "";

    /// <summary>主模型（Qwen3-Omni-30B-A3B-Instruct Q4_K_M）完整路径。</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>音频编码器（mmproj）完整路径。</summary>
    public string MmprojPath { get; set; } = "";

    /// <summary>待转写音频完整路径。</summary>
    public string AudioPath { get; set; } = "";

    /// <summary>提示词（决定输出格式）。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>上下文长度（输入与输出共用）。</summary>
    public int Ctx { get; set; } = 65536;

    /// <summary>生成上限（token）。</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>层放显存（-ngl）。</summary>
    public int Ngl { get; set; } = 99;

    /// <summary>MoE 专家权重留 CPU 的层数（-ncmoe）。</summary>
    public int Ncmoe { get; set; } = 48;

    /// <summary>日志落点目录。</summary>
    public string LogDir { get; set; } = "";

    /// <summary>日志文件名前缀（一次任务一条）。</summary>
    public string Tag { get; set; } = "";
}

/// <summary>
/// 一次 Qwen3-Omni 转写调用的结果——文本、耗时、日志落点与失败原因。
/// </summary>
public sealed class OmniRunResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; set; }

    /// <summary>转写正文（引擎输出原样，退化行已截断）。</summary>
    public string Text { get; set; } = "";

    /// <summary>说明 / 失败原因。</summary>
    public string Message { get; set; } = "";

    /// <summary>本次耗时（秒）。</summary>
    public double Seconds { get; set; }

    /// <summary>引擎报告的生成 token 数（读不到给 0）。</summary>
    public int GenTokens { get; set; }

    /// <summary>日志落点目录（stdout / stderr 两份）。</summary>
    public string LogDir { get; set; } = "";
}

/// <summary>
/// Qwen3-Omni 转写引擎驱动——每次调用起一个 <c>llama-mtmd-cli</c> 进程（不切片、整条喂）。
/// <para>参数口径与「协商知识库」本地链同源：<c>-c 65536 -ctk/-ctv q8_0 -fa on -ngl 99 -ncmoe 48</c>
/// 加采样六参；输出后处理同口径（连续同句超过 5 次判退化并截断）。</para>
/// </summary>
public sealed class OmniEngine
{
    /// <summary>单条上限（秒）——49 分钟音频约 1300 秒，留足余量。</summary>
    private const int FileTimeoutSeconds = 10800;

    /// <summary>连续同句达到此数即判退化并截断。</summary>
    private const int LoopRun = 5;

    /// <summary>行首标签段（`[起-止][说话人]` 这类）。</summary>
    private static readonly Regex TagRegex = new Regex(@"^\s*((?:\[[^\]]*\]\s*)+)(.*)$", RegexOptions.Compiled);

    /// <summary>行首说话人标记（中文固定表 / 单字母 / 说话人N）。</summary>
    private static readonly Regex SpeakerRegex = new Regex(
        @"^\s*(?:经办|银行|客服|坐席|催收|其他|客户|本人|对方|[A-Za-z]{1,3}|说话人\s*[0-9]+)\s*(?:[:：]\s*|\s+)",
        RegexOptions.Compiled);

    /// <summary>引擎性能行（生成 token 数）。</summary>
    private static readonly Regex EvalRegex = new Regex(
        @"eval time =\s*[\d.]+ ms /\s*(\d+) tokens", RegexOptions.Compiled);

    /// <summary>状态锁（进程句柄 / 阶段 / pid）。</summary>
    private readonly object _gate = new object();

    /// <summary>当前在跑的进程（没有为 null）。</summary>
    private Process? _proc;

    /// <summary>当前进程 pid（没有为 0）。</summary>
    private int _pid;

    /// <summary>当前阶段：idle / loading / running。</summary>
    private string _stage = "idle";

    /// <summary>是否有转写在跑。</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _proc is not null;
            }
        }
    }

    /// <summary>当前进程 pid（没有为 0）。</summary>
    public int Pid
    {
        get
        {
            lock (_gate)
            {
                return _pid;
            }
        }
    }

    /// <summary>当前阶段：idle / loading / running。</summary>
    public string Stage
    {
        get
        {
            lock (_gate)
            {
                return _stage;
            }
        }
    }

    /// <summary>跑一次转写——起进程、等退出、读 stdout、做退化截断。</summary>
    /// <param name="request">本次请求。</param>
    /// <param name="ct">取消令牌（取消即结束进程树）。</param>
    /// <returns>结果。</returns>
    public async Task<OmniRunResult> RunAsync(OmniRunRequest request, CancellationToken ct)
    {
        var result = new OmniRunResult
        {
            LogDir = request.LogDir,
        };

        if (!File.Exists(request.CliPath))
        {
            result.Message = "引擎不在位：" + request.CliPath;
            return result;
        }

        if (!File.Exists(request.ModelPath))
        {
            result.Message = "主模型不在位：" + request.ModelPath;
            return result;
        }

        if (!File.Exists(request.MmprojPath))
        {
            result.Message = "音频编码器不在位：" + request.MmprojPath;
            return result;
        }

        if (!File.Exists(request.AudioPath))
        {
            result.Message = "音频不在位：" + request.AudioPath;
            return result;
        }

        string cliDir = Path.GetDirectoryName(request.CliPath) ?? "";
        if (cliDir.Length == 0)
        {
            result.Message = "引擎路径没有目录：" + request.CliPath;
            return result;
        }

        Directory.CreateDirectory(request.LogDir);
        string outPath = Path.Combine(request.LogDir, request.Tag + ".out.txt");
        string errPath = Path.Combine(request.LogDir, request.Tag + ".err.txt");

        var args = new List<string>
        {
            "-m", request.ModelPath,
            "--mmproj", request.MmprojPath,
            "--audio", request.AudioPath,
            "-p", request.Prompt,
            "-c", request.Ctx.ToString(),
            "-n", request.MaxTokens.ToString(),
            "-ctk", "q8_0",
            "-ctv", "q8_0",
            "-fa", "on",
            "-ngl", request.Ngl.ToString(),
            "-ncmoe", request.Ncmoe.ToString(),
            "--perf",
            "--temp", "0.2",
            "--top-p", "0.9",
            "--repeat-penalty", "1.15",
            "--repeat-last-n", "256",
            "--dry-multiplier", "0.8",
            "--dry-base", "1.75",
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = request.CliPath,
            WorkingDirectory = cliDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // [段1] 引擎同目录的 DLL 要能被找到（PATH / LD_LIBRARY_PATH 双写）
        string path = startInfo.Environment.TryGetValue("PATH", out string? current) && current is not null
            ? current
            : "";
        startInfo.Environment["PATH"] = cliDir + Path.PathSeparator + path;
        startInfo.Environment["LD_LIBRARY_PATH"] = cliDir;

        DateTime started = DateTime.Now;
        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            process.Start();
            lock (_gate)
            {
                _proc = process;
                _pid = process.Id;
                _stage = "loading";
            }

            // [段2] 异步收 stdout / stderr——不占缓冲、不阻塞
            Task<string> outTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errTask = process.StandardError.ReadToEndAsync();

            bool canceled = false;
            bool timedOut = false;
            while (true)
            {
                if (process.WaitForExit(500))
                {
                    break;
                }

                if (ct.IsCancellationRequested)
                {
                    canceled = true;
                    KillTree(process);
                    break;
                }

                if ((DateTime.Now - started).TotalSeconds > FileTimeoutSeconds)
                {
                    timedOut = true;
                    KillTree(process);
                    break;
                }

                lock (_gate)
                {
                    // 引擎起来 5 秒后进入生成阶段（前几秒在读模型）
                    if (_stage == "loading" && (DateTime.Now - started).TotalSeconds > 5)
                    {
                        _stage = "running";
                    }
                }
            }

            process.WaitForExit();
            string stdout = await outTask.ConfigureAwait(false);
            string stderr = await errTask.ConfigureAwait(false);
            result.Seconds = Math.Round((DateTime.Now - started).TotalSeconds, 2);

            // [段3] 日志落盘（失败可见——stderr 尾部进消息）
            File.WriteAllText(outPath, stdout, Encoding.UTF8);
            File.WriteAllText(errPath, stderr, Encoding.UTF8);

            if (canceled)
            {
                result.Message = "已取消";
                return result;
            }

            if (timedOut)
            {
                result.Message = "引擎超时（> " + FileTimeoutSeconds + " 秒）";
                return result;
            }

            if (process.ExitCode != 0)
            {
                result.Message = "引擎退出 rc=" + process.ExitCode + "：" + Tail(stderr);
                return result;
            }

            result.Text = Clean(stdout);
            result.Ok = result.Text.Length > 0;
            if (!result.Ok)
            {
                result.Message = "引擎没有输出正文（日志：" + errPath + "）";
                return result;
            }

            Match eval = EvalRegex.Match(stderr);
            if (eval.Success && int.TryParse(eval.Groups[1].Value, out int tokens))
            {
                result.GenTokens = tokens;
            }

            result.Message = "完成（" + result.Seconds.ToString("0.0") + " 秒 · " + result.Text.Length + " 字）";
            return result;
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
        {
            result.Message = "引擎起不来：" + ex.Message;
            return result;
        }
        finally
        {
            lock (_gate)
            {
                _proc = null;
                _pid = 0;
                _stage = "idle";
            }

            if (process is not null)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>停掉在跑的引擎（结束进程树）。</summary>
    /// <returns>是否真的停了一个进程。</returns>
    public bool Stop()
    {
        Process? process;
        lock (_gate)
        {
            process = _proc;
        }

        if (process is null)
        {
            return false;
        }

        KillTree(process);
        return true;
    }

    /// <summary>探媒体时长与有没有音轨——借 ComfyUI 的 python_embeded 跑 PyAV；探不到返回 (0, false)。</summary>
    /// <param name="python">解释器完整路径。</param>
    /// <param name="path">媒体完整路径（音频或视频）。</param>
    /// <returns>(时长秒, 是否含音轨)。</returns>
    public static (double Seconds, bool HasAudio) ProbeMedia(string python, string path)
    {
        if (python.Length == 0 || !File.Exists(python) || !File.Exists(path))
        {
            return (0, false);
        }

        const string script =
            "import sys, av\n"
            + "c = av.open(sys.argv[1])\n"
            + "d = (c.duration or 0) / 1000000.0\n"
            + "has = 1 if any(s.type == 'audio' for s in c.streams) else 0\n"
            + "print('%.3f %d' % (d, has))";

        string text = RunPython(python, script, 60000, path).Trim();
        string[] parts = text.Split(' ');
        if (parts.Length != 2)
        {
            return (0, false);
        }

        double seconds = 0;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out seconds))
        {
            seconds = 0;
        }

        bool hasAudio = parts[1] == "1";
        return (seconds > 0 ? seconds : 0, hasAudio);
    }

    /// <summary>把视频（或任意容器）的音轨抽成 16 kHz 单声道 wav——借 python_embeded 的 PyAV 解码。</summary>
    /// <param name="python">解释器完整路径。</param>
    /// <param name="input">源文件（视频 / 音频容器）。</param>
    /// <param name="wavOut">目标 wav 完整路径。</param>
    /// <returns>成功返回 true；没有音轨或解码失败返回 false。</returns>
    public static bool ExtractAudio(string python, string input, string wavOut)
    {
        if (python.Length == 0 || !File.Exists(python) || !File.Exists(input))
        {
            return false;
        }

        string? dir = Path.GetDirectoryName(wavOut);
        if (dir is not null && dir.Length > 0)
        {
            Directory.CreateDirectory(dir);
        }

        const string script =
            "import sys, av, numpy as np, soundfile as sf\n"
            + "src, dst = sys.argv[1], sys.argv[2]\n"
            + "c = av.open(src)\n"
            + "s = next((st for st in c.streams if st.type == 'audio'), None)\n"
            + "if s is None:\n"
            + "    print('NO_AUDIO')\n"
            + "    raise SystemExit(3)\n"
            + "r = av.AudioResampler(format='s16', layout='mono', rate=16000)\n"
            + "chunks = []\n"
            + "for frame in c.decode(s):\n"
            + "    for f in r.resample(frame):\n"
            + "        chunks.append(f.to_ndarray().reshape(-1))\n"
            + "for f in r.resample(None):\n"
            + "    chunks.append(f.to_ndarray().reshape(-1))\n"
            + "data = np.concatenate(chunks) if chunks else np.zeros(0, dtype=np.int16)\n"
            + "sf.write(dst, data, 16000, subtype='PCM_16')\n"
            + "print('OK %.3f' % (len(data) / 16000.0))";

        string text = RunPython(python, script, 1800000, input, wavOut).Trim();
        return text.StartsWith("OK", StringComparison.Ordinal) && File.Exists(wavOut);
    }

    /// <summary>跑一段 Python 脚本（stdout 全量返回；失败或超时返回空串）。</summary>
    /// <param name="python">解释器完整路径。</param>
    /// <param name="script">脚本正文（-c 传参）。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <param name="arguments">脚本之后的实参（sys.argv[1..]）。</param>
    /// <returns>stdout 原文；失败返回空串。</returns>
    private static string RunPython(string python, string script, int timeoutMs, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("进程没有起来");
            Task<string> outTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                KillTree(process);
                return "";
            }

            process.WaitForExit();
            string text = outTask.GetAwaiter().GetResult();
            _ = errTask.GetAwaiter().GetResult();
            return text;
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    /// <summary>结束进程树——先 CloseMainWindow 不适用（控制台程序），直接 Kill(true)。</summary>
    /// <param name="process">进程。</param>
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
        {
            // 进程已退出 / 权限不足——停不下来时由上层超时兜底
            Console.Error.WriteLine("结束引擎进程失败：" + ex.Message);
        }
    }

    /// <summary>stderr 尾部三行（失败时进消息，不整段倒进面板）。</summary>
    /// <param name="stderr">标准错误全文。</param>
    /// <returns>尾部摘要。</returns>
    private static string Tail(string stderr)
    {
        string[] lines = (stderr ?? "").Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        for (int i = lines.Length - 1; i >= 0 && kept.Count < 3; i--)
        {
            if (lines[i].Trim().Length > 0)
            {
                kept.Insert(0, lines[i].Trim());
            }
        }

        return kept.Count == 0 ? "无 stderr" : string.Join(" / ", kept);
    }

    /// <summary>引擎输出后处理——去空行 + 连续同句退化截断。</summary>
    /// <param name="raw">stdout 原文。</param>
    /// <returns>正文。</returns>
    private static string Clean(string raw)
    {
        string text = (raw ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length == 0)
        {
            return "";
        }

        string[] lines = text.Split('\n');
        var kept = new List<string>();
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                kept.Add(trimmed);
            }
        }

        return string.Join("\n", DedupeLoop(kept));
    }

    /// <summary>连续同句超过 run 次即判退化：保留前 run 行 + 一行标注，其后内容丢弃。</summary>
    /// <param name="lines">正文行。</param>
    /// <param name="run">允许的连续重复次数。</param>
    /// <returns>截断后的行。</returns>
    private static List<string> DedupeLoop(List<string> lines, int run = LoopRun)
    {
        var result = new List<string>();
        int i = 0;
        while (i < lines.Count)
        {
            int j = i;
            while (j + 1 < lines.Count && NormLine(lines[j + 1]) == NormLine(lines[i]))
            {
                j = j + 1;
            }

            int count = j - i + 1;
            if (count > run)
            {
                for (int k = i; k < i + run; k++)
                {
                    result.Add(lines[k]);
                }

                result.Add("……（同句连续重复 " + count + " 次，判退化，以下丢弃）");
                return result;
            }

            for (int k = i; k <= j; k++)
            {
                result.Add(lines[k]);
            }

            i = j + 1;
        }

        return result;
    }

    /// <summary>去掉行首标签与说话人标记后的正文——退化检测用（时间戳不同、正文相同也算同句）。</summary>
    /// <param name="line">一行。</param>
    /// <returns>正文。</returns>
    private static string NormLine(string line)
    {
        Match tag = TagRegex.Match(line);
        string text = tag.Success ? tag.Groups[2].Value : line;
        return SpeakerRegex.Replace(text, "").Trim();
    }
}
