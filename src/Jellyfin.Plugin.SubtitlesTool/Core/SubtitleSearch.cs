using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed class SubtitleSearch(ThunderSource thunder, SubtitleCatSource subtitleCat, HashRecords hashes) : IDisposable
{
    private readonly MemoryCache _candidates = new(new MemoryCacheOptions { SizeLimit = 4096 });

    public async Task SearchAsync(MediaTarget target, IReadOnlyList<MediaTarget> targets, string text, string language, bool computeHash, Func<object, Task> emit, CancellationToken cancellationToken)
    {
        text = text.Trim();
        if (text.Length > 160) throw new ToolException("搜索词过长，请输入番号和分段标记。");
        if (language.Length > 24) throw new ToolException("字幕语言无效。");
        var query = new SearchQuery(text, JavIdentity.Extract(text), target.PartNumber, target.PartCount);
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        async Task<bool> Add(SourceSubtitle item, bool byFile)
        {
            var match = JavIdentity.Match(item, query, byFile);
            if (match is null) return false;
            var destination = target;
            var part = JavIdentity.ExtractPart(item.Name, target.PartCount > 1);
            if (part is not null && part != target.PartNumber && JavIdentity.Extract(item.Name) == query.Code)
            {
                var matchingPart = targets.FirstOrDefault(value => value.VersionId == target.VersionId && value.PartNumber == part);
                if (matchingPart is null) return false;
                destination = matchingPart;
            }
            var key = destination.Id + "|" + item.Source + "|" + (item.Url?.AbsoluteUri ?? item.Name + item.Format) + "|" + item.Language;
            if (!seen.TryAdd(key, 0)) return false;
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            var reason = !SubtitleFormats.Supports(item.Format) ? $"暂不支持 {item.Format.ToUpperInvariant()} 格式，当前支持 SRT、ASS、SSA、VTT。" : item.Url is null ? "来源没有提供可用的下载地址。" : null;
            _candidates.Set(id, new CandidateDownload(destination.Path, item), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15), Size = 1 });
            var candidate = new Candidate(id, item.Source, item.Name, item.Format, item.Language, match, part, reason is null, reason);
            await emit(new { type = "candidates", targetId = destination.Id.ToString("N"), source = item.Source, candidates = new[] { candidate } });
            return true;
        }

        async Task Run(string source, Func<Func<SourceSubtitle, bool, Task>, List<string>, Task> operation)
        {
            var count = 0;
            var errors = new List<string>();
            await emit(new { type = "source", targetId = target.Id.ToString("N"), source, state = "searching" });
            try
            {
                await operation(async (item, byFile) => { if (await Add(item, byFile)) Interlocked.Increment(ref count); }, errors);
            }
            catch (ToolException ex) { errors.Add(ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add("读取文件或字幕源响应失败，请检查服务器网络和文件权限。"); }
            var message = errors.Count > 0 ? string.Join(" ", errors.Distinct()) : count == 0 ? "没有找到匹配的字幕。" : $"找到 {count} 条候选，请核对内容。";
            await emit(new { type = "source", targetId = target.Id.ToString("N"), source, state = errors.Count == 0 ? "complete" : count > 0 ? "partial" : "error", count, message });
        }

        async Task QuerySafely(Func<Task> operation, List<string> errors)
        {
            try { await operation(); }
            catch (ToolException ex) { errors.Add(ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add("读取文件或字幕源响应失败，请检查服务器网络和文件权限。"); }
        }

        await Task.WhenAll(
            Run("xunlei", async (add, errors) =>
            {
                foreach (var term in query.Terms)
                    await QuerySafely(async () => { foreach (var item in await thunder.SearchAsync(term, false, cancellationToken)) await add(item, false); }, errors);
                await QuerySafely(async () =>
                {
                    var pair = computeHash
                        ? await hashes.GetOrCreateAsync(target.Path, update => emit(new { type = "progress", targetId = target.Id.ToString("N"), update.Phase, update.Read, update.Total }), cancellationToken)
                        : await HashRecords.ReadAsync(target.Path, cancellationToken);
                    if (pair is not null)
                        foreach (var item in await thunder.SearchAsync(pair.Gcid, true, cancellationToken)) await add(item, true);
                    else if (query.Terms.Length == 0) errors.Add("请输入番号，或点击“按视频文件进一步搜索”。");
                }, errors);
            }),
            Run("subtitlecat", async (add, errors) =>
            {
                var pages = new HashSet<string>(StringComparer.Ordinal);
                foreach (var term in query.Terms)
                {
                    await QuerySafely(async () =>
                    {
                        var entries = (await subtitleCat.SearchAsync(term, cancellationToken))
                            .Where(item => JavIdentity.Match(item, query, false) is not null && pages.Add(item.Url!.AbsoluteUri)).ToArray();
                        // 详情按两条一批读取，源站并发总量也由 SourceHttp 统一限制。
                        foreach (var batch in entries.Chunk(2))
                        {
                            var results = await Task.WhenAll(batch.Select(async entry =>
                            {
                                try { return (Items: await subtitleCat.LanguagesAsync(entry, language, cancellationToken), Error: (string?)null); }
                                catch (ToolException ex) { return (Items: Array.Empty<SourceSubtitle>(), Error: ex.Message); }
                            }));
                            foreach (var result in results)
                            {
                                if (result.Error is not null) errors.Add(result.Error);
                                foreach (var item in result.Items) await add(item, false);
                            }
                        }
                    }, errors);
                }
                if (query.Terms.Length == 0) errors.Add("请输入番号后查询 SubtitleCat。");
            }));
        await emit(new { type = "done", targetId = target.Id.ToString("N") });
    }

    public CandidateDownload Resolve(string id, string path)
    {
        if (!_candidates.TryGetValue<CandidateDownload>(id, out var candidate) || candidate is null || !MediaTargets.PathComparer.Equals(candidate.MediaPath, path))
            throw new ToolException("字幕候选已过期或不属于当前视频，请重新搜索。", 410);
        if (!SubtitleFormats.Supports(candidate.Subtitle.Format) || candidate.Subtitle.Url is null) throw new ToolException("这条候选暂时无法下载。");
        return candidate;
    }

    public Task<byte[]> DownloadAsync(CandidateDownload candidate, CancellationToken cancellationToken) => candidate.Subtitle.Source == "xunlei"
        ? thunder.DownloadAsync(candidate.Subtitle.Url!, cancellationToken)
        : subtitleCat.DownloadAsync(candidate.Subtitle.Url!, cancellationToken);
    public void Dispose() => _candidates.Dispose();
}
