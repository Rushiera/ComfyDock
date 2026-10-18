using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Omni;

/// <summary>
/// Qwen3-Omni 转写的自检 CLI——不经面板、不开端口，直接把一段音频转成文字打到 stdout。
/// <para>用途：单机验收（引擎 / 主模型 / 音频编码器 / 提示词 一条链跑通），以及排错时看真实输出。
/// 走与面板完全同一条引擎代码路径，不是另一份实现。</para>
/// </summary>
public static class OmniCli
{
    /// <summary>跑一次自检。</summary>
    /// <param name="input">待转写音频完整路径。</param>
    /// <param name="cli">引擎可执行文件。</param>
    /// <param name="model">主模型。</param>
    /// <param name="mmproj">音频编码器。</param>
    /// <param name="python">解释器（只用于探时长）。</param>
    /// <param name="outDir">产物落点。</param>
    /// <returns>退出码（0 成功 / 2 前置不足 / 3 转写失败）。</returns>
    public static async Task<int> RunAsync(
        string input,
        string cli,
        string model,
        string mmproj,
        string python,
        string outDir)
    {
        if (!File.Exists(input))
        {
            Console.Error.WriteLine("输入音频不存在：" + input);
            return 2;
        }

        var service = new OmniService(
            new OmniConfig
            {
                CliPath = cli,
                ModelPath = model,
                MmprojPath = mmproj,
                PythonPath = python,
            },
            Path.Combine(AppContext.BaseDirectory, "data"),
            outDir);

        Console.WriteLine("引擎：" + cli);
        Console.WriteLine("主模型：" + model);
        Console.WriteLine("音频编码器：" + mmproj);
        Console.WriteLine("解释器：" + python);
        Console.WriteLine("输入：" + input);

        service.UpdateConfig(new OmniConfig
        {
            InputPath = input,
            Ctx = 65536,
            MaxTokens = 0,
            Ncmoe = 48,
            Ngl = 99,
            SaveText = true,
            CliPath = cli,
            ModelPath = model,
            MmprojPath = mmproj,
            PythonPath = python,
        });

        (bool ok, string message) = await service.GenerateAsync(CancellationToken.None).ConfigureAwait(false);
        if (!ok)
        {
            Console.Error.WriteLine("提交失败：" + message);
            return 2;
        }

        // 轮询任务状态到终态（引擎侧自己会报错，这里不设额外超时）
        OmniJobStatus job = service.Job();
        while (job.State == "submitted" || job.State == "running")
        {
            await Task.Delay(1000).ConfigureAwait(false);
            job = service.Job();
        }

        Console.WriteLine("结果：" + job.State + " · " + job.Message);
        Console.WriteLine("产物：" + (job.Items.Count > 0 && job.Items[0].File.Length > 0
            ? Path.Combine(outDir, job.Items[0].File)
            : "（无）"));
        Console.WriteLine("----- 正文 -----");
        Console.WriteLine(job.Text);

        return job.State == "done" && job.Text.Length > 0 ? 0 : 3;
    }
}
