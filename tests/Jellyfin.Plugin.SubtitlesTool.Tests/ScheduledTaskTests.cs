using System.Reflection;
using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SubtitlesTool.Tests;

[CollectionDefinition("Jellyfin models", DisableParallelization = true)]
public sealed class JellyfinModelCollection;

[Collection("Jellyfin models")]
public sealed class ScheduledTaskTests
{
    [Fact]
    public async Task TaskGeneratesMissingRecordsReportsInvalidOnesAndKeepsTriggersEmpty()
    {
        using var fixture = new MediaFixture();
        var fresh = fixture.Movie("缺失.mp4");
        var existing = fixture.Movie("已存在.mp4");
        var broken = fixture.Movie("损坏.mp4");
        fixture.Add(new Movie { Id = Guid.NewGuid(), Path = fresh.Path });
        fixture.Add(new Movie { Id = Guid.NewGuid(), Path = "https://example.com/movie.mp4" });
        fixture.Movie("远程.strm");
        var pair = new HashPair(new string('A', 40), new string('B', 40));
        await File.WriteAllTextAsync(HashRecords.RecordPath(existing.Path), pair.Serialize());
        await File.WriteAllTextAsync(HashRecords.RecordPath(broken.Path), "需要修复的记录");
        using var hashes = new HashRecords();
        var task = new GenerateHashRecordsTask(fixture.Library, MediaFixture.Sessions([]), hashes, fixture.Targets(), NullLogger<GenerateHashRecordsTask>.Instance);
        Assert.Empty(task.GetDefaultTriggers());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new InlineProgress(_ => { }), default));
        Assert.Contains("已生成 1 个，复用 1 个，跳过 3 个，失败 1 个", error.Message);
        Assert.NotNull(await HashRecords.ReadAsync(fresh.Path, default));
        Assert.Equal(pair, await HashRecords.ReadAsync(existing.Path, default));
        Assert.Equal("需要修复的记录", await File.ReadAllTextAsync(HashRecords.RecordPath(broken.Path)));
    }

    [Fact]
    public async Task PartsAndVersionsAreEnumeratedSeparatelyAndEveryPartGetsARecord()
    {
        using var fixture = new MediaFixture();
        var main = fixture.Movie("ASFB-161-cd1.mp4");
        var second = fixture.Part(main, "ASFB-161-cd2.mp4");
        var third = fixture.Part(main, "ASFB-161-cd3.mp4");
        var alternate = fixture.Movie("ASFB-161 - 480p.mp4");
        fixture.Versions[main.Id] = [main, alternate];
        var targets = fixture.Targets();
        var files = targets.Enumerate(main);
        Assert.Equal(4, files.Count);
        Assert.Equal(3, files.Single(item => item.Id == second.Id).PartCount);
        Assert.Equal(2, files.Single(item => item.Id == second.Id).PartNumber);
        Assert.Equal(1, files.Single(item => item.Id == alternate.Id).PartCount);
        using var hashes = new HashRecords();
        var task = new GenerateHashRecordsTask(fixture.Library, MediaFixture.Sessions([]), hashes, targets, NullLogger<GenerateHashRecordsTask>.Instance);
        await task.ExecuteAsync(new InlineProgress(_ => { }), default);
        foreach (var item in new Video[] { main, second, third, alternate }) Assert.NotNull(await HashRecords.ReadAsync(item.Path, default));
        var numbered = fixture.Movie("ABP-361 - 1080p-1.mp4");
        var numbered2 = fixture.Movie("ABP-361 - 1080p-2.mp4");
        fixture.Versions[numbered.Id] = [numbered, numbered2];
        Assert.All(targets.Enumerate(numbered), target => Assert.Equal(1, target.PartCount));
    }

    [Fact]
    public async Task ScopeAndMissingPathsAreSkipped()
    {
        using var fixture = new MediaFixture();
        var inside = fixture.Movie("jav/ABC-123.mp4");
        var outside = fixture.Movie("other/ABC-124.mp4");
        var absent = fixture.Movie("jav/ABC-125.mp4"); File.Delete(absent.Path);
        using var hashes = new HashRecords();
        var targets = new MediaTargets(fixture.Library, fixture.Sources, () => [Path.Combine(fixture.Directory, "jav")]);
        var task = new GenerateHashRecordsTask(fixture.Library, MediaFixture.Sessions([]), hashes, targets, NullLogger<GenerateHashRecordsTask>.Instance);
        await task.ExecuteAsync(new InlineProgress(_ => { }), default);
        Assert.NotNull(await HashRecords.ReadAsync(inside.Path, default));
        Assert.False(File.Exists(HashRecords.RecordPath(outside.Path)));
        Assert.False(File.Exists(HashRecords.RecordPath(absent.Path)));
    }

    [Fact]
    public async Task ActivePlaybackWaitsAndPausedPlaybackAllowsGeneration()
    {
        using var fixture = new MediaFixture();
        var video = fixture.Movie("播放状态.mp4");
        var session = new SessionInfo(null!, NullLogger.Instance) { NowPlayingItem = new BaseItemDto() };
        using var hashes = new HashRecords();
        var task = new GenerateHashRecordsTask(fixture.Library, MediaFixture.Sessions([session]), hashes, fixture.Targets(), NullLogger<GenerateHashRecordsTask>.Instance);
        using var cancellation = new CancellationTokenSource();
        var running = task.ExecuteAsync(new InlineProgress(_ => { }), cancellation.Token);
        Assert.False(running.IsCompleted); Assert.False(File.Exists(HashRecords.RecordPath(video.Path)));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        session.PlayState.IsPaused = true;
        double final = 0; await task.ExecuteAsync(new InlineProgress(value => final = value), default);
        Assert.Equal(100, final); Assert.NotNull(await HashRecords.ReadAsync(video.Path, default));
        await session.DisposeAsync();
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
}

