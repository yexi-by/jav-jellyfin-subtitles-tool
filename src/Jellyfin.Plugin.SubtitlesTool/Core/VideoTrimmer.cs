using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed record TrimPlan(Guid Id, double RequestedMilliseconds, double CutMilliseconds, double DurationMilliseconds);
public sealed record TrimStatus(Guid Id, string State, double Progress, double CutMilliseconds, string Message);

/// <summary>关键帧对齐后复制媒体流，校验成功才直接替换视频，不保存原视频。</summary>
public sealed class VideoTrimmer(IMediaEncoder encoder, ISessionManager sessions, MediaTargets targets, SubtitleStore subtitles, IFileSystem fileSystem, IHostApplicationLifetime lifetime, ILogger<VideoTrimmer> logger)
{
    private sealed record StreamInfo(int Index, string Type, string Codec, int Width, int Height);
    private sealed record ProbeInfo(double Start, double Duration, StreamInfo[] Streams);
    private sealed record Packet(double Pts, double Dts, string Hash, bool Key);
    private sealed record Planned(MediaTarget Target, string Stamp, TrimPlan Public, ProbeInfo Media, Packet Key, DateTime Expires);
    private sealed class Job(Planned plan, CancellationToken stopping)
    {
        public Planned Plan { get; } = plan;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        public TrimStatus Status = new(Guid.NewGuid(), "copying", 0, plan.Public.CutMilliseconds, "正在复制保留的视频和音轨…");
    }
    private readonly ConcurrentDictionary<Guid, Planned> _plans = new();
    private readonly ConcurrentDictionary<string, Job> _jobs = new(MediaTargets.PathComparer);
    private static readonly ConcurrentDictionary<string, byte> Active = new(MediaTargets.PathComparer);
    private readonly SemaphoreSlim _worker = new(1, 1);

    public static void CheckAvailable(string path)
    {
        if (Active.ContainsKey(path)) throw new ToolException("当前视频正在裁切，请完成或取消后再操作。", 409);
    }
    public TrimStatus? Status(string path) => _jobs.TryGetValue(path, out var job) ? Volatile.Read(ref job.Status) : null;

    public async Task<TrimPlan> PlanAsync(MediaTarget target, double requestedMilliseconds, CancellationToken token)
    {
        CheckAvailable(target.Path);
        if (!double.IsFinite(requestedMilliseconds) || requestedMilliseconds <= 0)
            throw new ToolException("请选择大于 0 秒的正片起点。");
        CheckPlayback(target);
        var stamp = MediaFiles.Stamp(target.Path);
        var media = await ProbeAsync(target.Path, token);
        if (media.Streams.Count(stream => stream.Type == "video") != 1 || media.Streams[0].Type != "video")
            throw new ToolException("快速裁切目前需要一条主视频流，请先处理额外的视频流或封面流。");
        if (requestedMilliseconds / 1000 >= media.Duration - 1) throw new ToolException("起点太接近视频结束，请至少保留 1 秒。");
        var packets = await PacketsAsync(target.Path, requestedMilliseconds / 1000 + media.Start, token);
        var key = packets.FirstOrDefault(packet => packet.Key && (packet.Pts - media.Start) * 1000 + 0.001 >= requestedMilliseconds)
            ?? throw new ToolException("所选位置之后没有找到可用关键帧，请选择更早的位置。");
        var cut = (key.Pts - media.Start) * 1000;
        if (cut / 1000 >= media.Duration - 1) throw new ToolException("之后的关键帧太接近结束，请选择更早的位置。");
        if (MediaFiles.Stamp(target.Path) != stamp) throw new ToolException("定位期间视频发生变化，请重新定位。", 409);
        foreach (var (id, previous) in _plans) if (previous.Expires < DateTime.UtcNow) _plans.TryRemove(id, out _);
        if (_plans.Count >= 128) throw new ToolException("待执行起点过多，请稍后重新定位。", 409);
        var plan = new TrimPlan(Guid.NewGuid(), requestedMilliseconds, cut, media.Duration * 1000);
        _plans[plan.Id] = new Planned(target, stamp, plan, media, key, DateTime.UtcNow.AddMinutes(15));
        return plan;
    }

