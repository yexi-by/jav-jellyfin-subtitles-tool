namespace Jellyfin.Plugin.SubtitlesTool.Core;

public static class SidecarWriter
{
    public static async Task WriteAsync(string target, bool overwrite, byte[] bytes, CancellationToken cancellationToken)
    {
        if (!overwrite && File.Exists(target)) throw new ToolException($"同名字幕 {Path.GetFileName(target)} 已存在。替换吗？", 409);
        var temp = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temp, target, overwrite); }
            catch (IOException) when (!overwrite && File.Exists(target)) { throw new ToolException($"同名字幕 {Path.GetFileName(target)} 已存在。替换吗？", 409); }
        }
        finally { File.Delete(temp); }
    }
}
