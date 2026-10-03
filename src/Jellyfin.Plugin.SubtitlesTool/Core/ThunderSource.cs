using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed class ThunderSource(HttpClient client) : IDisposable
{
    private readonly SourceHttp _http = new(client, "迅雷字幕", Allowed);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SourceSubtitle[]> SearchAsync(string value, bool byFile, CancellationToken cancellationToken)
    {
        var uri = new Uri("https://api-shoulei-ssl.xunlei.com/oracle/subtitle?" + (byFile ? "gcid=" : "name=") + Uri.EscapeDataString(value));
        try
        {
            var payload = JsonSerializer.Deserialize<ThunderResponse>(await _http.GetAsync(uri, true, cancellationToken), JsonOptions);
            if (payload is null || payload.Code != 0 || payload.Result != "ok") throw new ToolException("迅雷字幕返回了异常结果，请重试。", 502);
            return (payload.Data ?? []).OrderByDescending(item => item.Score).ThenByDescending(item => item.FingerprintScore)
                .Select(item => new SourceSubtitle("xunlei", item.Name ?? "未命名字幕", SubtitleFormats.Normalize(item.Ext ?? ""), null,
                    Uri.TryCreate(item.Url, UriKind.Absolute, out var url) && Allowed(url) ? url : null)).ToArray();
        }
        catch (JsonException) { _http.Invalidate(uri); throw new ToolException("迅雷字幕返回的数据无法识别，请稍后重试。", 502); }
        catch (ToolException) { _http.Invalidate(uri); throw; }
    }

    public Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken) => _http.GetAsync(uri, false, cancellationToken);
    private static bool Allowed(Uri uri) => (uri.Scheme is "https" or "http") && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host == "api-shoulei-ssl.xunlei.com" || uri.Host == "subtitle.v.geilijiasu.com" || uri.Host == "subtitle.v.xunlei.com" || uri.Host == "sub.xmp.sandai.net");
    public void Dispose() => _http.Dispose();

    private sealed class ThunderResponse
    {
        public int Code { get; set; }
        public string? Result { get; set; }
        public ThunderItem[]? Data { get; set; }
    }
    private sealed class ThunderItem
    {
        public string? Url { get; set; }
        public string? Name { get; set; }
        public string? Ext { get; set; }
        public int Score { get; set; }
        [JsonPropertyName("fingerprintf_score")] public double FingerprintScore { get; set; }
    }
}
