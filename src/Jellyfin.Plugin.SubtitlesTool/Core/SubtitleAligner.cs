using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed record AlignmentAudioTrack(int Index, string Label, bool IsDefault);
public sealed record AlignmentOptions(bool Available, string Message, AlignmentAudioTrack[] AudioTracks, int? DefaultAudioIndex, long MediaStartMilliseconds);
public sealed record AlignmentStatus(Guid Id, string State, string Stage, int Completed, int Total, long? OffsetMilliseconds, string CurrentHash, string MediaStamp, int AudioIndex, AlignmentAnchor[] Anchors, AlignmentUsage Usage, double ElapsedSeconds, string Message);
internal sealed record AlignmentPoint(int Sample, AlignmentAnchor Anchor);

public sealed class SubtitleAligner(IMediaEncoder encoder, SubtitleStore subtitles, IApplicationPaths paths, IHostApplicationLifetime lifetime, ILogger<SubtitleAligner> logger) : IDisposable
{
    private sealed record Media(double Duration, double Start, AlignmentAudioTrack[] Tracks);
    private sealed class Job(MediaTarget target, string subtitleId, AlignmentStatus status, CancellationToken stopping)
    {
        public readonly MediaTarget Target = target;
        public readonly string SubtitleId = subtitleId;
        public readonly DateTime Created = DateTime.UtcNow;
        public readonly CancellationTokenSource Cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        public AlignmentStatus Status = status;
    }
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly ConcurrentDictionary<string, Job> _jobs = new(MediaTargets.PathComparer);
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<AlignmentOptions> OptionsAsync(MediaTarget target, CancellationToken token)
    {
        var media = await ProbeAsync(target.Path, token);
        var error = !SpeechAssets.Supported ? "自动校准需要 Linux x64 或 Windows x64。" : !File.Exists(SpeechAssets.Archive) ? "安装包缺少语音识别资源，请重新安装完整发行包。" : media.Tracks.Length == 0 ? "视频没有可分析的音轨。" : AnchorMatcher.ConfigurationError(Plugin.Instance?.Configuration ?? new());
        return new(error is null, error ?? "可识别实际台词并核对对应字幕，预览后保存。", media.Tracks, (media.Tracks.FirstOrDefault(track => track.IsDefault) ?? media.Tracks.FirstOrDefault())?.Index, (long)Math.Round(media.Start * 1000));
    }

    public async Task<AlignmentStatus> StartAsync(MediaTarget target, string subtitleId, string hash, string stamp, int? audioIndex, long? startMilliseconds, CancellationToken token)
    {
        var current = Plugin.Instance?.Configuration ?? new();
        var config = new Configuration { LlmApiBaseUrl = current.LlmApiBaseUrl, LlmApiKey = current.LlmApiKey, LlmModel = current.LlmModel, LlmParameters = current.LlmParameters };
        if (AnchorMatcher.ConfigurationError(config) is { } error) throw new ToolException(error);
        if (!_worker.Wait(0)) throw new ToolException("服务器正在分析另一份字幕，请等待或取消该任务。", 409);
        try
        {
            var info = await subtitles.OpenAsync(target, subtitleId, false, token);
            if (info.CurrentHash != hash || info.MediaStamp != stamp) throw new ToolException("视频或字幕已发生变化，请重新打开校准面板。", 409);
            var media = await ProbeAsync(target.Path, token);
            var selected = audioIndex ?? (media.Tracks.FirstOrDefault(track => track.IsDefault) ?? media.Tracks.FirstOrDefault())?.Index;
            if (selected is null || !media.Tracks.Any(track => track.Index == selected)) throw new ToolException("所选音轨已不存在，请重新打开面板。");
            var positions = Positions(media.Duration, startMilliseconds);
            foreach (var pair in _jobs.Where(pair => pair.Value.Status.State != "running" && (DateTime.UtcNow - pair.Value.Created > TimeSpan.FromMinutes(15) || _jobs.Count >= 32))) _jobs.TryRemove(pair);
            Invalidate(target.Path);
            var status = new AlignmentStatus(Guid.NewGuid(), "running", "preparing", 0, positions.Length, null, hash, stamp, selected.Value, [], new(0, 0, 0), 0, "正在准备本地语音识别…");
            var job = new Job(target, subtitleId, status, lifetime.ApplicationStopping);
            _jobs[target.Path] = job;
            _ = Task.Run(() => RunAsync(job, info, media, positions, config, startMilliseconds is null));
            return status;
        }
        catch { _worker.Release(); throw; }
    }

