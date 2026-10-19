using System;
using System.Collections.Generic;
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
    /// <summary>跑一次自检（输入给目录、音色给 * 即走批量，逐条打印成败）。</summary>
    /// <param name="input">待变声音频或目录完整路径（目录取一层音频，逐个变声）。</param>
    /// <param name="python">解释器。</param>
    /// <param name="repo">推理代码目录。</param>
    /// <param name="models">模型根目录。</param>
    /// <param name="modelName">音色模型文件名（空 = weights 里第一个；* = 全部；逗号分隔可多选）。</param>
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
        if (!File.Exists(input) && !Directory.Exists(input))
        {
            Console.Error.WriteLine("输入不存在：" + input);
            return 2;
        }

        var service = new RvcService(
            new RvcConfig { PythonPath = python, RepoDir = repo, ModelsRoot = models },
            Path.Combine(AppContext.BaseDirectory, "data"),
            outDir);

        var modelList = new List<string>();
        if (modelName == "*")
        {
            foreach (RvcModelItem item in service.ListModels())
            {
                modelList.Add(item.Name);
            }
        }
        else if (modelName.Length > 0)
        {
            foreach (string one in modelName.Split(','))
            {
                string name = one.Trim();
                if (name.Length > 0 && modelList.IndexOf(name) < 0)
                {
                    modelList.Add(name);
                }
            }
        }
        else
        {
            foreach (RvcModelItem item in service.ListModels())
            {
                modelList.Add(item.Name);
                break;
            }
        }

        if (modelList.Count == 0)
        {
            Console.Error.WriteLine("weights 目录里没有音色模型：" + Path.Combine(models, "weights"));
            return 2;
        }

        Console.WriteLine("解释器：" + python);
        Console.WriteLine("推理代码：" + repo);
        Console.WriteLine("模型根：" + models);
        Console.WriteLine("输入：" + input);
        Console.WriteLine("音色：" + string.Join(" · ", modelList));

        service.UpdateConfig(new RvcConfig
        {
            Model = modelList[0],
            Models = modelList,
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
        foreach (RvcJobItem item in job.Items)
        {
            string mark = "[FAIL]";
            if (item.State == "done")
            {
                mark = "[OK]";
            }

            string file = item.File;
            if (file.Length == 0)
            {
                file = "（无）";
            }

            Console.WriteLine("  " + mark + " " + item.Input + " × " + item.Model + " → " + file
                + " · " + item.ElapsedSeconds.ToString("F1") + " 秒 · " + item.Message);
        }

        Console.WriteLine("产物目录：" + outDir);
        service.Stop();
        return job.State == "done" && job.Failed == 0 && job.Done > 0 ? 0 : 3;
    }

    /// <summary>
    /// 跑一次实时链自检——Voicemeeter 三态 → 枚举设备 → 3 秒 440 Hz 正弦进实时变声链 → 打印延迟与每块推理耗时。
    /// <para>不动麦克风、不落音频；走与面板完全同一条实时引擎代码路径（不是另一份实现）。</para>
    /// </summary>
    /// <param name="outIndexText">输出设备索引（整数；auto / 空 = 第一个 hostapi 的第一个输出）。</param>
    /// <param name="python">解释器。</param>
    /// <param name="repo">推理代码目录。</param>
    /// <param name="models">模型根目录。</param>
    /// <param name="modelName">音色模型文件名（空 = weights 里第一个）。</param>
    /// <param name="outDir">产物落点（实时链不落盘，仅用于构造服务）。</param>
    /// <returns>退出码（0 成功 / 2 前置不足 / 3 自检失败）。</returns>
    public static async Task<int> RunRealtimeAsync(
        string outIndexText,
        string python,
        string repo,
        string models,
        string modelName,
        string outDir)
    {
        int outIndex = -1;
        string raw = outIndexText == null ? "" : outIndexText.Trim();
        if (raw.Length > 0 && raw != "auto")
        {
            if (!int.TryParse(raw, out outIndex))
            {
                Console.Error.WriteLine("输出设备索引不是整数：" + raw + "（用整数或 auto）");
                return 2;
            }
        }

        var service = new RvcService(
            new RvcConfig { PythonPath = python, RepoDir = repo, ModelsRoot = models },
            Path.Combine(AppContext.BaseDirectory, "data"),
            outDir);
        service.SetRealtimeWorkerSource(RvcRealtimeWorkerScript.Source);

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
        Console.WriteLine("音色：" + modelName);

        // [段1] Voicemeeter 三态（未装 / 已装未跑 / 在跑）各出声
        VoicemeeterStatus vm = service.VoicemeeterDetect();
        if (!vm.Installed)
        {
            Console.WriteLine("Voicemeeter：未安装（" + vm.InstallDir + "）");
        }
        else
        {
            if (vm.Running)
            {
                Console.WriteLine("Voicemeeter：在跑（" + vm.ProcessName + "）");
            }
            else
            {
                Console.WriteLine("Voicemeeter：已装未跑（" + vm.ExePath + "）—— 面板可一键拉起");
            }
        }

        // [段2] 设备枚举
        (bool devOk, List<RvcHostApiItem> hostApis, string devMessage) = await service
            .RealtimeDevicesAsync(CancellationToken.None).ConfigureAwait(false);
        if (!devOk)
        {
            Console.Error.WriteLine("设备枚举失败：" + devMessage);
            service.RealtimeShutdown();
            return 2;
        }

        Console.WriteLine("设备：" + devMessage);
        int resolved = outIndex;
        for (int i = 0; i < hostApis.Count; i++)
        {
            RvcHostApiItem hostApi = hostApis[i];
            Console.WriteLine("  [" + hostApi.Index + "] " + hostApi.Name + "（" + hostApi.Devices.Count + " 个设备）");
            for (int j = 0; j < hostApi.Devices.Count; j++)
            {
                RvcDeviceItem dev = hostApi.Devices[j];
                string flags = "";
                if (dev.In)
                {
                    flags = flags + " in";
                }

                if (dev.Out)
                {
                    flags = flags + " out";
                }

                if (dev.Vaio)
                {
                    flags = flags + " vaio";
                }

                if (dev.Default)
                {
                    flags = flags + " 默认";
                }

                Console.WriteLine("    " + dev.Index + " · " + dev.Name + flags);
                if (resolved < 0 && dev.Out && hostApi.Index == 0)
                {
                    resolved = dev.Index;
                }
            }
        }

        if (resolved < 0)
        {
            Console.Error.WriteLine("没有可用的输出端点（先在系统里接一个耳机 / 虚拟声卡）");
            service.RealtimeShutdown();
            return 2;
        }

        // [段3] 3 秒正弦进链
        Console.WriteLine("自检：3 秒 440 Hz 正弦进实时变声链（输出设备 " + resolved + "）…");
        RvcRealtimeConfig cfg = service.RealtimeConfig();
        cfg.Model = modelName;
        if (cfg.F0Method.Length == 0)
        {
            cfg.F0Method = "rmvpe";
        }

        cfg.Half = true;
        cfg.SampleRate = 48000;
        await service.RealtimeUpdateAsync(cfg, CancellationToken.None).ConfigureAwait(false);

        (bool ok, RvcRealtimeState result, string message) = await service
            .RealtimeSelftestAsync(3, resolved, 48000, CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine("结果：" + (ok ? "通过" : "失败") + " · " + message);
        if (ok)
        {
            Console.WriteLine("采样率：" + result.SampleRate + " Hz");
            Console.WriteLine("算法延迟：" + result.LatencyMs.ToString("F0") + " 毫秒（块 "
                + result.BlockMs.ToString("F0") + " + crossfade " + result.CrossfadeMs.ToString("F0")
                + " + 额外 " + result.ExtraMs.ToString("F0")
                + " + 设备 " + result.DeviceMs.ToString("F0") + "）");
            Console.WriteLine("每块推理：" + result.InferMs.ToString("F0") + " 毫秒");
        }
        else
        {
            List<string> tail = result.StderrTail;
            for (int i = 0; i < tail.Count; i++)
            {
                Console.Error.WriteLine("  " + tail[i]);
            }
        }

        service.RealtimeShutdown();
        return ok ? 0 : 3;
    }
}
