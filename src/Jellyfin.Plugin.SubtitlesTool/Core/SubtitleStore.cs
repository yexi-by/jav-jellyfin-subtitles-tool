using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed record SavedSubtitle(string Id, string FileName, string Format, bool CanCalibrate);
public sealed record CalibrationInfo(string FileName, string Format, long OffsetMilliseconds, string CurrentHash, string MediaStamp, IReadOnlyList<SubtitleCue> Cues);

public sealed class SubtitleStore(string dataPath) : IDisposable
{
    private readonly SemaphoreSlim[] _writes = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".smi", ".sami", ".sub", ".idx", ".sup", ".mks", ".ttml"];
    private sealed record CalibrationState(string MediaStamp, string CurrentHash, long OffsetMilliseconds);

    public SavedSubtitle[] List(MediaTarget target)
    {
        var prefix = Path.GetFileNameWithoutExtension(target.Path) + ".";
        return Directory.EnumerateFiles(Path.GetDirectoryName(target.Path)!)
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && SubtitleExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => new SavedSubtitle(Path.GetFileName(path), Path.GetFileName(path), SubtitleFormats.Normalize(Path.GetExtension(path)), SubtitleFormats.Supports(Path.GetExtension(path)))).ToArray();
    }

    public async Task<string> SaveDownloadAsync(MediaTarget target, SourceSubtitle subtitle, byte[] bytes, bool overwrite, CancellationToken cancellationToken)
    {
        var current = new SubtitleDocument(bytes, subtitle.Format).Shift(0);
        var suffix = "." + subtitle.Source + (subtitle.Source == "subtitlecat" && subtitle.Language is not null ? "." + SubtitleFormats.FileLanguage(subtitle.Language) : "");
        var path = Path.Combine(Path.GetDirectoryName(target.Path)!, Path.GetFileNameWithoutExtension(target.Path) + suffix + "." + subtitle.Format);
        var gate = Gate(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!overwrite && File.Exists(path)) throw new ToolException($"同名字幕 {Path.GetFileName(path)} 已存在。替换吗？", 409);
            if (new FileInfo(path).LinkTarget is not null) throw new ToolException("现有字幕是链接，请先在媒体目录中处理该链接。", 409);
            var directory = RecordDirectory(target, Path.GetFileName(path));
            Directory.CreateDirectory(directory);
            var originalPath = Path.Combine(directory, "original");
            var statePath = Path.Combine(directory, "state.json");
            var previous = new Dictionary<string, byte[]?>();
            foreach (var file in new[] { path, originalPath, statePath })
            {
                if (new FileInfo(file) is { Exists: true, Length: > 20 * 1024 * 1024 }) throw new ToolException("现有字幕或校准记录异常大，请检查文件后重试。");
                previous[file] = File.Exists(file) ? await File.ReadAllBytesAsync(file, cancellationToken) : null;
            }
            var published = false;
            try
            {
                await SidecarWriter.WriteAsync(path, overwrite, current, cancellationToken);
                published = true;
                // 下载时立即保留原稿，替换下载也从这一份新原稿开始校准。
                await SidecarWriter.WriteAsync(originalPath, true, bytes, CancellationToken.None);
                await WriteState(directory, new CalibrationState(MediaStamp(target.Path), Hash(current), 0), CancellationToken.None);
            }
            catch when (published)
            {
                foreach (var (file, content) in previous)
                {
                    if (content is null) File.Delete(file);
                    else await SidecarWriter.WriteAsync(file, true, content, CancellationToken.None);
                }
                throw;
            }
            return path;
        }
        finally { gate.Release(); }
    }

    public async Task<CalibrationInfo> OpenAsync(MediaTarget target, string subtitleId, bool restart, CancellationToken cancellationToken)
    {
        var path = Resolve(target, subtitleId);
        var gate = Gate(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var bytes = await ReadSubtitle(path, cancellationToken);
            var hash = Hash(bytes);
            var stamp = MediaStamp(target.Path);
            var directory = RecordDirectory(target, subtitleId);
            var state = restart ? null : await ReadState(directory, cancellationToken);
            if (state is not null && (state.MediaStamp != stamp || state.CurrentHash != hash) && !restart)
                throw new ToolException("视频或字幕已被替换。确认以当前字幕重新开始校准后，原来的校准记录将被更新。", 409);
            if (state is null || restart)
            {
                _ = new SubtitleDocument(bytes, SubtitleFormats.Normalize(Path.GetExtension(path)));
                Directory.CreateDirectory(directory);
                await SidecarWriter.WriteAsync(Path.Combine(directory, "original"), true, bytes, cancellationToken);
                state = new CalibrationState(stamp, hash, 0);
                await WriteState(directory, state, cancellationToken);
            }
            if (!File.Exists(Path.Combine(directory, "original"))) throw new ToolException("校准原稿已缺失，请确认以当前字幕重新开始。", 409);
            var original = await ReadSubtitle(Path.Combine(directory, "original"), cancellationToken);
            var format = SubtitleFormats.Normalize(Path.GetExtension(path));
            return new CalibrationInfo(Path.GetFileName(path), format, state.OffsetMilliseconds, hash, stamp, new SubtitleDocument(original, format).Cues);
        }
        finally { gate.Release(); }
    }

    public async Task<string> CalibrateAsync(MediaTarget target, string subtitleId, long offsetMilliseconds, string currentHash, string mediaStamp, CancellationToken cancellationToken)
    {
        var path = Resolve(target, subtitleId);
        var gate = Gate(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var directory = RecordDirectory(target, subtitleId);
            var state = await ReadState(directory, cancellationToken) ?? throw new ToolException("请重新打开校准面板。", 409);
            var before = await ReadSubtitle(path, cancellationToken);
            if (Hash(before) != currentHash || state.CurrentHash != currentHash || MediaStamp(target.Path) != mediaStamp || state.MediaStamp != mediaStamp)
                throw new ToolException("视频或字幕已发生变化，请重新打开校准面板。", 409);
            var original = await ReadSubtitle(Path.Combine(directory, "original"), cancellationToken);
            var bytes = new SubtitleDocument(original, SubtitleFormats.Normalize(Path.GetExtension(path))).Shift(offsetMilliseconds);
            await SidecarWriter.WriteAsync(path, true, bytes, cancellationToken);
            try
            {
                // 文件发布后完成记录写入；若写入失败则恢复旧字幕，避免只更新其中一个文件。
                await WriteState(directory, state with { CurrentHash = Hash(bytes), OffsetMilliseconds = offsetMilliseconds }, CancellationToken.None);
            }
            catch
            {
                await SidecarWriter.WriteAsync(path, true, before, CancellationToken.None);
                throw;
            }
            return path;
        }
        finally { gate.Release(); }
    }

    private static string Resolve(MediaTarget target, string subtitleId)
    {
        if (string.IsNullOrWhiteSpace(subtitleId) || subtitleId != Path.GetFileName(subtitleId)
            || !subtitleId.StartsWith(Path.GetFileNameWithoutExtension(target.Path) + ".", StringComparison.OrdinalIgnoreCase)
            || !SubtitleFormats.Supports(Path.GetExtension(subtitleId))) throw new ToolException("字幕不属于当前视频，或格式不支持校准。", 404);
        var path = Path.Combine(Path.GetDirectoryName(target.Path)!, subtitleId);
        if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null) throw new ToolException("字幕文件不存在，或是不可编辑的链接。", 404);
        return path;
    }

    private static async Task<byte[]> ReadSubtitle(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 20 * 1024 * 1024) throw new ToolException("字幕文件不存在、为空或异常大。", 404);
        return await File.ReadAllBytesAsync(path, token);
    }
    private static string MediaStamp(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new ToolException("视频文件已不存在。", 404);
        return info.Length + ":" + info.LastWriteTimeUtc.Ticks;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private string RecordDirectory(MediaTarget target, string name) => Path.Combine(dataPath, "calibration", Hash(Encoding.UTF8.GetBytes(target.Path + "\n" + name)));
    private SemaphoreSlim Gate(string path) => _writes[(uint)MediaTargets.PathComparer.GetHashCode(path) % _writes.Length];
    private static async Task<CalibrationState?> ReadState(string directory, CancellationToken token)
    {
        var path = Path.Combine(directory, "state.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CalibrationState>(await File.ReadAllBytesAsync(path, token), JsonOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new ToolException("校准记录损坏，请检查插件数据目录中的记录。", 409); }
    }
    private static Task WriteState(string directory, CalibrationState state, CancellationToken token) => SidecarWriter.WriteAsync(Path.Combine(directory, "state.json"), true, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), token);
    public void Dispose() { foreach (var gate in _writes) gate.Dispose(); }
}
