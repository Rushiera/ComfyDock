using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Vits;

/// <summary>
/// VITS 自检 CLI——不经面板、不开端口，直接起常驻 worker 合成一句（并可选做一次换音色），把结果打到 stdout。
/// <para>与面板端点同一内核（<see cref="VitsEngine"/>），只是入口不同；排错时先跑它。</para>
/// </summary>
public static class VitsCli
{
    /// <summary>跑一次自检。</summary>
    /// <param name="model">模型名（文件名去后缀）。</param>
    /// <param name="python">解释器。</param>
    /// <param name="script">worker 脚本。</param>
    /// <param name="modelsRoot">模型根。</param>
    /// <param name="text">要合成的文本（空 = 默认一句）。</param>
    /// <param name="outDir">产物目录。</param>
    /// <param name="vcAudio">换音色的输入音频（空 = 不做 VC）。</param>
    /// <param name="vcSrc">原说话人索引。</param>
    /// <param name="vcDst">目标说话人索引。</param>
    /// <returns>进程退出码：0 全通过 / 2 有一项失败。</returns>
    public static async Task<int> RunAsync(
        string model,
        string python,
        string script,
        string modelsRoot,
        string text,
        string outDir,
        string vcAudio,
        int vcSrc,
        int vcDst)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var options = new JsonSerializerOptions { WriteIndented = false };
        string dir = string.IsNullOrWhiteSpace(outDir)
            ? Path.Combine(AppContext.BaseDirectory, "out", "vits")
            : outDir;
        Directory.CreateDirectory(dir);

        using var engine = new VitsEngine();
        engine.Prepare(python, script, modelsRoot, "auto");

        (bool started, string startMessage) = await engine.EnsureStartedAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { ok = started, message = startMessage }, options));
        if (!started)
        {
            return 2;
        }

        (bool loaded, string loadMessage, System.Text.Json.Nodes.JsonNode? node) = await engine
            .LoadAsync(model, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = loaded,
            message = loadMessage,
            model,
            samplingRate = node?["sampling_rate"]?.GetValue<double>() ?? 0,
            speakers = node?["n_speakers"]?.GetValue<double>() ?? 0,
        }, options));
        if (!loaded)
        {
            return 2;
        }

        string body = string.IsNullOrWhiteSpace(text) ? "こんにちは、世界。" : text;
        string ttsOut = Path.Combine(dir, "selftest_tts.wav");
        (bool ok, string file, double seconds, int tokens, string message) = await engine
            .TtsAsync(body, 0, 1.0, 0.667, 0.8, false, ttsOut, CancellationToken.None)
            .ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok,
            op = "tts",
            message,
            outPath = file,
            audioSeconds = seconds,
            tokens,
        }, options));
        if (!ok)
        {
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(vcAudio))
        {
            string vcOut = Path.Combine(dir, "selftest_vc.wav");
            (bool vcOk, string vcFile, double vcSeconds, int _, string vcMessage) = await engine
                .VcAsync(vcAudio, vcSrc, vcDst, vcOut, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = vcOk,
                op = "vc",
                message = vcMessage,
                outPath = vcFile,
                audioSeconds = vcSeconds,
                src = vcSrc,
                dst = vcDst,
            }, options));
            if (!vcOk)
            {
                return 2;
            }
        }

        engine.Stop();
        return 0;
    }
}