    public async Task<TrimStatus> StartAsync(MediaTarget target, Guid planId, CancellationToken token)
    {
        if (!_plans.TryGetValue(planId, out var plan) || plan.Expires < DateTime.UtcNow || !MediaTargets.PathComparer.Equals(plan.Target.Path, target.Path))
            throw new ToolException("裁切起点已失效，请重新定位。", 409);
        if (_worker.CurrentCount == 0) throw new ToolException("另一段视频正在裁切，请完成后再开始。", 409);
        using var gate = await MediaFiles.EnterAsync(target.Path, token);
        CheckAvailable(target.Path);
        CheckPlayback(target);
        if (!_plans.ContainsKey(planId)) throw new ToolException("这一裁切计划已使用，请重新定位。", 409);
        if (MediaFiles.Stamp(target.Path) != plan.Stamp) throw new ToolException("视频已被替换，请重新定位裁切起点。", 409);
        if (!_worker.Wait(0)) throw new ToolException("另一段视频正在裁切，请完成后再开始。", 409);
        _plans.TryRemove(planId, out _);
        var job = new Job(plan, lifetime.ApplicationStopping);
        _jobs[target.Path] = job;
        Active[target.Path] = 0;
        foreach (var (path, previous) in _jobs)
            if (_jobs.Count > 64 && previous != job && !Active.ContainsKey(path)) _jobs.TryRemove(path, out _);
        _ = RunAsync(job);
        return job.Status;
    }

    public TrimStatus Cancel(string path, Guid jobId)
    {
        if (!_jobs.TryGetValue(path, out var job) || job.Status.Id != jobId) throw new ToolException("裁切任务不存在。", 404);
        lock (job)
        {
            if (job.Status.State is "copying" or "verifying") job.Cancellation.Cancel();
            return job.Status;
        }
    }

