namespace Jellyfin.Plugin.SubtitlesTool.Core;

public static class SidecarWriter
{
    public static async Task WriteAsync(string target, byte[] bytes, CancellationToken cancellationToken)
    {
        var temp = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, target, true);
        }
        finally { File.Delete(temp); }
    }
}
