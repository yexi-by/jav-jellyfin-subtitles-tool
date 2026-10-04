namespace Jellyfin.Plugin.SubtitlesTool.Core;

/// <summary>按视频路径串行保存字幕和指纹文件。</summary>
public static class MediaFiles
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public static string Stamp(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new ToolException("视频文件已不存在。", 404);
        return info.Length + ":" + info.LastWriteTimeUtc.Ticks;
    }
    public static async Task<IDisposable> EnterAsync(string path, CancellationToken token)
    {
        var gate = Gates[(uint)MediaTargets.PathComparer.GetHashCode(path) % Gates.Length];
        await gate.WaitAsync(token);
        return new Lease(gate);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
}