    private async Task RunAsync(Job job)
    {
        var plan = job.Plan;
        var target = plan.Target;
        var token = job.Cancellation.Token;
        var temp = Path.Combine(Path.GetDirectoryName(target.Path)!, "." + Path.GetFileNameWithoutExtension(target.Path) + ".jav-trim-" + job.Status.Id.ToString("N") + Path.GetExtension(target.Path));
        var published = false;
        try
        {
            using var gate = await MediaFiles.EnterAsync(target.Path, token);
            var remaining = plan.Media.Duration - plan.Public.CutMilliseconds / 1000;
            var decodeStart = Math.Max(0, plan.Key.Dts - plan.Media.Start);
            var seek = Math.Max(0, decodeStart - 1);
            // 输入寻址先到附近，再按解码时间丢弃前面的包；B 帧的呈现时间不能代替解码时间。
            await ProcessAsync(encoder.EncoderPath,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", Number(seek), "-i", target.Path, "-ss", Number(Math.Max(0, decodeStart - seek - 0.000001)),
                 "-map", "0", "-c", "copy", "-map_metadata", "0", "-map_chapters", "-1", "-avoid_negative_ts", "make_zero", "-progress", "pipe:1", "-nostats", temp],
                line =>
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && double.TryParse(line[12..], NumberStyles.Float, CultureInfo.InvariantCulture, out var micros))
                        Volatile.Write(ref job.Status, job.Status with { Progress = Math.Clamp(micros / 1_000_000 / remaining * 100, 0, 99) });
                }, token);
            Volatile.Write(ref job.Status, job.Status with { State = "verifying", Progress = 99, Message = "正在核对关键帧、时长和媒体流…" });
            var output = await ProbeAsync(temp, token);
            var first = (await PacketsAsync(temp, 0, token, true)).FirstOrDefault();
            if (first is null || !first.Key || first.Hash != plan.Key.Hash || !plan.Media.Streams.SequenceEqual(output.Streams)
                || output.Duration < 1 || Math.Abs(output.Duration - (first.Pts - output.Start) - remaining) > 0.3
                || await FrameHashAsync(target.Path, plan.Public.CutMilliseconds / 1000, token) != await FrameHashAsync(temp, 0, token))
                throw new ToolException("裁切结果未通过关键帧、时长或媒体流校验，视频未替换。");
            token.ThrowIfCancellationRequested();
            var resolved = MediaTargets.ResolvePath(target.Path);
            if (MediaFiles.Stamp(target.Path) != plan.Stamp || !MediaTargets.PathComparer.Equals(resolved, target.Path) || !targets.IsInScope(resolved))
                throw new ToolException("处理期间视频或处理路径发生变化，视频未替换。", 409);
            CheckPlayback(target);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(target.Path));
            else File.SetAttributes(temp, File.GetAttributes(target.Path));
            lock (job)
            {
                token.ThrowIfCancellationRequested();
                job.Status = job.Status with { State = "publishing", Message = "正在替换视频并刷新媒体信息…" };
            }
            // 先使旧记录失效，再原子替换；不创建原视频或字幕的恢复副本。
            File.Delete(HashRecords.RecordPath(target.Path));
            subtitles.ForgetVideo(target);
            File.Move(temp, target.Path, true);
            published = true;
            try
            {
                await target.Item.RefreshMetadata(new MetadataRefreshOptions(new DirectoryService(fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh, ImageRefreshMode = MetadataRefreshMode.None,
                    ReplaceAllMetadata = false, ForceSave = true, RegenerateTrickplay = false
                }, CancellationToken.None);
                Volatile.Write(ref job.Status, job.Status with { State = "done", Progress = 100, Message = "视频已永久裁切。字幕时间保持，可播放核对并继续校准。" });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "视频 {ItemId} 已裁切，Jellyfin 刷新失败", target.Id);
                Volatile.Write(ref job.Status, job.Status with { State = "done", Progress = 100, Message = "视频已永久裁切。Jellyfin 刷新失败，请刷新媒体信息后继续校准。" });
            }
        }
        catch (OperationCanceledException) when (!published)
        { Volatile.Write(ref job.Status, job.Status with { State = "cancelled", Message = "裁切已取消，原视频保持。" }); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "视频 {ItemId} 裁切失败", target.Id);
            Volatile.Write(ref job.Status, job.Status with { State = "error", Message = ex is ToolException ? ex.Message : "裁切失败，视频未替换。请检查空间、目录权限及服务器日志。" });
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) { logger.LogWarning(ex, "清理裁切临时输出 {Path} 失败", temp); }
            Active.TryRemove(target.Path, out _);
            job.Cancellation.Dispose();
            _worker.Release();
        }
    }

    private void CheckPlayback(MediaTarget target)
    {
        if (sessions.Sessions.Any(session => session.IsActive && session.NowPlayingItem is { } item
            && (item.Id == target.Id || item.Id == target.VersionId)))
            throw new ToolException("当前影片还有播放会话，请先退出播放器，再执行裁切。", 409);
    }

    private async Task<ProbeInfo> ProbeAsync(string path, CancellationToken token)
    {
        var text = await CaptureAsync(["-v", "error", "-show_entries", "format=start_time,duration:stream=index,codec_type,codec_name,width,height", "-of", "json", path], token);
        using var json = JsonDocument.Parse(text);
        var format = json.RootElement.GetProperty("format");
        var duration = ReadNumber(format, "duration");
        if (!double.IsFinite(duration) || duration <= 0) throw new ToolException("无法读取视频时长，当前容器不支持快速裁切。");
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().Select(stream => new StreamInfo(stream.GetProperty("index").GetInt32(),
            stream.GetProperty("codec_type").GetString()!, stream.GetProperty("codec_name").GetString()!,
            stream.TryGetProperty("width", out var width) ? width.GetInt32() : 0, stream.TryGetProperty("height", out var height) ? height.GetInt32() : 0)).ToArray();
        return new ProbeInfo(ReadNumber(format, "start_time"), duration, streams);
    }
    private async Task<Packet[]> PacketsAsync(string path, double start, CancellationToken token, bool firstOnly = false)
    {
        var interval = firstOnly ? "%+#1" : Number(start) + "%+120";
        var text = await CaptureAsync(["-v", "error", "-select_streams", "v:0", "-read_intervals", interval, "-show_packets", "-show_data_hash", "sha256",
            "-show_entries", "packet=pts_time,dts_time,flags,data_hash", "-of", "json", path], token);
        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("packets").EnumerateArray().Where(packet => packet.TryGetProperty("pts_time", out _)).Select(packet =>
            new Packet(ReadNumber(packet, "pts_time"), packet.TryGetProperty("dts_time", out _) ? ReadNumber(packet, "dts_time") : ReadNumber(packet, "pts_time"),
                packet.GetProperty("data_hash").GetString()!, packet.GetProperty("flags").GetString()!.Contains('K'))).ToArray();
    }
    private async Task<string> FrameHashAsync(string path, double start, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        string? hash = null;
        await ProcessAsync(encoder.EncoderPath, ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", Number(start), "-i", path,
            "-map", "0:v:0", "-frames:v", "1", "-an", "-f", "framemd5", "-"], line =>
        {
            if (!line.StartsWith('#') && line.Contains(',')) hash = line.Split(',')[^1].Trim();
        }, timeout.Token);
        return hash ?? throw new ToolException("无法解码裁切起点，视频未替换。");
    }
    private async Task<string> CaptureAsync(string[] args, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var lines = new System.Text.StringBuilder();
        try
        {
            await ProcessAsync(encoder.ProbePath, args, line =>
            {
                if (lines.Length > 8 * 1024 * 1024) throw new ToolException("媒体探测结果异常大，请选择另一处起点。");
                lines.AppendLine(line);
            }, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ToolException("关键帧探测超时，请选择另一处起点后重试。", 504); }
        return lines.ToString();
    }
    private static double ReadNumber(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static string Number(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static async Task ProcessAsync(string executable, string[] arguments, Action<string> output, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { throw new ToolException("无法启动 Jellyfin 的 FFmpeg／FFprobe，请检查服务器媒体编码器设置。"); }
        using var killed = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token) is { } line) output(line);
            await process.WaitForExitAsync(token);
            var detail = await errors;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("FFmpeg／FFprobe 失败：" + detail[..Math.Min(detail.Length, 1500)]);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
