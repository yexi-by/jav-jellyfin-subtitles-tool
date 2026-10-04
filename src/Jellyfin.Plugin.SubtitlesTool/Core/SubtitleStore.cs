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
            .Where(path => BelongsTo(stem, Path.GetFileName(path)) && SubtitleExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => new SavedSubtitle(Path.GetFileName(path), Path.GetFileName(path), SubtitleFormats.Normalize(Path.GetExtension(path)), SubtitleFormats.Supports(Path.GetExtension(path)))).ToArray();
    }

    public async Task<string> SaveDownloadAsync(MediaTarget target, SourceSubtitle subtitle, byte[] bytes, CancellationToken cancellationToken)
    {
        var current = new SubtitleDocument(bytes, subtitle.Format).Shift(0);
        var suffix = "." + subtitle.Source + (subtitle.Source == "subtitlecat" && subtitle.Language is not null ? "." + SubtitleFormats.FileLanguage(subtitle.Language) : "");
        var path = Path.Combine(Path.GetDirectoryName(target.Path)!, Path.GetFileNameWithoutExtension(target.Path) + suffix + "." + subtitle.Format);
        using var gate = await MediaFiles.EnterAsync(target.Path, cancellationToken);
        var existing = List(target).Select(value => Path.Combine(Path.GetDirectoryName(target.Path)!, value.FileName)).ToArray();
        if (existing.Append(path).Any(value => new FileInfo(value).LinkTarget is not null)) throw new ToolException("现有字幕是链接，请先在媒体目录中处理该链接。", 409);
        var directory = RecordDirectory(target, Path.GetFileName(path));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "state.json");
        var oldStates = existing.Select(value => Path.Combine(RecordDirectory(target, Path.GetFileName(value)), "state.json")).ToArray();
        var previous = new Dictionary<string, byte[]>(MediaTargets.PathComparer);
        foreach (var value in existing) previous[value] = await ReadSubtitle(value, cancellationToken);
        foreach (var value in oldStates.Append(statePath).Distinct(MediaTargets.PathComparer).Where(File.Exists)) previous[value] = await File.ReadAllBytesAsync(value, cancellationToken);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, current, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteState(directory, new CalibrationState(MediaFiles.Stamp(target.Path), Hash(current)), cancellationToken);
            // 输出已经就绪后，先移除其他字幕，再发布这一份；处理中也不会新增第二份字幕。
            foreach (var value in existing.Where(value => !MediaTargets.PathComparer.Equals(value, path))) File.Delete(value);
            File.Move(temp, path, true);
            foreach (var value in oldStates.Where(value => !MediaTargets.PathComparer.Equals(value, statePath) && File.Exists(value))) File.Delete(value);
        }
        catch
        {
            if (!previous.ContainsKey(path)) File.Delete(path);
            if (!previous.ContainsKey(statePath)) File.Delete(statePath);
            foreach (var (value, content) in previous) await SidecarWriter.WriteAsync(value, content, CancellationToken.None);
            throw;
        }
        finally { File.Delete(temp); }
        return path;
    }

    public async Task<CalibrationInfo> OpenAsync(MediaTarget target, string subtitleId, bool restart, CancellationToken cancellationToken)
    {
        using var gate = await MediaFiles.EnterAsync(target.Path, cancellationToken);
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
        using var gate = await MediaFiles.EnterAsync(target.Path, cancellationToken);
        var path = Resolve(target, subtitleId);
        var directory = RecordDirectory(target, subtitleId);
        var state = await ReadState(directory, cancellationToken) ?? throw new ToolException("请重新打开校准面板。", 409);
        var before = await ReadSubtitle(path, cancellationToken);
        if (Hash(before) != currentHash || state.CurrentHash != currentHash || MediaFiles.Stamp(target.Path) != mediaStamp || state.MediaStamp != mediaStamp)
            throw new ToolException("视频或字幕已发生变化，请重新打开校准面板。", 409);
        var bytes = new SubtitleDocument(before, SubtitleFormats.Normalize(Path.GetExtension(path))).Shift(offsetMilliseconds);
        await SidecarWriter.WriteAsync(path, bytes, cancellationToken);
        try { await WriteState(directory, state with { CurrentHash = Hash(bytes) }, CancellationToken.None); }
        catch { await SidecarWriter.WriteAsync(path, before, CancellationToken.None); throw; }
        return path;
    }

    private static string Resolve(MediaTarget target, string subtitleId)
    {
        var stem = Path.GetFileNameWithoutExtension(target.Path);
        if (string.IsNullOrWhiteSpace(subtitleId) || subtitleId != Path.GetFileName(subtitleId)
            || !BelongsTo(stem, subtitleId)
            || !SubtitleFormats.Supports(Path.GetExtension(subtitleId))) throw new ToolException("字幕不属于当前视频，或格式不支持校准。", 404);
        var path = Path.Combine(Path.GetDirectoryName(target.Path)!, subtitleId);
        if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null) throw new ToolException("字幕文件不存在，或是不可编辑的链接。", 404);
        return path;
    }
    private static bool BelongsTo(string stem, string fileName)
    {
        if (!fileName.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = fileName[(stem.Length + 1)..];
        // 来源标记前还有名称时，属于同目录中名称更长的另一个视频版本。
        return !suffix.Contains(".xunlei.", StringComparison.OrdinalIgnoreCase) && !suffix.Contains(".subtitlecat.", StringComparison.OrdinalIgnoreCase);
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
    private static Task WriteState(string directory, CalibrationState state, CancellationToken token) => SidecarWriter.WriteAsync(Path.Combine(directory, "state.json"), JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), token);
}
