using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ComfyDock.RunningHub;

/// <summary>
/// 解出的一张图——宽 / 高 / 通道数 + 紧凑像素。
/// </summary>
public sealed class LatentPixels
{
    /// <summary>宽（像素）。</summary>
    public int Width { get; set; }

    /// <summary>高（像素）。</summary>
    public int Height { get; set; }

    /// <summary>通道数（pixel_space 口径恒为 3）。</summary>
    public int Channels { get; set; }

    /// <summary>像素数据——逐行紧凑排列，长度 = 宽 × 高 × 通道数。</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

/// <summary>内置 latent 解码器——把云端出的 safetensors latent 直接解成 PNG，不起任何外部进程、不连本机 ComfyUI、不引第三方包。 口径与 `LoadLatent` → `VAEDecode(pixel_space)` → `SaveImage` 逐像素一致（实测比对）：pixel_space 的 `first_stage_model.decode` 是恒等，缩放全落在 VAE 的 `process_input`（编码 ×2−1）与 `process_output`（解码 clamp((v+1)/2, 0, 1)）上； 故整条链 = 「读 latent_tensor → 乘倍率 → (v+1)/2 → 钳到 0..1 → ×255 截断成 uint8 → 轴序换 [H,W,C]」。 safetensors 走 System.Text.Json + BinaryPrimitives，PNG 走 ZLibStream（均为标准库）。</summary>
public static class LatentCodec
{
    /// <summary>张量名——safetensors 里存图的那一项。</summary>
    private const string TensorName = "latent_tensor";

    /// <summary>新版格式标记——在则倍率取 1（与 ComfyUI `LoadLatent` 同判据）。</summary>
    private const string FormatVersionKey = "latent_format_version_0";
    /// <summary>元数据键——头里内嵌的 prompt 与整份 workflow JSON。</summary>
    private const string MetadataKey = "__metadata__";

    /// <summary>老式 latent 的缩放倍率（缺标记时用；与 ComfyUI 同值）。</summary>
    private const double LegacyMultiplier = 1.0 / 0.18215;

    /// <summary>PNG 文件签名。</summary>
    private static readonly byte[] PngSignature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>CRC32 查表（PNG 每个块都要算）。</summary>
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>读文件并解码。</summary>
    /// <param name="path">latent 文件完整路径。</param>
    /// <returns>解出的像素。</returns>
    public static LatentPixels DecodeFile(string path)
    {
        return ToPixels(ReadFile(path));
    }

    /// <summary>解码一份 safetensors latent 字节。</summary>
    /// <param name="bytes">文件全文。</param>
    /// <returns>解出的像素。</returns>
    public static LatentPixels Decode(byte[] bytes)
    {
        return ToPixels(Read(bytes));
    }
    /// <summary>读 latent 文件并取出张量数值。</summary>
    /// <param name="path">latent 文件完整路径。</param>
    /// <returns>张量数值（含倍率与元数据）。</returns>
    public static LatentTensor ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("latent 文件不在：" + path);
        }

