using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

internal static class SpeechAssets
{
    private sealed record Asset(string Path, long Bytes, string Sha256);
    public static bool Supported => RuntimeInformation.ProcessArchitecture == Architecture.X64 && (OperatingSystem.IsLinux() || OperatingSystem.IsWindows());
    public static string Archive => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(SpeechAssets).Assembly.Location)!, "speech-assets.zip");

    public static async Task<string> PrepareAsync(string cache, CancellationToken token)
    {
        if (!Supported) throw new ToolException("自动校准需要 Linux x64 或 Windows x64 服务器。");
        if (!File.Exists(Archive)) throw new ToolException("安装包缺少语音识别资源，请重新安装完整发行包。");
        using var manifest = Assembly.GetExecutingAssembly().GetManifestResourceStream("SubtitlesTool.SpeechAssets")!;
        var assets = (await JsonSerializer.DeserializeAsync<Asset[]>(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web), token))!;
        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        var directory = System.IO.Path.Combine(cache, "speech-1.13.8");
        using var zip = ZipFile.OpenRead(Archive);
        foreach (var asset in assets.Where(asset => !asset.Path.Contains('/') || asset.Path.StartsWith(rid + "/", StringComparison.Ordinal)))
        {
            var path = System.IO.Path.Combine(directory, asset.Path);
            if (!await ValidAsync(path, asset, token))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                try
                {
                    using var input = (zip.GetEntry(asset.Path) ?? throw new ToolException("语音识别资源不完整。")).Open();
                    await using (var output = File.Create(temp)) await input.CopyToAsync(output, token);
                    if (!await ValidAsync(temp, asset, token)) throw new ToolException("语音识别资源校验失败，请重新安装插件。");
                    File.Move(temp, path, true);
                }
                finally { File.Delete(temp); }
            }
        }
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(System.IO.Path.Combine(directory, rid, "bin/sherpa-onnx-offline"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    private static async Task<bool> ValidAsync(string path, Asset asset, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Bytes) return false;
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)) == asset.Sha256;
    }
}
