using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyDock.Tagger;

/// <summary>一条打标模型文件的下载任务（面板按它画进度条）。</summary>
public sealed class TaggerDownloadJob
{
    /// <summary>索引 id。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Title { get; set; } = "";

    /// <summary>落盘完整路径。</summary>
    public string TargetPath { get; set; } = "";

    /// <summary>期望字节数（0 = 未知，按 Content-Length 累加）。</summary>
    public long ExpectedBytes { get; set; }

    /// <summary>已完成字节数（含断点续传之前的部分）。</summary>
    public long DoneBytes { get; set; }

    /// <summary>状态：queued / running / done / error / canceled。</summary>
    public string State { get; set; } = "queued";

    /// <summary>当前使用的下载源（hf-mirror / hf / modelscope）。</summary>
    public string Source { get; set; } = "";

    /// <summary>瞬时速度（字节/秒）。</summary>
    public double SpeedBps { get; set; }

    /// <summary>说明 / 失败原因。</summary>
    public string Message { get; set; } = "";

    /// <summary>开始时刻。</summary>
    public string StartedAt { get; set; } = "";

    /// <summary>结束时刻。</summary>
    public string FinishedAt { get; set; } = "";
}

/// <summary>
/// 打标模型下载器——单线程串行（同一时刻只下一个大文件），断点续传（`.part` → 原子改名），
/// 源按目录给的顺序依次回退；零第三方包（HttpClient + FileStream）。
/// <para>与 MOSS 那套同构，但只认 <see cref="TaggerFile"/>——两边索引结构不同，不共用类型。</para>
/// </summary>
public sealed class TaggerDownloader
{
    /// <summary>读写块大小（1 MB）。</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>状态锁。</summary>
    private readonly object _gate = new object();

    /// <summary>全部任务（含历史）。</summary>
    private readonly List<TaggerDownloadJob> _jobs = new List<TaggerDownloadJob>();

