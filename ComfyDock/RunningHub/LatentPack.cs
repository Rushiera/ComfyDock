using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ComfyDock.RunningHub;

/// <summary>
/// latent 张量的原始数值——内置解码器与自建中间态打包器共用的载体。
/// </summary>
public sealed class LatentTensor
{
    /// <summary>通道数（pixel_space 口径恒为 3）。</summary>
    public int Channels { get; set; }

    /// <summary>宽（像素）。</summary>
    public int Width { get; set; }

    /// <summary>高（像素）。</summary>
    public int Height { get; set; }

    /// <summary>倍率（新版格式为 1；老式 latent 为 1/0.18215）。</summary>
    public double Multiplier { get; set; }

    /// <summary>张量数值——按通道平面排列（通道号 × 宽 × 高 + 平面内序号）。</summary>
    public float[] Values { get; set; } = Array.Empty<float>();

    /// <summary>safetensors 头里的 <c>__metadata__</c> 原文 JSON（无则空）。</summary>
    public byte[] Metadata { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// 一次打包的结果——体积对比 + 无损判据。
/// </summary>
public sealed class LatentPackResult
{
    /// <summary>档位（0 = 8 位图像无损 · 1 = F32 逐位无损回落）。</summary>
    public int Mode { get; set; }

    /// <summary>通道数。</summary>
    public int Channels { get; set; }

    /// <summary>宽（像素）。</summary>
    public int Width { get; set; }

    /// <summary>高（像素）。</summary>
    public int Height { get; set; }

    /// <summary>源 latent 字节数。</summary>
    public long SourceBytes { get; set; }

    /// <summary>中间态字节数。</summary>
    public long PackBytes { get; set; }

    /// <summary>元数据段字节数（压缩后；0 = 源里没有元数据）。</summary>
    public int MetadataBytes { get; set; }

    /// <summary>逐位无损校验是否通过（不通过即抛错、不落盘）。</summary>
    public bool Lossless { get; set; }

    /// <summary>中间态落点完整路径。</summary>
    public string Path { get; set; } = "";
}

/// <summary>
/// 自建中间态容器 `.cdlat`——pixel_space 口径下「图像过一遍 VAE 不变」，故中间态只承载图像本身：
/// 逐像素 8 位量化 + zlib（编码期逐点实测：解回像素必须与原解码结果全等才走本档——过了就是图像无损），
/// 实测不过自动回落 F32 平面 + zlib（逐位无损）——两档都保证解出来的图像与原来一模一样，差别只在体积。
/// safetensors 头里的 <c>__metadata__</c> 一并收进容器，故 `.cdlat` 可完整替代 `.latent`。
/// </summary>
public static class LatentPack
{
    /// <summary>容器签名（4 字节 ASCII）。</summary>
    private static readonly byte[] Magic = new byte[] { (byte)'C', (byte)'D', (byte)'L', (byte)'T' };

    /// <summary>容器版本。</summary>
    private const byte Version = 1;

    /// <summary>档位：8 位图像无损（解回像素逐点等于原解码结果）。</summary>
    public const int ModeGrid = 0;

    /// <summary>档位：F32 平面回落（张量逐位无损，体积大）。</summary>
    public const int ModeFloat = 1;

    /// <summary>头部字节数（定长 32）。</summary>
    private const int HeaderSize = 32;

    /// <summary>打包——safetensors latent 转成同目录同名的 `.cdlat`。</summary>
    /// <param name="latentPath">latent 文件完整路径。</param>
    /// <returns>打包结果。</returns>
    public static LatentPackResult Pack(string latentPath)
    {
        return Pack(latentPath, Path.ChangeExtension(latentPath, ".cdlat"));
    }

    /// <summary>打包——safetensors latent 转成指定落点的 `.cdlat`。</summary>
    /// <param name="latentPath">latent 文件完整路径。</param>
    /// <param name="outputPath">中间态落点完整路径。</param>
    /// <returns>打包结果（含逐位无损校验结论）。</returns>
    public static LatentPackResult Pack(string latentPath, string outputPath)
    {
        LatentTensor tensor = LatentCodec.ReadFile(latentPath);

        LatentPackResult result = new LatentPackResult();
        result.Channels = tensor.Channels;
        result.Width = tensor.Width;
        result.Height = tensor.Height;
        result.SourceBytes = new FileInfo(latentPath).Length;
        result.Path = outputPath;

        byte[] metadata = Compress(tensor.Metadata);

        // [段1] 先试 8 位图像无损档——解回像素必须与原解码逐点一致，不过就回落
        int mode = ModeFloat;
        byte[]? payload = null;
        byte[]? grid = Quantize(tensor);
        if (grid != null)
        {
            DeltaEncode(grid, tensor.Width, tensor.Height, tensor.Channels);
            byte[] gridPayload = Compress(grid);
            LatentPixels unpacked = LatentCodec.ToPixels(Unpack(BuildContainer(ModeGrid, tensor, metadata, gridPayload)));
            if (SamePixels(LatentCodec.ToPixels(tensor), unpacked))
            {
                mode = ModeGrid;
                payload = gridPayload;
            }
        }

        // [段2] 回落档——F32 平面 + zlib
        if (payload == null)
        {
            payload = Compress(Floats(tensor.Values));
        }

        // [段3] 校验通过才落盘——先 .tmp 再改名，不留半截文件
        byte[] container = BuildContainer(mode, tensor, metadata, payload);
        LatentTensor back = Unpack(container);
        bool ok = mode == ModeGrid
            ? SamePixels(LatentCodec.ToPixels(tensor), LatentCodec.ToPixels(back))
            : SameTensor(tensor, back);
        if (!ok)
        {
            throw new InvalidOperationException("中间态校验未过——拒绝落盘（原 latent 保持不动）");
        }

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = outputPath + ".tmp";
        File.WriteAllBytes(temp, container);
        File.Move(temp, outputPath, true);

        result.Mode = mode;
        result.PackBytes = container.Length;
        result.MetadataBytes = metadata.Length;
        result.Lossless = true;
        return result;
    }

    /// <summary>解中间态——读文件。</summary>
    /// <param name="packPath">`.cdlat` 完整路径。</param>
    /// <returns>张量数值（逐位等于打包前）。</returns>
    public static LatentTensor Unpack(string packPath)
    {
        if (!File.Exists(packPath))
        {
            throw new InvalidOperationException("中间态文件不在：" + packPath);
        }

        return Unpack(File.ReadAllBytes(packPath));
    }

    /// <summary>解中间态——读字节。</summary>
    /// <param name="container">容器全文。</param>
    /// <returns>张量数值（逐位等于打包前）。</returns>
    public static LatentTensor Unpack(byte[] container)
    {
        if (container.Length < HeaderSize)
        {
            throw new InvalidOperationException("中间态太短，读不出头：" + container.Length + " 字节");
        }

        for (int i = 0; i < Magic.Length; i = i + 1)
        {
            if (container[i] != Magic[i])
            {
                throw new InvalidOperationException("不是自建中间态文件（签名不符）");
            }
        }

        if (container[4] != Version)
        {
            throw new InvalidOperationException("中间态版本不认识：" + container[4]);
        }

        int mode = container[5];
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(container.AsSpan(6, 2));
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(8, 4));
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(12, 4));
        double multiplier = BinaryPrimitives.ReadDoubleLittleEndian(container.AsSpan(16, 8));
        int metadataLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(24, 4));
        int payloadLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(28, 4));
        if (HeaderSize + metadataLength + payloadLength != container.Length)
        {
            throw new InvalidOperationException("中间态长度与头不符：" + container.Length + " 字节");
        }

        LatentTensor tensor = new LatentTensor();
        tensor.Channels = channels;
        tensor.Width = width;
        tensor.Height = height;
        tensor.Multiplier = multiplier;

        if (metadataLength > 0)
        {
            tensor.Metadata = Decompress(container, HeaderSize, metadataLength);
        }

        byte[] payload = Decompress(container, HeaderSize + metadataLength, payloadLength);
        int count = channels * height * width;
        float[] values = new float[count];
        if (mode == ModeGrid)
        {
            if (payload.Length != count)
            {
                throw new InvalidOperationException("8 位档载荷长度不符：" + payload.Length + "（期望 " + count + "）");
            }

            DeltaDecode(payload, width, height, channels);
            for (int i = 0; i < count; i = i + 1)
            {
                // 取该档的档心——离量化边界半个步长，解回像素与打包前逐点相同（不受 float 舍入影响）
                values[i] = (float)((((payload[i] + 0.5) / 255.0) * 2.0) - 1.0);
            }
        }
        else if (mode == ModeFloat)
        {
            if (payload.Length != count * 4)
            {
                throw new InvalidOperationException("F32 档载荷长度不符：" + payload.Length + "（期望 " + (count * 4) + "）");
            }

            for (int i = 0; i < count; i = i + 1)
            {
                values[i] = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(i * 4, 4));
            }
        }
        else
        {
            throw new InvalidOperationException("中间态档位不认识：" + mode);
        }

        tensor.Values = values;
        return tensor;
    }

    /// <summary>解中间态并出图——与内置解码器同一口径。</summary>
    /// <param name="packPath">`.cdlat` 完整路径。</param>
    /// <param name="pngPath">png 落点完整路径。</param>
    public static void UnpackToPng(string packPath, string pngPath)
    {
        LatentTensor tensor = Unpack(packPath);
        LatentCodec.WritePng(pngPath, LatentCodec.ToPixels(tensor));
    }
    /// <summary>解中间态并还原成 safetensors latent——换装后想拿回原格式文件时用（张量逐位等于打包前）。</summary>
    /// <param name="packPath">`.cdlat` 完整路径。</param>
    /// <param name="latentPath">latent 落点完整路径。</param>
    public static void UnpackToLatent(string packPath, string latentPath)
    {
        LatentTensor tensor = Unpack(packPath);
        byte[] data = Floats(tensor.Values);

        // [段1] 组头——latent_tensor 必带；倍率为 1 时补版本标记（与 ComfyUI LoadLatent 同判据）；元数据原样带回
        string header = "{\"latent_tensor\":{\"dtype\":\"F32\",\"shape\":[1," + tensor.Channels + "," + tensor.Height + "," + tensor.Width + "],\"data_offsets\":[0," + data.Length + "]}";
        if (tensor.Multiplier == 1.0)
        {
            header = header + ",\"latent_format_version_0\":{\"dtype\":\"F32\",\"shape\":[0],\"data_offsets\":[0,0]}";
        }

        if (tensor.Metadata.Length > 0)
        {
            header = header + ",\"__metadata__\":" + Encoding.UTF8.GetString(tensor.Metadata);
        }

        header = header + "}";

        // [段2] 头长度补到 8 的整数倍（safetensors 规格：空格填充）
        byte[] headerBytes = Encoding.UTF8.GetBytes(header);
        int padded = ((headerBytes.Length + 7) / 8) * 8;
        byte[] paddedHeader = new byte[padded];
        for (int i = 0; i < padded; i = i + 1)
        {
            if (i < headerBytes.Length)
            {
                paddedHeader[i] = headerBytes[i];
            }
            else
            {
                paddedHeader[i] = 0x20;
            }
        }

        string? dir = Path.GetDirectoryName(latentPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // [段3] 八字节小端头长度 + 头 + 数据段
        string temp = latentPath + ".tmp";
        using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] length = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)padded);
            stream.Write(length, 0, 8);
            stream.Write(paddedHeader, 0, paddedHeader.Length);
            stream.Write(data, 0, data.Length);
        }

        File.Move(temp, latentPath, true);
    }

    /// <summary>把张量量化成 8 位（与原解码同口径的截断）——倍率非 1 时返回 null（回落 F32 档）。</summary>
    /// <param name="tensor">张量数值。</param>
    /// <returns>8 位平面数据；倍率非 1（老式 latent）时为 null。</returns>
    private static byte[]? Quantize(LatentTensor tensor)
    {
        if (tensor.Multiplier != 1.0)
        {
            return null;
        }

        float[] values = tensor.Values;
        byte[] grid = new byte[values.Length];
        for (int i = 0; i < values.Length; i = i + 1)
        {
            float value = values[i];
            double scaled = (value + 1.0) / 2.0;
            if (scaled < 0.0)
            {
                scaled = 0.0;
            }
            else if (scaled > 1.0)
            {
                scaled = 1.0;
            }

            // 与原解码同口径取整（截断）——存的这一档就是解码器会吐出的那个字节
            grid[i] = (byte)(scaled * 255.0);
        }

        return grid;
    }
    /// <summary>行内差分（每行首像素原样）——8 位载荷进 zlib 前的去相关，压得更小。</summary>
    /// <param name="plane">8 位平面数据（就地改）。</param>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <param name="channels">通道数。</param>
    private static void DeltaEncode(byte[] plane, int width, int height, int channels)
    {
        int planeLength = width * height;
        for (int c = 0; c < channels; c = c + 1)
        {
            int baseOffset = c * planeLength;
            for (int y = 0; y < height; y = y + 1)
            {
                int rowStart = baseOffset + (y * width);
                for (int x = width - 1; x >= 1; x = x - 1)
                {
                    plane[rowStart + x] = (byte)(plane[rowStart + x] - plane[rowStart + x - 1]);
                }
            }
        }
    }
    /// <summary>行内差分还原——与 <c>DeltaEncode</c> 互逆。</summary>
    /// <param name="plane">8 位平面数据（就地改）。</param>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <param name="channels">通道数。</param>
    private static void DeltaDecode(byte[] plane, int width, int height, int channels)
    {
        int planeLength = width * height;
        for (int c = 0; c < channels; c = c + 1)
        {
            int baseOffset = c * planeLength;
            for (int y = 0; y < height; y = y + 1)
            {
                int rowStart = baseOffset + (y * width);
                for (int x = 1; x < width; x = x + 1)
                {
                    plane[rowStart + x] = (byte)(plane[rowStart + x] + plane[rowStart + x - 1]);
                }
            }
        }
    }

    /// <summary>把 float 平面摊成小端字节（F32 档载荷）。</summary>
    /// <param name="values">张量数值。</param>
    /// <returns>字节平面数据。</returns>
    private static byte[] Floats(float[] values)
    {
        byte[] raw = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i = i + 1)
        {
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4, 4), values[i]);
        }

        return raw;
    }

    /// <summary>组容器——头 32 字节 + 元数据段 + 载荷段。</summary>
    /// <param name="mode">档位。</param>
    /// <param name="tensor">张量数值（取尺寸与倍率）。</param>
    /// <param name="metadata">压缩后的元数据（可空）。</param>
    /// <param name="payload">压缩后的载荷。</param>
    /// <returns>容器全文。</returns>
    private static byte[] BuildContainer(int mode, LatentTensor tensor, byte[] metadata, byte[] payload)
    {
        byte[] container = new byte[HeaderSize + metadata.Length + payload.Length];
        Buffer.BlockCopy(Magic, 0, container, 0, Magic.Length);
        container[4] = Version;
        container[5] = (byte)mode;
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(6, 2), (ushort)tensor.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(8, 4), (uint)tensor.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(12, 4), (uint)tensor.Height);
        BinaryPrimitives.WriteDoubleLittleEndian(container.AsSpan(16, 8), tensor.Multiplier);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(24, 4), (uint)metadata.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(28, 4), (uint)payload.Length);
        Buffer.BlockCopy(metadata, 0, container, HeaderSize, metadata.Length);
        Buffer.BlockCopy(payload, 0, container, HeaderSize + metadata.Length, payload.Length);
        return container;
    }

    /// <summary>逐位比对两份张量数值。</summary>
    /// <param name="left">其一。</param>
    /// <param name="right">其二。</param>
    /// <returns>倍率与全部数值逐位相同为 true。</returns>
    private static bool SameTensor(LatentTensor left, LatentTensor right)
    {
        if (left.Multiplier != right.Multiplier)
        {
            return false;
        }

        if (left.Values.Length != right.Values.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Values.Length; i = i + 1)
        {
            if (left.Values[i] != right.Values[i])
            {
                return false;
            }
        }

        return true;
    }
    /// <summary>逐点比对两份像素（尺寸 + 每个字节）。</summary>
    /// <param name="left">其一。</param>
    /// <param name="right">其二。</param>
    /// <returns>尺寸与全部像素相同为 true。</returns>
    private static bool SamePixels(LatentPixels left, LatentPixels right)
    {
        if (left.Width != right.Width || left.Height != right.Height || left.Channels != right.Channels)
        {
            return false;
        }

        if (left.Data.Length != right.Data.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Data.Length; i = i + 1)
        {
            if (left.Data[i] != right.Data[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>压一段 zlib 流（空输入返回空）。</summary>
    /// <param name="raw">未压缩字节。</param>
    /// <returns>压缩后字节。</returns>
    private static byte[] Compress(byte[] raw)
    {
        if (raw.Length == 0)
        {
            return Array.Empty<byte>();
        }

        using (MemoryStream output = new MemoryStream())
        {
            using (ZLibStream zlib = new ZLibStream(output, CompressionLevel.Optimal, true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            return output.ToArray();
        }
    }

    /// <summary>解一段 zlib 流。</summary>
    /// <param name="buffer">承载缓冲。</param>
    /// <param name="offset">起点。</param>
    /// <param name="count">字节数。</param>
    /// <returns>解出的字节。</returns>
    private static byte[] Decompress(byte[] buffer, int offset, int count)
    {
        using (MemoryStream input = new MemoryStream(buffer, offset, count, false))
        {
            using (ZLibStream zlib = new ZLibStream(input, CompressionMode.Decompress))
            {
                using (MemoryStream output = new MemoryStream())
                {
                    zlib.CopyTo(output);
                    return output.ToArray();
                }
            }
        }
    }
}
