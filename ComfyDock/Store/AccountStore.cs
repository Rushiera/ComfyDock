using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ComfyDock.Models;

namespace ComfyDock.Store;

/// <summary>
/// accounts.json 读写——面板数据的唯一真相（启动只读，采集才写）。
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;

    /// <summary>以快照文件路径构造。</summary>
    /// <param name="filePath">accounts.json 完整路径。</param>
    public AccountStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>快照文件完整路径。</summary>
    public string FilePath => _filePath;

    /// <summary>读快照——文件缺失或损坏时返回空快照（不抛，避免启动即崩）。</summary>
    /// <returns>账号快照；无数据时为空快照。</returns>
    public AccountSnapshot Load()
    {
        if (!File.Exists(_filePath))
        {
            return new AccountSnapshot();
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            AccountSnapshot? snapshot = JsonSerializer.Deserialize<AccountSnapshot>(json, SerializerOptions);
            return snapshot ?? new AccountSnapshot();
        }
        catch (JsonException)
        {
            return new AccountSnapshot();
        }
    }

    /// <summary>写快照（父目录不存在则创建）。</summary>
    /// <param name="snapshot">待落盘快照。</param>
    public void Save(AccountSnapshot snapshot)
    {
        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_filePath, JsonSerializer.Serialize(snapshot, SerializerOptions));
    }
}
