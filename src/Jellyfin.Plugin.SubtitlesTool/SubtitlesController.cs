using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitlesTool;

[ApiController]
[Route("SubtitlesTool/Items/{itemId:guid}")]
[Authorize(Policy = Policies.SubtitleManagement)]
public sealed class SubtitlesController(ILibraryManager library, IMediaSourceManager sources, IFileSystem fileSystem, MediaTargets targets, SubtitleSearch search, SubtitleStore subtitles, ILogger<SubtitlesController> logger, IAuthorizationContext authorization, VideoTrimmer trimmer) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public sealed record SearchRequest(Guid TargetId, string Query, string SubtitleCatLanguage = "zh", bool ComputeHash = false);
    public sealed record DownloadRequest(Guid TargetId, string CandidateId);
    public sealed record CalibrationOpenRequest(Guid TargetId, string SubtitleId, bool Restart = false);
    public sealed record CalibrationSaveRequest(Guid TargetId, string SubtitleId, long OffsetMilliseconds, string CurrentHash, string MediaStamp);
    public sealed record TrimPlanRequest(Guid TargetId, double RequestedMilliseconds);
    public sealed record TrimStartRequest(Guid TargetId, Guid PlanId, bool Confirmed = false);
    public sealed record TrimCancelRequest(Guid TargetId, Guid JobId);

    [HttpGet]
    public async Task<ActionResult> Info(Guid itemId, [FromQuery] string? mediaSourceId)
    {
        try
        {
            var root = await RootAsync(itemId);
            var list = targets.Enumerate(root);
            if (list.Count == 0) throw new ToolException("当前媒体不在已配置的 JAV 路径内，或视频文件已不存在。", 404);
            var selected = list.FirstOrDefault(target => target.Id == itemId && itemId != root.Id)
                ?? list.FirstOrDefault(target => target.VersionId.ToString("N") == mediaSourceId) ?? list[0];
            return Data(new
            {
                rootId = root.Id.ToString("N"), selectedTargetId = selected.Id.ToString("N"),
                canTrim = (await authorization.GetAuthorizationInfo(HttpContext)) is var auth && (auth.IsApiKey || auth.User?.HasPermission(PermissionKind.IsAdministrator) == true),
                targets = list.Select(target => new
                {
                    id = target.Id.ToString("N"), versionId = target.VersionId.ToString("N"), target.VersionName, target.FileName,
                    target.PartNumber, target.PartCount, target.RunTimeTicks, query = target.DefaultQuery,
                    hasRecord = System.IO.File.Exists(HashRecords.RecordPath(target.Path)), subtitles = subtitles.List(target)
                })
            });
        }
        catch (Exception ex) when (IsUserError(ex)) { return ErrorResult(ex); }
    }

    [HttpPost("search")]
    public async Task Search(Guid itemId, [FromBody] SearchRequest body, CancellationToken cancellationToken)
    {
        using var writes = new SemaphoreSlim(1, 1);
        async Task Emit(object value)
        {
            await writes.WaitAsync(cancellationToken);
            try
            {
                await Response.WriteAsync(JsonSerializer.Serialize(value, JsonOptions) + "\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
            finally { writes.Release(); }
        }
        try
        {
            var list = targets.Enumerate(await RootAsync(itemId));
            var target = Select(list, body.TargetId);
            Response.ContentType = "application/x-ndjson; charset=utf-8";
            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Accel-Buffering"] = "no";
            await search.SearchAsync(target, list, body.Query ?? "", body.SubtitleCatLanguage ?? "zh", body.ComputeHash, Emit, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (IsUserError(ex))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                if (!Response.HasStarted) Response.ContentType = "application/x-ndjson; charset=utf-8";
                await Emit(new { type = "error", targetId = body.TargetId.ToString("N"), message = Describe(ex) });
            }
        }
    }

    [HttpPost("download")]
    public async Task<ActionResult> Download(Guid itemId, [FromBody] DownloadRequest body, CancellationToken cancellationToken)
    {
        try
        {
            var target = Select(targets.Enumerate(await RootAsync(itemId)), body.TargetId);
            var candidate = search.Resolve(body.CandidateId, target.Path);
            var bytes = await search.DownloadAsync(candidate, cancellationToken);
            var path = await subtitles.SaveDownloadAsync(target, candidate.Subtitle, bytes, cancellationToken);
            return await Refresh(target, path, "当前字幕已更新，可以播放核对并校准。");
        }
        catch (Exception ex) when (IsUserError(ex)) { return ErrorResult(ex); }
    }

    [HttpPost("calibration/open")]
    public async Task<ActionResult> OpenCalibration(Guid itemId, [FromBody] CalibrationOpenRequest body, CancellationToken cancellationToken)
    {
        try
        {
            var target = Select(targets.Enumerate(await RootAsync(itemId)), body.TargetId);
            return Data(await subtitles.OpenAsync(target, body.SubtitleId, body.Restart, cancellationToken));
        }
        catch (Exception ex) when (IsUserError(ex)) { return ErrorResult(ex); }
    }

    [HttpPost("calibration/save")]
    public async Task<ActionResult> SaveCalibration(Guid itemId, [FromBody] CalibrationSaveRequest body, CancellationToken cancellationToken)
    {
        try
        {
            var target = Select(targets.Enumerate(await RootAsync(itemId)), body.TargetId);
            var path = await subtitles.CalibrateAsync(target, body.SubtitleId, body.OffsetMilliseconds, body.CurrentHash, body.MediaStamp, cancellationToken);
            return await Refresh(target, path, "校准已保存。请重新加载字幕，并将播放器的临时字幕偏移归零。");
        }
        catch (Exception ex) when (IsUserError(ex)) { return ErrorResult(ex); }
    }

    [HttpPost("trim/plan")]
    public async Task<ActionResult> PlanTrim(Guid itemId, [FromBody] TrimPlanRequest body, CancellationToken cancellationToken)
    {
        try
        {
            await RequireAdministrator();
            var target = Select(targets.Enumerate(await RootAsync(itemId)), body.TargetId);
            return Data(await trimmer.PlanAsync(target, body.RequestedMilliseconds, cancellationToken));
        }
        catch (Exception ex) when (IsUserError(ex)) { return ErrorResult(ex); }
    }

    [HttpPost("trim/start")]
    public async Task<ActionResult> StartTrim(Guid itemId, [FromBody] TrimStartRequest body, CancellationToken cancellationToken)
    {
        try
        {
            await RequireAdministrator();
            var target = Select(targets.Enumerate(await RootAsync(itemId)), body.TargetId);
            if (!body.Confirmed) throw new ToolException("请确认永久裁切当前视频。", 400);
            return Data(await trimmer.StartAsync(target, body.PlanId, cancellationToken));
        }
        catch (Exception ex) when (IsUserError(ex)) { return ErrorResult(ex); }
    }

    [HttpGet("trim")]
    public async Task<ActionResult> TrimProgress(Guid itemId, [FromQuery] Guid targetId)
    {
        try
        {
            await RequireAdministrator();
            var target = Select(targets.Enumerate(await RootAsync(itemId)), targetId);
            return Data(new { job = trimmer.Status(target.Path) });
        }
        catch (Exception ex) when (IsUserError(ex)) { return ErrorResult(ex); }
    }

    [HttpPost("trim/cancel")]
    public async Task<ActionResult> CancelTrim(Guid itemId, [FromBody] TrimCancelRequest body)
    {
        try
        {
            await RequireAdministrator();
            var target = Select(targets.Enumerate(await RootAsync(itemId)), body.TargetId);
            return Data(trimmer.Cancel(target.Path, body.JobId));
        }
        catch (Exception ex) when (IsUserError(ex)) { return ErrorResult(ex); }
    }

    private async Task RequireAdministrator()
    {
        var auth = await authorization.GetAuthorizationInfo(HttpContext);
        if (!auth.IsApiKey && auth.User?.HasPermission(PermissionKind.IsAdministrator) != true)
            throw new ToolException("永久裁切需要 Jellyfin 管理员权限。", 403);
    }

    private async Task<ActionResult> Refresh(MediaTarget target, string subtitlePath, string message)
    {
        try
        {
            await target.Item.RefreshMetadata(new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly, ImageRefreshMode = MetadataRefreshMode.None,
                ForceSave = true, RegenerateTrickplay = false
            }, CancellationToken.None);
            var visible = sources.GetStaticMediaSources(target.Item, false).SelectMany(source => source.MediaStreams)
                .Any(stream => stream.Type == MediaStreamType.Subtitle && stream.IsExternal && MediaTargets.PathComparer.Equals(stream.Path, subtitlePath));
            if (!visible) return Data(new { fileName = Path.GetFileName(subtitlePath), refreshed = false, message = "字幕已保存，Jellyfin 尚未更新字幕列表，请稍后刷新媒体信息。" });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "字幕已保存，但媒体 {ItemId} 的刷新失败", target.Id);
            return Data(new { fileName = Path.GetFileName(subtitlePath), refreshed = false, message = "字幕已保存，但 Jellyfin 刷新失败，请刷新媒体信息。" });
        }
        return Data(new { fileName = Path.GetFileName(subtitlePath), refreshed = true, message });
    }

    private async Task<Video> RootAsync(Guid itemId)
    {
        var auth = await authorization.GetAuthorizationInfo(HttpContext);
        if (!auth.IsAuthenticated || (!auth.IsApiKey && auth.User is null)) throw new ToolException("登录状态无效，请重新登录。", 401);
        var item = library.GetItemById<Video>(itemId, auth.User);
        var visited = new HashSet<Guid>();
        while (item is not null && item is not Movie && item is not Episode && item.OwnerId != Guid.Empty && visited.Add(item.Id))
        {
            var owner = library.GetItemById<Video>(item.OwnerId, auth.User);
            if (owner is null || !owner.GetAdditionalPartIds().Contains(item.Id)) break;
            item = owner;
        }
        if (item is not Movie && item is not Episode) throw new ToolException("此条目不是可处理的本地影片或所属分段。", 404);
        return item;
    }

    private static MediaTarget Select(IReadOnlyList<MediaTarget> list, Guid id) => list.FirstOrDefault(target => target.Id == id)
        ?? throw new ToolException("所选视频版本或分段已不存在，或不在已配置的 JAV 路径内。", 404);
    private static JsonResult Data(object value) => new(value, JsonOptions);
    private static bool IsUserError(Exception ex) => ex is ToolException or IOException or UnauthorizedAccessException;
    private static string Describe(Exception ex) => ex switch
    {
        ToolException => ex.Message,
        UnauthorizedAccessException => "无法读取视频或写入字幕，请检查 Jellyfin 的目录权限。",
        _ => "文件操作失败，请检查文件是否存在、磁盘空间和目录权限后重试。"
    };
    private ObjectResult ErrorResult(Exception ex) => StatusCode(ex is ToolException error ? error.Status : 500, new { message = Describe(ex) });
}
