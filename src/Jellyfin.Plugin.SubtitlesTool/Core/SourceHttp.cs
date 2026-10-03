using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

/// <summary>每个来源各自限流、缓存和计时，下载也遵守同一并发上限。</summary>
public sealed class SourceHttp(HttpClient client, string name, Func<Uri, bool> allowed) : IDisposable
{
    private readonly SemaphoreSlim _requests = new(2, 2);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });

    public async Task<byte[]> GetAsync(Uri uri, bool cache, CancellationToken cancellationToken)
    {
        if (!allowed(uri)) throw new ToolException($"{name}返回了不受支持的下载地址。", 502);
        var key = uri.AbsoluteUri;
        if (cache && _cache.TryGetValue<byte[]>(key, out var existing)) return existing!;
        await _requests.WaitAsync(cancellationToken);
        try
        {
            if (cache && _cache.TryGetValue<byte[]>(key, out existing)) return existing!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(cache ? 15 : 45));
            for (var redirect = 0; redirect <= 3; redirect++)
            {
                if (!allowed(uri)) throw new ToolException($"{name}返回了不受支持的跳转地址。", 502);
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new ToolException($"{name}请求失败（{(int)response.StatusCode}），请稍后重试。", 502);
                var maximum = cache ? 2 * 1024 * 1024 : 20 * 1024 * 1024;
                if (response.Content.Headers.ContentLength > maximum) throw new ToolException($"{name}返回的文件异常大。", 502);
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var output = new MemoryStream();
                var buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (output.Length + count > maximum) throw new ToolException($"{name}返回的文件异常大。", 502);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
                var bytes = output.ToArray();
                if (bytes.Length == 0) throw new ToolException($"{name}返回了空文件。", 502);
                if (cache) _cache.Set(key, bytes, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15), Size = bytes.Length });
                return bytes;
            }
            throw new ToolException($"{name}跳转次数过多，请稍后重试。", 502);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ToolException($"连接{name}超时，请重试。", 504); }
        catch (HttpRequestException) { throw new ToolException($"无法连接{name}，请检查服务器网络后重试。", 502); }
        finally { _requests.Release(); }
    }

    public void Invalidate(Uri uri) => _cache.Remove(uri.AbsoluteUri);
    public void Dispose() { _cache.Dispose(); _requests.Dispose(); client.Dispose(); }
}
