using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SubtitlesTool.Tests;

public sealed class SubtitleSourceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static HttpResponseMessage Html(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static MediaTarget Target(string path, int part = 1, int count = 1, Guid? version = null)
    {
        var id = Guid.NewGuid(); return new(id, version ?? id, "版本", new Video { Id = id, Path = path }, path, part, count, 60_000_000);
    }

    [Theory]
    [InlineData("ssni497-1080p.mp4", "SSNI-497")]
    [InlineData("[FC2-PPV-1234567]-cd2.mp4", "FC2-PPV-1234567")]
    [InlineData("HEYZO_1234.mkv", "HEYZO-1234")]
    public void JavCodesKeepTheirIdentity(string name, string expected) => Assert.Equal(expected, JavIdentity.Extract(name));

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task SubtitleCatTriesCompactCodeWhenTheRequestedLanguageIsMissing(bool primaryHasChinese)
    {
        var target = Target("/media/BEB-084.mp4");
        var queries = new List<string>(); var events = new List<JsonElement>();
        using var thunder = new ThunderSource(new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { code = 0, data = Array.Empty<object>() })))));
        using var cat = new SubtitleCatSource(new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/index.php")
            {
                var term = Uri.UnescapeDataString(request.RequestUri.Query[8..]); queries.Add(term);
                var page = term == "BEB084" ? "compact" : "primary";
                return Task.FromResult(Html($"<table class='sub-table'><tr><td><a href='/subs/1/{page}.html'>BEB084</a><a href='/subs/1/wrong.html'>BEB-085</a><a href='/subs/1/uncoded.html'>其他电影</a></td></tr></table>"));
            }
            Assert.DoesNotContain("wrong", path);
            Assert.DoesNotContain("uncoded", path);
            var language = primaryHasChinese || path.EndsWith("compact.html", StringComparison.Ordinal) ? "zh-CN" : "en";
            return Task.FromResult(Html($"<a id='download_{language}' href='/subs/1/{language}.srt'>Download</a>"));
        })));
        using var hashes = new HashRecords(); using var search = new SubtitleSearch(thunder, cat, hashes);
        await search.SearchAsync(target, [target], "BEB-084", "zh", false, value => { events.Add(JsonSerializer.SerializeToElement(value, JsonOptions)); return Task.CompletedTask; }, default);
        Assert.Equal(primaryHasChinese ? ["BEB-084"] : new[] { "BEB-084", "BEB084" }, queries);
        var item = Assert.Single(events, item => item.GetProperty("type").GetString() == "candidates");
        Assert.Equal("zh-CN", item.GetProperty("candidates")[0].GetProperty("language").GetString());
    }

    [Fact]
    public async Task NumberPaddingMatchesAndRoutesTheCandidateToItsOwnPart()
    {
        var first = Target("/media/IKUNA-001-cd1.mp4", 1, 2); var second = Target("/media/IKUNA-001-cd2.mp4", 2, 2, first.VersionId);
        var events = new List<JsonElement>(); var details = new List<string>();
        using var thunder = new ThunderSource(new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { code = 0, data = Array.Empty<object>() })))));
        using var cat = new SubtitleCatSource(new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/index.php") return Task.FromResult(Html("<table class='sub-table'><tr><td><a href='/subs/1/IKUNA-01-CD2.html'>IKUNA-01 CD2</a><a href='/subs/1/other.html'>IKUNA-002</a><a href='/subs/1/prefix.html'>OTHER-001</a></td></tr></table>"));
            details.Add(path);
            return Task.FromResult(Html("<a id='download_zh-CN' href='/subs/1/IKUNA-01-CD2-zh-CN.srt'>Download</a>"));
        })));
        using var hashes = new HashRecords(); using var search = new SubtitleSearch(thunder, cat, hashes);
        await search.SearchAsync(first, [first, second], "IKUNA-001 CD1", "zh", false, value => { events.Add(JsonSerializer.SerializeToElement(value, JsonOptions)); return Task.CompletedTask; }, default);
        Assert.Equal(["/subs/1/IKUNA-01-CD2.html"], details);
        var item = Assert.Single(events, item => item.GetProperty("type").GetString() == "candidates");
        Assert.Equal(second.Id.ToString("N"), item.GetProperty("targetId").GetString());
        var id = item.GetProperty("candidates")[0].GetProperty("id").GetString()!;
        Assert.Equal(second.Path, search.Resolve(id, second.Path).MediaPath);
        Assert.Throws<ToolException>(() => search.Resolve(id, first.Path));
    }

    [Fact]
    public async Task MissingSubtitleCatResultsUseABoundedSetOfDistinctQueries()
    {
        var target = Target("/media/IKUNA-001.mp4"); var queries = new List<string>();
        using var thunder = new ThunderSource(new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { code = 0, data = Array.Empty<object>() })))));
        using var cat = new SubtitleCatSource(new HttpClient(new Handler((request, _) =>
        {
            queries.Add(Uri.UnescapeDataString(request.RequestUri!.Query[8..]));
            return Task.FromResult(Html("<table class='sub-table'></table>"));
        })));
        using var hashes = new HashRecords(); using var search = new SubtitleSearch(thunder, cat, hashes);
        await search.SearchAsync(target, [target], "IKUNA-001", "zh", false, _ => Task.CompletedTask, default);
        Assert.Equal(["IKUNA-001", "IKUNA001", "IKUNA 001", "IKUNA-1", "IKUNA1", "IKUNA 1"], queries);
    }

    [Fact]
    public async Task ThunderIgnoresLanguageFieldsAndQueriesNameOrGcidExplicitly()
    {
        var queries = new List<string>();
        using var source = new ThunderSource(new HttpClient(new Handler((request, _) =>
        {
            queries.Add(request.RequestUri!.Query);
            return Task.FromResult(Json(new { code = 0, result = "ok", data = new[]
            {
                new { name = "ABC-123.srt", url = "https://subtitle.v.geilijiasu.com/a.srt", ext = "srt", languages = new[] { "默认" } },
                new { name = "ABC-123.en.ass", url = "https://subtitle.v.geilijiasu.com/b.ass", ext = "ass", languages = new[] { "en" } },
                new { name = "ABC-123.中文.srt", url = "https://subtitle.v.geilijiasu.com/c.srt", ext = "srt", languages = Array.Empty<string>() }
            } }));
        })));
        var items = await source.SearchAsync("ABC-123", false, default);
        Assert.Equal(3, items.Length); Assert.All(items, item => Assert.Null(item.Language));
        await source.SearchAsync(new string('A', 40), true, default);
        Assert.Equal(["?name=ABC-123", "?gcid=" + new string('A', 40)], queries);
    }

    [Fact]
    public async Task InvalidSourceResponsesCanBeRetriedImmediately()
    {
        var calls = 0;
        using var thunder = new ThunderSource(new HttpClient(new Handler((_, _) => Task.FromResult(++calls == 1
            ? Html("<html>Temporary failure</html>") : Json(new { code = 0, result = "ok", data = Array.Empty<object>() })))));
        await Assert.ThrowsAsync<ToolException>(() => thunder.SearchAsync("ABC-123", false, default));
        Assert.Empty(await thunder.SearchAsync("ABC-123", false, default));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedSourceDoesNotHideThunderResultsAndCandidatesAreBoundToTheirPart(bool timeout)
    {
        var first = Target(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-cd1.mp4"), 1, 2);
        var second = Target(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-cd2.mp4"), 2, 2, first.VersionId);
        using var thunder = new ThunderSource(new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { code = 0, result = "ok", data = new[]
        {
            new { name = "ABC-123-CD2.srt", ext = "srt", url = "https://subtitle.v.geilijiasu.com/two.srt" },
            new { name = "ABC-123-CD3.srt", ext = "srt", url = "https://subtitle.v.geilijiasu.com/nonexistent-part.srt" },
            new { name = "ABC-1234.srt", ext = "srt", url = "https://subtitle.v.geilijiasu.com/wrong.srt" },
            new { name = "ABC-123.smi", ext = "smi", url = "https://subtitle.v.geilijiasu.com/unsupported.smi" }
        } })))));
        using var cat = new SubtitleCatSource(new HttpClient(new Handler((_, _) => timeout
            ? throw new TaskCanceledException("source timeout")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))));
        using var hashes = new HashRecords(); using var search = new SubtitleSearch(thunder, cat, hashes);
        var events = new List<JsonElement>();
        await search.SearchAsync(first, [first, second], "ABC-123 CD1", "zh", false, value => { events.Add(JsonSerializer.SerializeToElement(value, JsonOptions)); return Task.CompletedTask; }, default);
        var results = events.Where(item => item.GetProperty("type").GetString() == "candidates").ToArray();
        Assert.Equal(2, results.Length);
        var routed = results.Single(item => item.GetProperty("targetId").GetString() == second.Id.ToString("N")).GetProperty("candidates")[0];
        var id = routed.GetProperty("id").GetString()!;
        Assert.True(routed.GetProperty("canDownload").GetBoolean());
        Assert.Equal(second.Path, search.Resolve(id, second.Path).MediaPath);
        Assert.Throws<ToolException>(() => search.Resolve(id, first.Path));
        Assert.Contains(results, item => !item.GetProperty("candidates")[0].GetProperty("canDownload").GetBoolean());
        Assert.Contains(events, item => item.GetProperty("type").GetString() == "source" && item.GetProperty("source").GetString() == "subtitlecat" && item.GetProperty("state").GetString() == "error");
        Assert.Equal("done", events[^1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task SubtitleCatDistinguishesTranslateOnlyPagesFromBrokenDownloads()
    {
        using var cat = new SubtitleCatSource(new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith(".srt", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Html("<button onclick=\"translate_from_server_folder('zh-CN','orig.srt','/subs/1/')\">Translate</button>")))));
        var entry = new SourceSubtitle("subtitlecat", "ABC-123", "srt", null, new Uri("https://www.subtitlecat.com/subs/1/ABC-123.html"));
        Assert.Empty(await cat.LanguagesAsync(entry, "zh", default));
        var error = await Assert.ThrowsAsync<ToolException>(() => cat.DownloadAsync(new Uri("https://www.subtitlecat.com/subs/1/missing.srt"), default));
        Assert.Equal(502, error.Status);
        Assert.Contains("404", error.Message);
    }

    [Fact]
    public async Task SubtitleCatReadsActualLanguageLinksAndNeverInvokesTranslate()
    {
        var calls = new List<string>();
        using var cat = new SubtitleCatSource(new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath; calls.Add(path);
            return Task.FromResult(Html(path == "/index.php"
                ? "<table class='sub-table'><tr><td><a href='subs/1/ABC-123.html'>ABC-123</a></td></tr></table>"
                : "<a id='download_zh-CN' href='/subs/2/ABC-123-zh-CN.srt'>Download</a><a id='download_en' href='/subs/3/ABC-123-en.srt'>Download</a><button onclick=\"translate_from_server_folder('zh-TW', 'orig.srt','/subs/1/')\">Translate</button>"));
        })));
        var entry = Assert.Single(await cat.SearchAsync("ABC-123", default));
        var item = Assert.Single(await cat.LanguagesAsync(entry, "zh", default));
        Assert.Equal("zh-CN", item.Language); Assert.Equal("/subs/2/ABC-123-zh-CN.srt", item.Url!.AbsolutePath);
        Assert.Equal(2, (await cat.LanguagesAsync(entry, "all", default)).Length);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task SourceHttpLimitsConcurrencyAndCancellationReleasesItsSlots()
    {
        var active = 0; var maximum = 0;
        using var http = new SourceHttp(new HttpClient(new Handler(async (_, token) =>
        {
            var count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count);
            try { await Task.Delay(20, token); return Html("response"); }
            finally { Interlocked.Decrement(ref active); }
        })), "测试来源", _ => true);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(index => http.GetAsync(new Uri("https://example.com/" + index), true, default)));
        Assert.Equal(2, maximum);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync(new Uri("https://example.com/canceled"), true, cancellation.Token));
        Assert.NotEmpty(await http.GetAsync(new Uri("https://example.com/after"), true, default));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