    public async Task<AlignmentStatus?> StatusAsync(MediaTarget target, CancellationToken token)
    {
        if (!_jobs.TryGetValue(target.Path, out var job)) return null;
        if (job.Status.State is "running" or "ready")
        {
            try { await CheckAsync(job, token); }
            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
            { Cancel(job); Set(job, job.Status with { State = "invalid", OffsetMilliseconds = null, Anchors = [], Message = "视频或字幕已变化，旧结果已失效，请重新分析。" }); }
        }
        return job.Status;
    }

    public AlignmentStatus Cancel(string path, Guid id)
    {
        if (!_jobs.TryGetValue(path, out var job) || job.Status.Id != id) throw new ToolException("分析任务已不存在。", 404);
        Cancel(job); return job.Status;
    }

    private static void Cancel(Job job) { if (job.Status.State == "running") job.Cancel.Cancel(); }
    public void Invalidate(string path) { if (_jobs.TryRemove(path, out var job)) Cancel(job); }
    private static void Set(Job job, AlignmentStatus status) => Volatile.Write(ref job.Status, status);

    private async Task RunAsync(Job job, CalibrationInfo info, Media media, double[] positions, Configuration config, bool searchBefore)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var temp = Path.Combine(paths.CachePath, "jav-subtitles-tool", "analysis-" + job.Status.Id.ToString("N"));
        var matcher = new AnchorMatcher(_http, config);
        var points = new List<AlignmentPoint>();
        job.Cancel.CancelAfter(TimeSpan.FromMinutes(5));
        var token = job.Cancel.Token;
        try
        {
            Directory.CreateDirectory(temp);
            var cache = Path.Combine(paths.CachePath, "jav-subtitles-tool");
            var assets = await SpeechAssets.PrepareAsync(cache, token);
            using var detector = new SpeechDetector(cache);
            using var candidates = new SubtitleCandidates(assets, info.Cues);
            foreach (var (position, sample) in positions.Select((position, sample) => (position, sample)))
            {
                token.ThrowIfCancellationRequested(); await CheckAsync(job, token);
                Set(job, job.Status with { Stage = "recognizing", Message = "正在本地识别台词…", ElapsedSeconds = clock.Elapsed.TotalSeconds });
                var pcm = Path.Combine(temp, "clip.pcm");
                // FFmpeg 输入寻址相对于媒体起点；恢复位置后使用播放器的零起点时钟。
                // 填补音频时间戳间隙，防止解码出的 PCM 时长与实际视频时钟不同。
                await MediaProcess.RunAsync(encoder.EncoderPath, ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", Number(position), "-i", job.Target.Path, "-t", Number(Math.Min(30, media.Duration - position)), "-map", "0:" + job.Status.AudioIndex, "-vn", "-threads", "2", "-af", "aresample=async=1:first_pts=0", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "s16le", "-y", pcm], _ => { }, token);
                var speech = await SpeechRecognizer.RecognizeAsync(assets, pcm, temp, (long)Math.Round(position * 1000), detector, token);
                if (speech.Words.Count > 0 && matcher.CanRequest)
                {
                    Set(job, job.Status with { Stage = "selecting", Message = "正在本地筛选字幕候选…" });
                    var selected = candidates.Select(speech, token, CandidateOffset(points.Select(point => point.Anchor)));
                    if (selected.Count > 0)
                    {
                        Set(job, job.Status with { Stage = "matching", Message = "正在核对实际台词与字幕的具体对应…" });
                        var found = await matcher.MatchAsync(speech, selected, info.Cues, token);
                        if (found.Length > 0)
                        {
                            await CheckAsync(job, token);
                            if (job.Status.State == "invalid") return;
                            points.AddRange(found.Select(anchor => new AlignmentPoint(sample, anchor)));
                            var agreed = Consensus(points);
                            if (agreed.Length >= 2)
                            {
                                Set(job, job.Status with { State = "ready", Stage = "complete", Completed = job.Status.Completed + 1, OffsetMilliseconds = Median(agreed.Select(point => point.VideoMilliseconds - point.SubtitleMilliseconds)), Anchors = agreed, Usage = matcher.Usage, ElapsedSeconds = clock.Elapsed.TotalSeconds, Message = $"找到 {agreed.Length} 处具体台词，时间差一致。建议已填入本次调整，请核对对白后保存。" });
                                return;
                            }
                            Set(job, job.Status with { Anchors = points.Select(point => point.Anchor).ToArray(), Message = $"已找到 {points.Count} 处对应，正在读取其他位置核对固定偏移…" });
                            // 对话附近更容易找到第二处清楚台词；仍保留后续位置作为失败后的搜索机会。
                            var proposed = CandidateOffset(found);
                            var nextCue = proposed is null ? null : info.Cues.FirstOrDefault(cue => cue.StartMilliseconds + proposed >= (position + 32) * 1000
                                && cue.StartMilliseconds + proposed < (position + 300) * 1000 && AnchorMatcher.Normalize(cue.Text).Length >= 8);
                            var nearby = Math.Min(searchBefore ? Math.Max(0, position - 45) : nextCue is null ? position + 45 : (nextCue.StartMilliseconds + proposed!.Value) / 1000.0 - 2, Math.Max(0, media.Duration - 30));
                            if (sample + 1 < positions.Length && !positions.Take(sample + 1).Any(previous => Math.Abs(previous - nearby) < 30)) positions[sample + 1] = nearby;
                        }
                    }
                }
                Set(job, job.Status with { Completed = job.Status.Completed + 1, Usage = matcher.Usage, ElapsedSeconds = clock.Elapsed.TotalSeconds });
                if (!matcher.CanRequest) break;
            }
            Set(job, job.Status with { State = "uncertain", Stage = "complete", OffsetMilliseconds = null, Message = (points.Count > 0 ? $"找到 {points.Count} 处候选对应，尚无两句独立台词支持一致的固定偏移。" : matcher.LastReason) + " 可换到清楚的完整对白位置重试，或手动校准。" });
        }
        catch (OperationCanceledException)
        {
            if (job.Status.State != "invalid") Set(job, job.Status with { State = "cancelled", Stage = "complete", Message = lifetime.ApplicationStopping.IsCancellationRequested || clock.Elapsed < TimeSpan.FromMinutes(5) ? "分析已取消。" : "分析超过 5 分钟，请换一处播放位置重试。" });
        }
        catch (Exception ex)
        {
            // 不记录台词、LLM 请求和密钥；来源错误只向当前用户显示可操作的说明。
            logger.LogWarning("字幕自动分析 {JobId} 失败，异常类型 {Type}", job.Status.Id, ex.GetType().Name);
            Set(job, job.Status with { State = "error", Stage = "complete", OffsetMilliseconds = null, Anchors = [], Message = ex is ToolException ? ex.Message : "本地语音识别失败，请检查完整安装包与缓存权限，再换一处位置重试。" });
        }
        finally
        {
            Set(job, job.Status with { Usage = matcher.Usage, ElapsedSeconds = clock.Elapsed.TotalSeconds });
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("清理自动分析临时音频失败：{JobId}", job.Status.Id); }
            job.Cancel.Dispose(); _worker.Release();
        }
    }

    internal static long Median(IEnumerable<long> offsets)
    {
        var values = offsets.Order().ToArray();
        return values.Length % 2 == 1 ? values[values.Length / 2] : (long)Math.Round((values[values.Length / 2 - 1] + values[values.Length / 2]) / 2.0);
    }

    internal static long? CandidateOffset(IEnumerable<AlignmentAnchor> anchors)
    {
        var values = anchors.Select(anchor => anchor.VideoMilliseconds - anchor.SubtitleMilliseconds).ToArray();
        return values.Length > 0 && values.Max() - values.Min() <= 750 ? Median(values) : null;
    }

    internal static AlignmentAnchor[] Consensus(IReadOnlyList<AlignmentPoint> points)
    {
        var ordered = points.OrderBy(point => point.Anchor.VideoMilliseconds - point.Anchor.SubtitleMilliseconds).ToArray();
        var groups = new List<AlignmentAnchor[]>();
        foreach (var start in ordered)
        {
            var offset = start.Anchor.VideoMilliseconds - start.Anchor.SubtitleMilliseconds;
            var independent = new List<AlignmentPoint>();
            foreach (var point in ordered.Where(point => point.Anchor.VideoMilliseconds - point.Anchor.SubtitleMilliseconds >= offset
                && point.Anchor.VideoMilliseconds - point.Anchor.SubtitleMilliseconds <= offset + 750))
                if (!independent.Any(previous => previous.Anchor.CueEndId >= point.Anchor.CueStartId && point.Anchor.CueEndId >= previous.Anchor.CueStartId
                    || previous.Sample == point.Sample && previous.Anchor.WordEndId >= point.Anchor.WordStartId && point.Anchor.WordEndId >= previous.Anchor.WordStartId)) independent.Add(point);
            var group = independent.Select(point => point.Anchor).ToArray();
            if (group.Length >= 2 && group.Select(anchor => anchor.CueStartId).Distinct().Count() >= 2) groups.Add(group);
        }
        if (groups.Count == 0 || groups.Max(group => Median(group.Select(anchor => anchor.VideoMilliseconds - anchor.SubtitleMilliseconds))) - groups.Min(group => Median(group.Select(anchor => anchor.VideoMilliseconds - anchor.SubtitleMilliseconds))) > 750) return [];
        return groups.OrderByDescending(group => group.Length).ThenBy(group => group.Max(anchor => anchor.VideoMilliseconds - anchor.SubtitleMilliseconds) - group.Min(anchor => anchor.VideoMilliseconds - anchor.SubtitleMilliseconds)).First();
    }

    private async Task CheckAsync(Job job, CancellationToken token)
    {
        var info = await subtitles.OpenAsync(job.Target, job.SubtitleId, false, token);
        if (info.CurrentHash != job.Status.CurrentHash || info.MediaStamp != job.Status.MediaStamp) throw new ToolException("视频或字幕发生变化，请重新分析。", 409);
    }

    internal static double[] Positions(double duration, long? startMilliseconds)
    {
        if (!double.IsFinite(duration) || duration <= 0) throw new ToolException("无法读取视频时长。");
        if (startMilliseconds is < 0 || startMilliseconds / 1000.0 >= duration) throw new ToolException("所选播放位置不在视频范围内。");
        return (startMilliseconds is { } start ? new[] { Math.Max(0, start / 1000.0 - 3), start / 1000.0 + 30, start / 1000.0 + 90, start / 1000.0 + 180, start / 1000.0 + 300, start / 1000.0 + 420, start / 1000.0 + 600, start / 1000.0 + 900 }
            : new[] { duration * .05, duration * .5, duration * .5 - 60, duration * .8, duration * .8 + 60, duration * .2, duration * .05 + 60, duration * .2 + 60 })
            .Where(position => position >= 0 && position < duration).Select(position => startMilliseconds is null ? Math.Min(position, Math.Max(0, duration - 30)) : position).Distinct().ToArray();
    }

    private async Task<Media> ProbeAsync(string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var output = await MediaProcess.CaptureAsync(encoder.ProbePath, ["-v", "error", "-show_entries", "format=format_name,start_time,duration:stream=index,codec_type,codec_name,start_time,duration:stream_tags=language,title:stream_disposition=default", "-of", "json", path], timeout.Token);
        using var doc = JsonDocument.Parse(output); var root = doc.RootElement; var format = root.GetProperty("format");
        var tracks = root.GetProperty("streams").EnumerateArray().Where(stream => stream.GetProperty("codec_type").GetString() == "audio").Select(stream =>
        {
            var index = stream.GetProperty("index").GetInt32();
            var label = "音轨 " + index + " · " + (stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() : "");
            if (stream.TryGetProperty("tags", out var tags)) foreach (var key in new[] { "language", "title" }) if (tags.TryGetProperty(key, out var tag)) label += " · " + tag.GetString();
            return new AlignmentAudioTrack(index, label, stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("default", out var primary) && primary.GetInt32() == 1);
        }).ToArray();
        var start = ReadNumber(format, "start_time");
        return new(PlaybackDuration(root), start, tracks);
    }
    internal static double PlaybackDuration(JsonElement root)
    {
        var format = root.GetProperty("format"); var start = ReadNumber(format, "start_time");
        // 流时长表示实际长度；非零起点不能直接从 format.duration 扣除。
        var duration = root.GetProperty("streams").EnumerateArray().Where(stream => stream.GetProperty("codec_type").GetString() == "video")
            .Select(stream => ReadNumber(stream, "duration") is > 0 and var length ? length + Math.Max(0, ReadNumber(stream, "start_time") - start) : 0).DefaultIfEmpty().Max();
        if (duration > 0) return duration;
        duration = ReadNumber(format, "duration");
        // Matroska 的 Segment Duration 从时间戳零点计算，包含开头时间戳空档。
        if (format.TryGetProperty("format_name", out var name) && name.GetString()!.Contains("matroska", StringComparison.Ordinal)) duration -= Math.Max(0, start);
        return Math.Max(0, duration);
    }
    private static double ReadNumber(JsonElement value, string key) => value.TryGetProperty(key, out var field) && double.TryParse(field.ToString(), CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;
    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    public void Dispose() => _http.Dispose();
}