internal sealed class MediaFixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "jav-subtitle-test-" + Guid.NewGuid().ToString("N"));
    private readonly ILibraryManager _originalLibrary = BaseItem.LibraryManager;
    private readonly IMediaSourceManager _originalSources = BaseItem.MediaSourceManager;
    public Dictionary<Guid, Video> Items { get; } = [];
    public Dictionary<Guid, Video[]> Versions { get; } = [];
    public ILibraryManager Library { get; }
    public IMediaSourceManager Sources { get; }

    public MediaFixture()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Library = Stub<ILibraryManager>((method, args) => method.Name switch
        {
            nameof(ILibraryManager.GetItemIds) => Items.Values.OfType<Movie>().Select(item => item.Id).ToArray(),
            nameof(ILibraryManager.GetItemList) => Items.Values.Where(item => ((InternalItemsQuery)args[0]!).ItemIds.Contains(item.Id)).Cast<BaseItem>().ToArray(),
            nameof(ILibraryManager.GetItemById) => Items.GetValueOrDefault((Guid)args[0]!),
            nameof(ILibraryManager.GetNewItemId) => Items.Values.First(item => item.Path == (string)args[0]!).Id,
            _ => throw new NotSupportedException(method.Name)
        });
        Sources = Stub<IMediaSourceManager>((method, args) => method.Name switch
        {
            nameof(IMediaSourceManager.GetPathProtocol) => ((string)args[0]!).StartsWith("https://", StringComparison.Ordinal) ? MediaProtocol.Http : MediaProtocol.File,
            nameof(IMediaSourceManager.GetStaticMediaSources) => (Versions.GetValueOrDefault(((Video)args[0]!).Id) ?? [(Video)args[0]!]).Select(video => new MediaSourceInfo
            {
                Id = video.Id.ToString("N"), Path = video.Path, Name = video.Name, RunTimeTicks = 600000000,
                Protocol = video.Path.StartsWith("https://", StringComparison.Ordinal) ? MediaProtocol.Http : MediaProtocol.File, MediaStreams = []
            }).ToList(),
            _ => throw new NotSupportedException(method.Name)
        });
        BaseItem.LibraryManager = Library; BaseItem.MediaSourceManager = Sources;
    }
    public Movie Movie(string name) { var video = new Movie(); Initialize(video, name); Add(video); return video; }
    public Video Part(Video owner, string name)
    {
        var video = new Video { OwnerId = owner.Id }; Initialize(video, name); Add(video);
        owner.AdditionalParts = [.. owner.AdditionalParts, video.Path]; return video;
    }
    private void Initialize(Video video, string name)
    {
        video.Id = Guid.NewGuid(); video.Name = name; video.ForcedSortName = name; video.Path = Path.Combine(Directory, name); video.VideoType = VideoType.VideoFile;
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(video.Path)!); File.WriteAllBytes(video.Path, new byte[600000]);
    }
    public void Add(Video video) => Items.Add(video.Id, video);
    public MediaTargets Targets() => new(Library, Sources, () => [Directory]);
    public static ISessionManager Sessions(IReadOnlyList<SessionInfo> sessions) => Stub<ISessionManager>((method, _) => method.Name == "get_Sessions" ? sessions : throw new NotSupportedException(method.Name));
    private static T Stub<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        var result = DispatchProxy.Create<T, InterfaceStub>(); ((InterfaceStub)(object)result).InvokeMethod = invoke; return result;
    }
    public class InterfaceStub : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> InvokeMethod { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeMethod(targetMethod!, args!);
    }
    public void Dispose()
    {
        BaseItem.LibraryManager = _originalLibrary; BaseItem.MediaSourceManager = _originalSources; System.IO.Directory.Delete(Directory, true);
    }
}
