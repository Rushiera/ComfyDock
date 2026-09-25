using System;
using System.IO;

namespace ComfyDock.RunningHub;

/// <summary>
/// latent 编解码 CLI——`--latent-decode` 解成 png；`--cdlat-pack` 转自建中间态 `.cdlat`；`--cdlat-unpack` 还原成 png。
/// 走内置解码器，不连本机 ComfyUI、不起任何外部进程（试跑 / 验收用）。
/// </summary>
public static class LatentCli
{
    /// <summary>执行一次解码。</summary>
    /// <param name="latentPath">latent 文件完整路径。</param>
    /// <returns>退出码（0 成功 / 2 失败）。</returns>
    public static int Run(string latentPath)
    {
        try
        {
            LatentPixels pixels = LatentCodec.DecodeFile(latentPath);
            string outputPath = Path.ChangeExtension(latentPath, ".png");
            LatentCodec.WritePng(outputPath, pixels);

            Console.WriteLine("latent 解码完成（内置解码器，未连 ComfyUI）");
            Console.WriteLine("输入：" + latentPath + "（" + new FileInfo(latentPath).Length + " 字节）");
            Console.WriteLine("尺寸：" + pixels.Width + "×" + pixels.Height + " · " + pixels.Channels + " 通道");
            Console.WriteLine("输出：" + outputPath + "（" + new FileInfo(outputPath).Length + " 字节）");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("latent 解码失败：" + ex.Message);
            return 2;
        }
    }
    /// <summary>执行一次打包——safetensors latent 转自建中间态 `.cdlat`。</summary>
    /// <param name="latentPath">latent 文件完整路径。</param>
    /// <returns>退出码（0 成功 / 2 失败）。</returns>
    public static int RunPack(string latentPath)
    {
        try
        {
            LatentPackResult result = LatentPack.Pack(latentPath);
            string mode = result.Mode == LatentPack.ModeGrid ? "8 位图像无损" : "F32 逐位无损回落";
            Console.WriteLine("中间态打包完成（无损校验通过）");
            Console.WriteLine("输入：" + latentPath + "（" + result.SourceBytes + " 字节）");
            Console.WriteLine("尺寸：" + result.Width + "×" + result.Height + " · " + result.Channels + " 通道");
            Console.WriteLine("档位：" + mode + " · 元数据段 " + result.MetadataBytes + " 字节");
            Console.WriteLine("输出：" + result.Path + "（" + result.PackBytes + " 字节 · " + Ratio(result.SourceBytes, result.PackBytes) + "×）");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("中间态打包失败：" + ex.Message);
            return 2;
        }
    }
    /// <summary>执行一次解包——自建中间态还原成 png。</summary>
    /// <param name="packPath">`.cdlat` 完整路径。</param>
    /// <returns>退出码（0 成功 / 2 失败）。</returns>
    public static int RunUnpack(string packPath)
    {
        try
        {
            LatentTensor tensor = LatentPack.Unpack(packPath);
            string pngPath = Path.ChangeExtension(packPath, ".png");
            LatentCodec.WritePng(pngPath, LatentCodec.ToPixels(tensor));

            Console.WriteLine("中间态解包完成");
            Console.WriteLine("输入：" + packPath + "（" + new FileInfo(packPath).Length + " 字节）");
            Console.WriteLine("尺寸：" + tensor.Width + "×" + tensor.Height + " · " + tensor.Channels + " 通道");
            Console.WriteLine("输出：" + pngPath + "（" + new FileInfo(pngPath).Length + " 字节）");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("中间态解包失败：" + ex.Message);
            return 2;
        }
    }
    /// <summary>算体积倍率（保留一位小数）。</summary>
    /// <param name="source">源字节数。</param>
    /// <param name="packed">中间态字节数。</param>
    /// <returns>倍率文本。</returns>
    private static string Ratio(long source, long packed)
    {
        if (packed <= 0)
        {
            return "-";
        }

        return (source / (double)packed).ToString("0.0");
    }
}
