using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed record SavedSubtitle(string Id, string FileName, string Format, bool CanCalibrate);
public sealed record CalibrationInfo(string FileName, string Format, string CurrentHash, string MediaStamp, IReadOnlyList<SubtitleCue> Cues);

public sealed class SubtitleStore(string dataPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".smi", ".sami", ".sub", ".idx", ".sup", ".mks", ".ttml"];
    private sealed record CalibrationState(string MediaStamp, string CurrentHash);

    public SavedSubtitle[] List(MediaTarget target)
    {
        var stem = Path.GetFileNameWithoutExtension(target.Path);
        return Directory.EnumerateFiles(Path.GetDirectoryName(target.Path)!)
            .Where(path => Path.GetFileName(path).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) && SubtitleExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => new SavedSubtitle(Path.GetFileName(path), Path.GetFileName(path), SubtitleFormats.Normalize(Path.GetExtension(path)), SubtitleFormats.Supports(Path.GetExtension(path)))).ToArray();
    }

    public async Task<string> SaveDownloadAsync(MediaTarget target, SourceSubtitle subtitle, byte[] bytes, bool overwrite, CancellationToken cancellationToken)
    {
        VideoTrimmer.CheckAvailable(target.Path);
        var current = new SubtitleDocument(bytes, subtitle.Format).Shift(0);
        var suffix = "." + subtitle.Source + (subtitle.Source == "subtitlecat" && subtitle.Language is not null ? "." + SubtitleFormats.FileLanguage(subtitle.Language) : "");
        var path = Path.Combine(Path.GetDirectoryName(target.Path)!, Path.GetFileNameWithoutExtension(target.Path) + suffix + "." + subtitle.Format);
        using var gate = await MediaFiles.EnterAsync(target.Path, cancellationToken);
        VideoTrimmer.CheckAvailable(target.Path);
        if (!overwrite && File.Exists(path)) throw new ToolException($"同名字幕 {Path.GetFileName(path)} 已存在。替换吗？", 409);
        if (new FileInfo(path).LinkTarget is not null) throw new ToolException("现有字幕是链接，请先在媒体目录中处理该链接。", 409);
        var directory = RecordDirectory(target, Path.GetFileName(path));
        Directory.CreateDirectory(directory);
        var previous = File.Exists(path) ? await ReadSubtitle(path, cancellationToken) : null;
        await SidecarWriter.WriteAsync(path, overwrite, current, cancellationToken);
        try { await WriteState(directory, new CalibrationState(MediaFiles.Stamp(target.Path), Hash(current)), CancellationToken.None); }
        catch
        {
            if (previous is null) File.Delete(path);
            else await SidecarWriter.WriteAsync(path, true, previous, CancellationToken.None);
            throw;
        }
        return path;
    }

    public async Task<CalibrationInfo> OpenAsync(MediaTarget target, string subtitleId, bool restart, CancellationToken cancellationToken)
    {
        VideoTrimmer.CheckAvailable(target.Path);
        using var gate = await MediaFiles.EnterAsync(target.Path, cancellationToken);
        VideoTrimmer.CheckAvailable(target.Path);
        var path = Resolve(target, subtitleId);
        var bytes = await ReadSubtitle(path, cancellationToken);
        var hash = Hash(bytes);
        var stamp = MediaFiles.Stamp(target.Path);
        var directory = RecordDirectory(target, subtitleId);
        var state = restart ? null : await ReadState(directory, cancellationToken);
        if (state is not null && (state.MediaStamp != stamp || state.CurrentHash != hash))
            throw new ToolException("视频或字幕已被替换，请确认以当前字幕继续校准。", 409);
        var format = SubtitleFormats.Normalize(Path.GetExtension(path));
        var document = new SubtitleDocument(bytes, format);
        Directory.CreateDirectory(directory);
        if (state is null) await WriteState(directory, new CalibrationState(stamp, hash), cancellationToken);
        return new CalibrationInfo(Path.GetFileName(path), format, hash, stamp, document.Cues);
    }

    public async Task<string> CalibrateAsync(MediaTarget target, string subtitleId, long offsetMilliseconds, string currentHash, string mediaStamp, CancellationToken cancellationToken)
    {
        VideoTrimmer.CheckAvailable(target.Path);
        using var gate = await MediaFiles.EnterAsync(target.Path, cancellationToken);
        VideoTrimmer.CheckAvailable(target.Path);
        var path = Resolve(target, subtitleId);
        var directory = RecordDirectory(target, subtitleId);
        var state = await ReadState(directory, cancellationToken) ?? throw new ToolException("请重新打开校准面板。", 409);
        var before = await ReadSubtitle(path, cancellationToken);
        if (Hash(before) != currentHash || state.CurrentHash != currentHash || MediaFiles.Stamp(target.Path) != mediaStamp || state.MediaStamp != mediaStamp)
            throw new ToolException("视频或字幕已发生变化，请重新打开校准面板。", 409);
        var bytes = new SubtitleDocument(before, SubtitleFormats.Normalize(Path.GetExtension(path))).Shift(offsetMilliseconds);
        await SidecarWriter.WriteAsync(path, true, bytes, cancellationToken);
        try { await WriteState(directory, state with { CurrentHash = Hash(bytes) }, CancellationToken.None); }
        catch { await SidecarWriter.WriteAsync(path, true, before, CancellationToken.None); throw; }
        return path;
    }

    public void ForgetVideo(MediaTarget target)
    {
        foreach (var subtitle in List(target)) File.Delete(Path.Combine(RecordDirectory(target, subtitle.Id), "state.json"));
    }

    private static string Resolve(MediaTarget target, string subtitleId)
    {
        var stem = Path.GetFileNameWithoutExtension(target.Path);
        if (string.IsNullOrWhiteSpace(subtitleId) || subtitleId != Path.GetFileName(subtitleId)
            || !subtitleId.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)
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
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private string RecordDirectory(MediaTarget target, string name) => Path.Combine(dataPath, "calibration", Hash(Encoding.UTF8.GetBytes(target.Path + "\n" + name)));
    private static async Task<CalibrationState?> ReadState(string directory, CancellationToken token)
    {
        var path = Path.Combine(directory, "state.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CalibrationState>(await File.ReadAllBytesAsync(path, token), JsonOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new ToolException("校准记录损坏，请检查插件数据目录中的记录。", 409); }
    }
    private static Task WriteState(string directory, CalibrationState state, CancellationToken token) => SidecarWriter.WriteAsync(Path.Combine(directory, "state.json"), true, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), token);
}
