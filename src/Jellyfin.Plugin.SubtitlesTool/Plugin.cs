using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitlesTool;

public sealed class Configuration : BasePluginConfiguration
{
    public string[] MediaRoots { get; set; } = [];
}

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override string Name => "JAV Subtitles Tool";
    public override Guid Id => Guid.Parse("c4b75732-8527-4f58-9cdf-18efca21a9e5");
    public override string Description => "按番号和视频分段聚合迅雷、SubtitleCat 字幕，支持手动校准并保存。";
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "jav-subtitles-tool", EmbeddedResourcePath = "SubtitlesTool.Configuration" }];
}

public sealed class Registrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<HashRecords>();
        services.AddSingleton(_ => new ThunderSource(CreateClient()));
        services.AddSingleton(_ => new SubtitleCatSource(CreateClient()));
        services.AddSingleton<SubtitleSearch>();
        services.AddSingleton(provider => new MediaTargets(provider.GetRequiredService<ILibraryManager>(), provider.GetRequiredService<IMediaSourceManager>(), () => Plugin.Instance?.Configuration.MediaRoots ?? []));
        services.AddSingleton(provider => new SubtitleStore(Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, "jav-subtitles-tool")));
        services.AddTransient<IStartupFilter, WebIntegration>();
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JAV-Jellyfin-Subtitles-Tool/" + typeof(Plugin).Assembly.GetName().Version);
        return http;
    }
}
