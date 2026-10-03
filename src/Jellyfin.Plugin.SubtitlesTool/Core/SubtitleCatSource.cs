using System.Text;
using AngleSharp.Html.Parser;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed class SubtitleCatSource(HttpClient client) : IDisposable
{
    private static readonly Uri Site = new("https://www.subtitlecat.com/");
    private readonly SourceHttp _http = new(client, "SubtitleCat", Allowed);

    public async Task<SourceSubtitle[]> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var uri = new Uri(Site, "index.php?search=" + Uri.EscapeDataString(query));
        var html = Encoding.UTF8.GetString(await _http.GetAsync(uri, true, cancellationToken));
        var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);
        var table = document.QuerySelector("table.sub-table");
        if (table is null) { _http.Invalidate(uri); throw new ToolException("SubtitleCat 搜索页面暂时无法识别，请稍后重试。", 502); }
        return table.QuerySelectorAll("a[href]")
            .Select(link => (Name: link.TextContent.Trim(), Url: Link(link.GetAttribute("href"))))
            .Where(item => item.Url is not null && item.Url.AbsolutePath.StartsWith("/subs/", StringComparison.Ordinal) && item.Url.AbsolutePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => item.Url!.AbsoluteUri)
            .Select(item => new SourceSubtitle("subtitlecat", item.Name, "srt", null, item.Url)).ToArray();
    }

    public async Task<SourceSubtitle[]> LanguagesAsync(SourceSubtitle entry, string language, CancellationToken cancellationToken)
    {
        var html = Encoding.UTF8.GetString(await _http.GetAsync(entry.Url!, true, cancellationToken));
        var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);
        // 只读取现成的 Download 链接，Translate 按钮不代表已有字幕。
        var links = document.QuerySelectorAll("a[id^='download_'][href]");
        if (links.Length == 0 && document.QuerySelector("button[onclick*='translate_from_server_folder']") is null)
        {
            _http.Invalidate(entry.Url!);
            throw new ToolException("SubtitleCat 字幕详情页面暂时无法识别。", 502);
        }
        return links.Select(link => (Language: link.Id!["download_".Length..], Url: Link(link.GetAttribute("href"))))
            .Where(item => item.Url is not null && item.Url.AbsolutePath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                && (language == "all" || (language == "zh" ? item.Language is "zh-CN" or "zh-TW" : string.Equals(item.Language, language, StringComparison.OrdinalIgnoreCase))))
            .DistinctBy(item => item.Url!.AbsoluteUri)
            .Select(item => entry with { Language = item.Language, Url = item.Url }).ToArray();
    }

    private static Uri? Link(string? href) => Uri.TryCreate(Site, href, out var uri) && Allowed(uri) ? uri : null;
    private static bool Allowed(Uri uri) => (uri.Scheme is "https" or "http") && string.IsNullOrEmpty(uri.UserInfo) && (uri.Host is "www.subtitlecat.com" or "subtitlecat.com");
    public Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken) => _http.GetAsync(uri, false, cancellationToken);
    public void Dispose() => _http.Dispose();
}
