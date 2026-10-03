using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitlesTool;

// 类型名和 Key 保持稳定，Jellyfin 使用类型全名识别已保存的触发时间。
public sealed class GenerateHashRecordsTask(ILibraryManager library, ISessionManager sessions, HashRecords hashes, MediaTargets targets, ILogger<GenerateHashRecordsTask> logger) : IScheduledTask
{
    public string Name => "生成 CID、GCID 记录";
    public string Key => "SubtitlesToolGenerateHashRecords";
    public string Category => "JAV Subtitles Tool";
    public string Description => "为空闲时可访问的已配置 JAV 视频、各媒体版本及附属分段生成指纹记录。播放期间暂停，手动计算优先。";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
    private bool IsIdle() => !sessions.Sessions.Any(session => session.IsActive && session.NowPlayingItem is not null && !session.PlayState.IsPaused);

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(0);
        var ids = library.GetItemIds(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode], IsVirtualItem = false, IsFolder = false,
            GroupByPresentationUniqueKey = false, SourceTypes = [SourceType.Library], Recursive = true
        });
        var seen = new HashSet<string>(MediaTargets.PathComparer);
        var generated = 0; var reused = 0; var skipped = 0; var failed = 0;
        try
        {
            const int batchSize = 256;
            for (var offset = 0; offset < ids.Count; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchIds = ids.Skip(offset).Take(batchSize).ToArray();
                var items = library.GetItemList(new InternalItemsQuery
                {
                    ItemIds = batchIds, GroupByPresentationUniqueKey = false,
                    DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false }
                });
                skipped += batchIds.Length - items.Count;
                var completed = offset;
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var media = item is Video video ? targets.Enumerate(video) : [];
                        if (media.Count == 0) skipped++;
                        for (var index = 0; index < media.Count; index++)
                        {
                            var target = media[index];
                            if (!seen.Add(target.Path)) { skipped++; continue; }
                            try
                            {
                                var result = await hashes.GetOrCreateWhenIdleAsync(target.Path, update =>
                                {
                                    var fraction = update.Total > 0 ? Math.Clamp((double)update.Read / update.Total, 0, 0.999) : 0;
                                    progress.Report((completed + (index + fraction) / media.Count) * 100 / ids.Count);
                                    return Task.CompletedTask;
                                }, IsIdle, cancellationToken);
                                if (result.Created) generated++; else reused++;
                            }
                            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                            { skipped++; logger.LogInformation("跳过已失效媒体路径：{Path}", target.Path); }
                            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
                            { failed++; logger.LogWarning(ex, "生成 CID、GCID 记录失败：{Path}", target.Path); }
                        }
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { skipped++; }
                    catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
                    { failed++; logger.LogWarning(ex, "无法枚举媒体及其分段：{ItemId}", item.Id); }
                    completed++;
                    progress.Report(completed * 100.0 / ids.Count);
                }
                progress.Report((offset + batchIds.Length) * 100.0 / ids.Count);
            }
            if (failed > 0) throw new InvalidOperationException($"记录检查完成：已生成 {generated} 个，复用 {reused} 个，跳过 {skipped} 个，失败 {failed} 个。请在 Jellyfin 日志中查看失败文件。");
            progress.Report(100);
        }
        finally
        {
            logger.LogInformation("CID、GCID 任务结束：已生成 {Generated} 个，复用 {Reused} 个，跳过 {Skipped} 个，失败 {Failed} 个", generated, reused, skipped, failed);
        }
    }
}
