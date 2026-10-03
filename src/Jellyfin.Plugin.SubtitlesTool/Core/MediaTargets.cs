using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed record MediaTarget(Guid Id, Guid VersionId, string VersionName, Video Item, string Path, int PartNumber, int PartCount, long? RunTimeTicks)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string DefaultQuery => JavIdentity.DefaultQuery(Path, Item.Name ?? "", PartNumber, PartCount);
}

public sealed class MediaTargets(ILibraryManager library, IMediaSourceManager sources, Func<string[]> configuredRoots)
{
    public static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public IReadOnlyList<MediaTarget> Enumerate(Video root)
    {
        var targets = new List<MediaTarget>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var source in sources.GetStaticMediaSources(root, false))
        {
            if (source.Protocol != MediaProtocol.File || !Guid.TryParse(source.Id, out var id)) continue;
            var version = id == root.Id ? root : library.GetItemById(id) as Video;
            if (version is null) continue;
            var parts = new[] { version }.Concat(version.GetAdditionalParts()).ToArray();
            for (var index = 0; index < parts.Length; index++)
            {
                var part = parts[index];
                if (part.VideoType != VideoType.VideoFile || part.IsShortcut || !part.IsFileProtocol || string.IsNullOrWhiteSpace(part.Path)
                    || Path.GetExtension(part.Path).Equals(".strm", StringComparison.OrdinalIgnoreCase)) continue;
                var path = ResolvePath(part.Path);
                if (!IsInScope(path) || !File.Exists(path) || !seen.Add(path)) continue;
                // Jellyfin 的附属分段为 Video；主影片的访问权限覆盖已验证的归属关系。
                targets.Add(new MediaTarget(part.Id, version.Id, source.Name ?? Path.GetFileNameWithoutExtension(version.Path), part, path,
                    JavIdentity.ExtractPart(Path.GetFileNameWithoutExtension(part.Path)) ?? index + 1, parts.Length,
                    index == 0 ? source.RunTimeTicks : part.RunTimeTicks));
            }
        }
        return targets;
    }

    public bool IsInScope(string path) => configuredRoots().Where(root => !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root)).Any(root =>
    {
        var resolved = ResolvePath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(resolved + Path.DirectorySeparatorChar, PathComparison);
    });

    public static string ResolvePath(string value)
    {
        var full = Path.GetFullPath(value);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var component in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null) current = info.ResolveLinkTarget(true)!.FullName;
        }
        return current;
    }
}
