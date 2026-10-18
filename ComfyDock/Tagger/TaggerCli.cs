using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Tagger;

/// <summary>
/// WD 打标的自检 CLI——不经面板、不开端口，直接用常驻 worker 打一张图并把结果打到 stdout。
/// <para>用途：单机验收（解释器 / onnxruntime / 权重 / 预处理 / 标签表 一条链跑通），以及排错时看真实输出。
/// 走与面板完全同一条引擎代码路径，不是另一份实现。</para>
/// </summary>
public static class TaggerCli
{
    /// <summary>跑一次自检。</summary>
    /// <param name="imagePath">待打标图片完整路径。</param>
    /// <param name="comfyRoot">ComfyUI 根目录。</param>
    /// <returns>退出码（0 成功 / 2 前置不足 / 3 打标失败）。</returns>
    public static async Task<int> RunAsync(string imagePath, string comfyRoot)
    {
        TaggerModel? model = TaggerCatalog.DefaultModel();
        if (model is null)
        {
            Console.Error.WriteLine("模型索引为空");
            return 2;
        }

        if (!File.Exists(imagePath))
        {
            Console.Error.WriteLine("图片不存在：" + imagePath);
            return 2;
        }

        var service = new TaggerService(new TaggerConfig { ModelId = model.Id, ComfyRoot = comfyRoot }, Path.Combine(AppContext.BaseDirectory, "data"));

        var parameters = new TaggerGenParams
        {
            ModelId = model.Id,
            InputPath = imagePath,
            GeneralThreshold = 0.35,
            CharacterThreshold = 0.85,
            WriteText = false,
            Device = "auto",
        };

        TaggerFile? onnx = model.Onnx();
        TaggerFile? labels = model.Labels();
        string onnxPath = onnx is null ? "" : service.PathOf(onnx);
        string labelsPath = labels is null ? "" : service.PathOf(labels);

        Console.WriteLine("模型：" + model.Title);
        Console.WriteLine("权重：" + onnxPath + " · " + Exists(onnxPath));
        Console.WriteLine("标签：" + labelsPath + " · " + Exists(labelsPath));

        var engine = new WdTaggerEngine();
        engine.Prepare(comfyRoot);

        var items = new List<TaggerImageResult>();
        var failures = new List<string>();

        (bool ok, string message, double seconds) = await engine.RunAsync(
            onnxPath,
            labelsPath,
            model.Id,
            new List<string> { imagePath },
            parameters,
            item => items.Add(item),
            (file, reason) => failures.Add(file + "：" + reason),
            CancellationToken.None).ConfigureAwait(false);

        engine.Stop();

        Console.WriteLine("结果：" + (ok ? "OK" : "失败") + " · " + message);
        foreach (string failure in failures)
        {
            Console.WriteLine("失败项：" + failure);
        }

        foreach (TaggerImageResult item in items)
        {
            var builder = new StringBuilder();
            builder.AppendLine("文件：" + item.File + " · " + item.Seconds + " 秒");
            builder.AppendLine("分级：" + item.Rating);
            var chars = new StringBuilder();
            foreach (TaggerTag tag in item.Characters)
            {
                chars.Append(tag.Name + " " + tag.Score.ToString("F3") + "、");
            }

            builder.AppendLine("角色：" + (chars.Length > 0 ? chars.ToString() : "（无）"));
            builder.AppendLine("通用（" + item.General.Count + "）：" + item.GeneralText);
            Console.Write(builder.ToString());
        }

        return ok && items.Count > 0 ? 0 : 3;
    }

    /// <summary>文件在位与否的人话。</summary>
    /// <param name="path">路径。</param>
    /// <returns>在位给字节数，否则给「缺」。</returns>
    private static string Exists(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length + " B" : "缺";
        }
        catch (IOException)
        {
            return "读不到";
        }
    }
}