    /// <summary>有新任务时的唤醒信号。</summary>
    private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);

    /// <summary>下载专用 HTTP 客户端（不设总超时——大文件要跑很久；UA 由 BuildClient 带上）。</summary>
    private readonly HttpClient _client = BuildClient();

    /// <summary>取消源（按任务换）。</summary>
    private CancellationTokenSource _cts = new CancellationTokenSource();

    /// <summary>工作线程是否已起。</summary>
    private bool _started;

    /// <summary>起后台工作线程（幂等）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _ = Task.Run(WorkerLoopAsync);
    }

    /// <summary>入队一条下载。</summary>
    /// <param name="file">索引条目。</param>
    /// <param name="targetPath">落盘完整路径。</param>
    /// <param name="reason">入队失败原因。</param>
    /// <returns>入队成功返回 true。</returns>
    public bool Enqueue(TaggerFile file, string targetPath, out string reason)
    {
        reason = "";
        Start();

        lock (_gate)
        {
            foreach (TaggerDownloadJob job in _jobs)
            {
                if (job.Id == file.Id && (job.State == "queued" || job.State == "running"))
                {
                    reason = "该文件已在下载队列里";
                    return false;
                }
            }

            if (File.Exists(targetPath))
            {
                long size = new FileInfo(targetPath).Length;
                if (file.Bytes <= 0 || size == file.Bytes)
                {
                    reason = "该文件已在下位";
                    return false;
                }
            }

            _jobs.Add(new TaggerDownloadJob
            {
                Id = file.Id,
                Title = file.Title,
                TargetPath = targetPath,
                ExpectedBytes = file.Bytes,
                DoneBytes = PartBytes(targetPath),
                State = "queued",
                Message = "排队中",
            });
        }

        _signal.Release();
        return true;
    }

    /// <summary>取消一条任务（排队中或下载中）。</summary>
    /// <param name="id">索引 id。</param>
    /// <returns>取消成功返回 true。</returns>
    public bool Cancel(string id)
    {
        lock (_gate)
        {
            foreach (TaggerDownloadJob job in _jobs)
            {
                if (job.Id != id || (job.State != "queued" && job.State != "running"))
                {
                    continue;
                }

                if (job.State == "queued")
                {
                    job.State = "canceled";
                    job.Message = "已取消";
                    job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    return true;
                }
            }
        }

        try
        {
            _cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>任务清单快照（新的在前）。</summary>
    /// <returns>任务列表。</returns>
    public List<TaggerDownloadJob> Snapshot()
    {
        var list = new List<TaggerDownloadJob>();
        lock (_gate)
        {
            for (int i = _jobs.Count - 1; i >= 0; i--)
            {
                TaggerDownloadJob job = _jobs[i];
                list.Add(new TaggerDownloadJob
                {
                    Id = job.Id,
                    Title = job.Title,
                    TargetPath = job.TargetPath,
                    ExpectedBytes = job.ExpectedBytes,
                    DoneBytes = job.DoneBytes,
                    State = job.State,
                    Source = job.Source,
                    SpeedBps = job.SpeedBps,
                    Message = job.Message,
                    StartedAt = job.StartedAt,
                    FinishedAt = job.FinishedAt,
                });
            }
        }

        return list;
    }

    /// <summary>是否还有排队或下载中的任务。</summary>
    /// <returns>有返回 true。</returns>
    public bool Busy()
    {
        lock (_gate)
        {
            foreach (TaggerDownloadJob job in _jobs)
            {
                if (job.State == "queued" || job.State == "running")
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>取下一个排队任务（没有则等一会儿）。</summary>
    /// <returns>任务；长期无任务返回 null。</returns>
    private async Task<TaggerDownloadJob?> TakeNextAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                foreach (TaggerDownloadJob job in _jobs)
                {
                    if (job.State == "queued")
                    {
                        return job;
                    }
                }
            }

            await _signal.WaitAsync(2000).ConfigureAwait(false);
        }
    }

    /// <summary>后台循环——一次一条，串行下载。</summary>
    /// <returns>永不结束的任务。</returns>
    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            TaggerDownloadJob? job = await TakeNextAsync().ConfigureAwait(false);
            if (job is null)
            {
                continue;
            }

            TaggerFile? file = TaggerCatalog.FindFile(job.Id);
            if (file is null)
            {
                lock (_gate)
                {
                    job.State = "error";
                    job.Message = "索引里没有这个 id";
                }

                continue;
            }

            _cts = new CancellationTokenSource();
            await RunAsync(job, file, _cts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>真正下一条文件——源依次回退 + 断点续传。</summary>
    /// <param name="job">任务。</param>
    /// <param name="file">索引条目。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>无。</returns>
    private async Task RunAsync(TaggerDownloadJob job, TaggerFile file, CancellationToken ct)
    {
        string target = job.TargetPath;
        string part = target + ".part";
        string lastError = "";

        lock (_gate)
        {
            job.State = "running";
            job.Message = "下载中";
            job.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        try
        {
            string? dir = Path.GetDirectoryName(target);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            lock (_gate)
            {
                job.State = "error";
                job.Message = "建目录失败：" + ex.Message;
            }

            return;
        }

        // [段0] `.part` 已经下满就直接落位
        if (file.Bytes > 0 && PartBytes(part) >= file.Bytes)
        {
            if (await TryFinishAsync(job, part, target, file.Bytes, ct).ConfigureAwait(false))
            {
                return;
            }
        }

        string[] urls = TaggerCatalog.Urls(file);
        foreach (string url in urls)
        {
            // 同一个源最多试两次——第二次走断点续传，专门对付中途断流
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                long existing = PartBytes(part);
                if (existing > 0 && file.Bytes > 0 && existing >= file.Bytes)
                {
                    if (await TryFinishAsync(job, part, target, file.Bytes, ct).ConfigureAwait(false))
                    {
                        return;
                    }
                }

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (existing > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(existing, null);
                    }

                    using HttpResponseMessage response = await _client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);

                    if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
                    {
                        existing = 0;
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        lastError = "HTTP 404（" + SourceName(url) + " 没有这个文件）";
                        break;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = "HTTP " + (int)response.StatusCode + "（" + SourceName(url) + "）";
                        break;
                    }

                    long total = file.Bytes;
                    long? length = response.Content.Headers.ContentLength;
                    if (length.HasValue)
                    {
                        total = existing + length.Value;
                    }

                    lock (_gate)
                    {
                        job.Source = SourceName(url);
                        job.ExpectedBytes = total;
                        job.DoneBytes = existing;
                    }

                    long done = existing;

                    // FileShare.Read：写的时候还允许别的句柄读尺寸
                    using (var output = new FileStream(
                        part,
                        existing > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read,
                        BufferSize,
                        true))
                    {
                        using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        byte[] buffer = new byte[BufferSize];
                        DateTime tick = DateTime.UtcNow;
                        long tickBytes = done;

                        while (true)
                        {
                            int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                            if (read <= 0)
                            {
                                break;
                            }

                            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            done += read;

                            double seconds = (DateTime.UtcNow - tick).TotalSeconds;
                            if (seconds >= 1.0)
                            {
                                double speed = (done - tickBytes) / seconds;
                                tick = DateTime.UtcNow;
                                tickBytes = done;
                                lock (_gate)
                                {
                                    job.DoneBytes = done;
                                    job.SpeedBps = speed;
                                }
                            }
                            else
                            {
                                lock (_gate)
                                {
                                    job.DoneBytes = done;
                                }
                            }
                        }

                        await output.FlushAsync(ct).ConfigureAwait(false);
                    }

                    // [段3] 判据以「写进去的字节数」为准——刚关掉的句柄读尺寸可能还拿不到
                    if (file.Bytes > 0 && done < file.Bytes)
                    {
                        lastError = "下载不完整（" + done + " / " + file.Bytes + " 字节）";
                        continue;
                    }

                    if (await TryFinishAsync(job, part, target, done, ct).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }
                catch (OperationCanceledException)
                {
                    lastError = "已取消";
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
                {
                    lastError = ex.Message;
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }
        }

        lock (_gate)
        {
            bool canceled = ct.IsCancellationRequested;
            job.State = canceled ? "canceled" : "error";
            job.Message = canceled
                ? "已取消（`.part` 留着，再点一次接着下）"
                : ("下载失败：" + (lastError.Length > 0 ? lastError : "未知原因"));
            job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    /// <summary>取地址对应的源名（面板显示用）。</summary>
    /// <param name="url">地址。</param>
    /// <returns>源名。</returns>
    private static string SourceName(string url)
    {
        if (url.Contains("hf-mirror.com", StringComparison.OrdinalIgnoreCase))
        {
            return "hf-mirror";
        }

        if (url.Contains("modelscope.cn", StringComparison.OrdinalIgnoreCase))
        {
            return "modelscope";
        }

        return "huggingface";
    }

    /// <summary>读 `.part` 已下载字节数（没有则 0）。</summary>
    /// <param name="target">目标路径。</param>
    /// <returns>字节数。</returns>
    private static long PartBytes(string target)
    {
        try
        {
            var info = new FileInfo(target + ".part");
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>把 `.part` 改名落位（带小重试），成功后把任务标成完成。</summary>
    /// <param name="job">任务。</param>
    /// <param name="part">`.part` 路径。</param>
    /// <param name="target">目标路径。</param>
    /// <param name="size">已下字节数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否已落位。</returns>
    private async Task<bool> TryFinishAsync(TaggerDownloadJob job, string part, string target, long size, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                File.Move(part, target, true);
                lock (_gate)
                {
                    job.State = "done";
                    job.DoneBytes = size;
                    job.ExpectedBytes = size;
                    job.Message = "已落位：" + Path.GetFileName(target);
                    job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lock (_gate)
                {
                    job.Message = "改名落位失败（重试中）：" + ex.Message;
                }

                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>建下载客户端——不设总超时（大文件要跑很久），但**必须带 UA**（部分源的 WAF 对空 UA 回 403）。</summary>
    /// <returns>HTTP 客户端。</returns>
    private static HttpClient BuildClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ComfyDock/0.33");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }
}