        return Read(File.ReadAllBytes(path));
    }
    /// <summary>取出 safetensors latent 的张量数值——内置解码器与自建中间态打包器共用。</summary>
    /// <param name="bytes">文件全文。</param>
    /// <returns>张量数值（含倍率与元数据）。</returns>
    public static LatentTensor Read(byte[] bytes)
    {
        // [段1] 拆头——前 8 字节小端长度，其后是 JSON 头
        if (bytes.Length < 8)
        {
            throw new InvalidOperationException("latent 太短，读不出头长度：" + bytes.Length + " 字节");
        }

        ulong headerLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8));
        if (headerLength == 0 || headerLength > (ulong)(bytes.Length - 8))
        {
            throw new InvalidOperationException("latent 头长度越界：" + headerLength + "（文件共 " + bytes.Length + " 字节）");
        }

        int headerSize = (int)headerLength;
        int dataStart = 8 + headerSize;

        // [段2] 读张量描述——dtype / shape / data_offsets，并定倍率与元数据
        TensorInfo info;
        double multiplier;
        byte[] metadata = Array.Empty<byte>();
        using (JsonDocument document = JsonDocument.Parse(bytes.AsSpan(8, headerSize).ToArray()))
        {
            JsonElement root = document.RootElement;
            info = ReadTensor(root, bytes.Length - dataStart);
            multiplier = 1.0;
            JsonElement versionMark;
            if (!root.TryGetProperty(FormatVersionKey, out versionMark))
            {
                multiplier = LegacyMultiplier;
            }

            JsonElement meta;
            if (root.TryGetProperty(MetadataKey, out meta) && meta.ValueKind == JsonValueKind.Object)
            {
                metadata = Encoding.UTF8.GetBytes(meta.GetRawText());
            }
        }

        // [段3] 取张量数值——平面排列（通道号 × 平面 + 平面内序号）
        int channels = info.Shape[1];
        int height = info.Shape[2];
        int width = info.Shape[3];
        long planeLong = (long)width * height;
        long expected = planeLong * channels * 4;
        long actual = info.End - info.Begin;
        if (actual != expected)
        {
            throw new InvalidOperationException("张量数据长度不符：期望 " + expected + " 字节，实际 " + actual);
        }

        if (planeLong * channels > int.MaxValue)
        {
            throw new InvalidOperationException("图太大，超出内置解码器承载：" + width + "×" + height + "×" + channels);
        }

        int count = (int)(planeLong * channels);
        float[] values = new float[count];
        int baseOffset = dataStart + (int)info.Begin;
        for (int i = 0; i < count; i = i + 1)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(baseOffset + (i * 4), 4));
        }

        LatentTensor tensor = new LatentTensor();
        tensor.Channels = channels;
        tensor.Height = height;
        tensor.Width = width;
        tensor.Multiplier = multiplier;
        tensor.Values = values;
        tensor.Metadata = metadata;
        return tensor;
    }

    /// <summary>把像素写成 PNG（原子替换——先落 .tmp 再改名，不会出现半截文件）。</summary>
    /// <param name="path">目标 png 完整路径。</param>
    /// <param name="pixels">已解出的像素。</param>
    public static void WritePng(string path, LatentPixels pixels)
    {
        if (pixels.Width <= 0 || pixels.Height <= 0 || pixels.Channels <= 0)
        {
            throw new InvalidOperationException("像素尺寸非法：" + pixels.Width + "×" + pixels.Height + "×" + pixels.Channels);
        }

        int rowBytes = pixels.Width * pixels.Channels;
        int expected = rowBytes * pixels.Height;
        if (pixels.Data.Length != expected)
        {
            throw new InvalidOperationException("像素数据长度不符：期望 " + expected + " 字节，实际 " + pixels.Data.Length);
        }

        // [段1] 原始扫描线——每行前置一个 filter 字节（0 = 不过滤）
        byte[] raw = new byte[(rowBytes + 1) * pixels.Height];
        for (int y = 0; y < pixels.Height; y = y + 1)
        {
            int target = y * (rowBytes + 1);
            raw[target] = 0;
            Buffer.BlockCopy(pixels.Data, y * rowBytes, raw, target + 1, rowBytes);
        }

        byte[] compressed = Compress(raw);

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // [段2] 四段落盘——签名 + IHDR + IDAT + IEND
        string temp = path + ".tmp";
        using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(PngSignature, 0, PngSignature.Length);
            WriteChunk(stream, "IHDR", BuildIhdr(pixels.Width, pixels.Height, pixels.Channels));
            WriteChunk(stream, "IDAT", compressed);
            WriteChunk(stream, "IEND", Array.Empty<byte>());
        }

        File.Move(temp, path, true);
    }

    /// <summary>读张量描述并校验形态——只吃 [1,C,H,W] 的 F32。</summary>
    /// <param name="root">头 JSON 根节点。</param>
    /// <param name="dataLength">数据段字节数（用于区间校验）。</param>
    /// <returns>张量描述。</returns>
    private static TensorInfo ReadTensor(JsonElement root, int dataLength)
    {
        JsonElement tensor;
        if (!root.TryGetProperty(TensorName, out tensor) || tensor.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("头里没有 " + TensorName + " 项——非 pixel_space 口径的 latent，内置解码器不吃");
        }

        TensorInfo info = new TensorInfo();

        JsonElement dtype;
        if (!tensor.TryGetProperty("dtype", out dtype) || dtype.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("张量描述缺 dtype");
        }

        info.Dtype = dtype.GetString() ?? "";
        if (info.Dtype != "F32")
        {
            throw new InvalidOperationException("内置解码器只支持 F32 张量，这份是 " + info.Dtype);
        }

        JsonElement shape;
        if (!tensor.TryGetProperty("shape", out shape) || shape.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("张量描述缺 shape");
        }

        int[] dims = new int[shape.GetArrayLength()];
        int index = 0;
        foreach (JsonElement dim in shape.EnumerateArray())
        {
            dims[index] = dim.GetInt32();
            index = index + 1;
        }

        info.Shape = dims;
        if (dims.Length != 4 || dims[0] != 1 || dims[1] <= 0 || dims[2] <= 0 || dims[3] <= 0)
        {
            throw new InvalidOperationException("内置解码器只吃 [1,C,H,W] 形态，这份是 [" + string.Join(",", dims) + "]");
        }

        JsonElement offsets;
        if (!tensor.TryGetProperty("data_offsets", out offsets) || offsets.ValueKind != JsonValueKind.Array || offsets.GetArrayLength() != 2)
        {
            throw new InvalidOperationException("张量描述缺 data_offsets");
        }

        info.Begin = offsets[0].GetInt64();
        info.End = offsets[1].GetInt64();
        if (info.Begin < 0 || info.End < info.Begin || info.End > dataLength)
        {
            throw new InvalidOperationException("张量数据区间越界：[" + info.Begin + "," + info.End + ")，数据段 " + dataLength + " 字节");
        }

        return info;
    }

    /// <summary>把张量数值转成像素——逐元素换值（±1 → 0..1）后钳位截断，并换轴序。</summary>
    /// <param name="tensor">张量数值（含倍率）。</param>
    /// <returns>解出的像素。</returns>
    public static LatentPixels ToPixels(LatentTensor tensor)
    {
        int channels = tensor.Channels;
        int height = tensor.Height;
        int width = tensor.Width;
        long planeLong = (long)width * height;
        long expected = planeLong * channels;
        if (tensor.Values.Length != expected)
        {
            throw new InvalidOperationException("张量数值个数不符：期望 " + expected + " 个，实际 " + tensor.Values.Length + " 个");
        }

        if (planeLong * channels > int.MaxValue)
        {
            throw new InvalidOperationException("图太大，超出内置解码器承载：" + width + "×" + height + "×" + channels);
        }

        int plane = (int)planeLong;
        LatentPixels pixels = new LatentPixels();
        pixels.Width = width;
        pixels.Height = height;
        pixels.Channels = channels;
        pixels.Data = new byte[plane * channels];

        // [段1] 逐通道逐像素——张量 [1,C,H,W] 与输出 [H,W,C] 只差轴序
        // 数值口径 = ComfyUI VAE 的 process_output：clamp((v + 1) / 2, 0, 1)
        // （pixel_space 的 first_stage_model.decode 是恒等，缩放全落在 process_input / process_output 上）
        for (int c = 0; c < channels; c = c + 1)
        {
            int channelBase = c * plane;
            for (int i = 0; i < plane; i = i + 1)
            {
                float value = tensor.Values[channelBase + i];
                double scaled = ((value * tensor.Multiplier) + 1.0) / 2.0;
                if (scaled < 0.0)
                {
                    scaled = 0.0;
                }
                else if (scaled > 1.0)
                {
                    scaled = 1.0;
                }

                // 截断而非四舍五入——与 numpy 的 astype(uint8) 同口径
                pixels.Data[(i * channels) + c] = (byte)(scaled * 255.0);
            }
        }

        return pixels;
    }

    /// <summary>压一段 zlib 流（PNG 的 IDAT 内容）。</summary>
    /// <param name="raw">未压缩字节。</param>
    /// <returns>zlib 压缩后的字节。</returns>
    private static byte[] Compress(byte[] raw)
    {
        using (MemoryStream output = new MemoryStream())
        {
            using (ZLibStream zlib = new ZLibStream(output, CompressionLevel.Optimal, true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            return output.ToArray();
        }
    }

    /// <summary>组 IHDR 内容（13 字节）。</summary>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <param name="channels">通道数。</param>
    /// <returns>IHDR 数据段。</returns>
    private static byte[] BuildIhdr(int width, int height, int channels)
    {
        byte colorType;
        if (channels == 3)
        {
            colorType = 2;
        }
        else if (channels == 4)
        {
            colorType = 6;
        }
        else if (channels == 1)
        {
            colorType = 0;
        }
        else if (channels == 2)
        {
            colorType = 4;
        }
        else
        {
            throw new InvalidOperationException("不支持的通道数：" + channels);
        }

        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = colorType;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        return header;
    }

    /// <summary>写一个 PNG 块——长度 + 类型 + 数据 + CRC32。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="type">块类型（4 个 ASCII 字符）。</param>
    /// <param name="data">块数据。</param>
    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] typeBytes = new byte[4];
        for (int i = 0; i < 4; i = i + 1)
        {
            typeBytes[i] = (byte)type[i];
        }

        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length, 0, 4);
        stream.Write(typeBytes, 0, 4);
        stream.Write(data, 0, data.Length);

        byte[] crcInput = new byte[4 + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcInput, 0, 4);
        Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);
        byte[] crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput, 0, crcInput.Length));
        stream.Write(crc, 0, 4);
    }

    /// <summary>算 CRC32（PNG 用标准多项式 0xEDB88320）。</summary>
    /// <param name="buffer">数据缓冲。</param>
    /// <param name="offset">起点。</param>
    /// <param name="count">字节数。</param>
    /// <returns>校验值。</returns>
    private static uint Crc32(byte[] buffer, int offset, int count)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < count; i = i + 1)
        {
            crc = CrcTable[(int)((crc ^ buffer[offset + i]) & 0xFFu)] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>建 CRC32 查表。</summary>
    /// <returns>256 项查表。</returns>
    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (int n = 0; n < 256; n = n + 1)
        {
            uint value = (uint)n;
            for (int k = 0; k < 8; k = k + 1)
            {
                if ((value & 1u) != 0u)
                {
                    value = 0xEDB88320u ^ (value >> 1);
                }
                else
                {
                    value = value >> 1;
                }
            }

            table[n] = value;
        }

        return table;
    }

    /// <summary>safetensors 头里一个张量的描述。</summary>
    private sealed class TensorInfo
    {
        /// <summary>数据类型（只吃 F32）。</summary>
        public string Dtype = "";

        /// <summary>张量形态。</summary>
        public int[] Shape = Array.Empty<int>();

        /// <summary>数据段起点（相对数据段）。</summary>
        public long Begin;

        /// <summary>数据段终点（相对数据段）。</summary>
        public long End;
    }
}
