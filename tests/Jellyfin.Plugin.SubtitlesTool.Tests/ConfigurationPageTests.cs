using System.Net.Mime;
using System.Runtime.CompilerServices;
using MediaBrowser.Model.Net;

namespace Jellyfin.Plugin.SubtitlesTool.Tests;

public sealed class ConfigurationPageTests
{
    [Fact]
    public void RegisteredPageIsEmbeddedAndServedAsHtml()
    {
        // 页面登记不依赖配置或服务，避免构造插件改变全局 Instance。
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        var page = Assert.Single(plugin.GetPages());
        Assert.Equal("text/html", new ContentType(MimeTypes.GetMimeType(page.EmbeddedResourcePath)).MediaType);
        using var resource = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(resource);
    }
}
