using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Rvc;

/// <summary>
/// RVC 变声的自检 CLI——不经面板、不开端口，直接用常驻 worker 变一段音频并把结果打到 stdout。
/// <para>用途：单机验收（解释器 / 依赖 / HuBERT / RMVPE / 音色模型 / 索引 一条链跑通），以及排错时看真实输出。
/// 走与面板完全同一条引擎代码路径，不是另一份实现。</para>
/// </summary>
public static class RvcCli
{
    /// <summary>跑一次自检。</summary>
    /// <param name="input">待变声音频完整路径。</param>
    /// <param name="python">解释器。</param>
    /// <param name="repo">推理代码目录。</param>
    /// <param name="models">模型根目录。</param>
    /// <param name="modelName">音色模型文件名（空 = 用 weights 里第一个）。</param>
    /// <param name="outDir">产物落点。</param>
    /// <returns>退出码（0 成功 / 2 前置不足 / 3 变声失败）。</returns>
    public static async Task<int> RunAsync(
        string input,
        string python,
        string repo,
        string models,
        string modelName,
        string outDir)
    {
        if (!File.Exists(input))
        {
            Console.Error.WriteLine("输入音频不存在：" + input);
            return 2;
        }

        var service = new RvcService(
            new RvcConfig { PythonPath = python, RepoDir = repo, ModelsRoot = models },
            Path.Combine(AppContext.BaseDirectory, "data"),
            outDir);

        if (modelName.Length == 0)
        {
            foreach (RvcModelItem item in service.ListModels())
            {
                modelName = item.Name;
                break;
            }
        }

        if (modelName.Length == 0)
        {
            Console.Error.WriteLine("weights 目录里没有音色模型：" + Path.Combine(models, "weights"));
            return 2;
        }

        Console.WriteLine("解释器：" + python);
        Console.WriteLine("推理代码：" + repo);
        Console.WriteLine("模型根：" + models);
        Console.WriteLine("输入：" + input);
        Console.WriteLine("音色：" + modelName);

        service.UpdateConfig(new RvcConfig
        {
            Model = modelName,
            InputPath = input,
            Sid = 0,
            F0UpKey = 0,
            F0Method = "rmvpe",
            IndexRate = 0.75,
            FilterRadius = 3,
            ResampleSr = 0,
            RmsMixRate = 0.25,
            Protect = 0.33,
            Device = "cuda",
            Half = true,
            PythonPath = python,
            RepoDir = repo,
            ModelsRoot = models,
        });

        (bool ok, string message) = await service.GenerateAsync(CancellationToken.None).ConfigureAwait(false);
        if (!ok)
        {
            Console.Error.WriteLine("提交失败：" + message);
            service.Stop();
            return 2;
        }

        // 轮询任务状态到终态（自检不设长超时——引擎侧自己会报错）
        RvcJobStatus job = service.Job();
        while (job.State == "submitted" || job.State == "running")
        {
            await Task.Delay(500).ConfigureAwait(false);
            job = service.Job();
        }

        RvcEngineState engine = service.Engine;
        Console.WriteLine("引擎：" + engine.State + (engine.Torch.Length > 0 ? " · torch " + engine.Torch : "")
            + (engine.Device.Length > 0 ? " · " + engine.Device : "")
            + (engine.Half ? " · 半精度" : " · 全精度")
            + " · 就绪耗时 " + engine.StartupSeconds.ToString("F1") + " 秒");
        Console.WriteLine("结果：" + job.State + " · " + job.Message);
        Console.WriteLine("产物：" + (job.File.Length > 0 ? Path.Combine(outDir, job.File) : "（无）"));

        service.Stop();
        return job.State == "done" && job.File.Length > 0 ? 0 : 3;
    }
}
