using System.Text;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.SubtitlesTool;

/// <summary>只为 Web 入口响应加入资源；不写入服务器的 Web 目录。</summary>
public sealed class WebIntegration(IServerConfigurationManager configuration) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var assembly = typeof(Plugin).Assembly;
        var assets = assembly.GetManifestResourceNames().Where(name => name.StartsWith("SubtitlesTool.", StringComparison.Ordinal) && name.EndsWith(".js", StringComparison.Ordinal))
            .ToDictionary(name => name["SubtitlesTool.".Length..], name =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                return Encoding.UTF8.GetBytes(reader.ReadToEnd());
            }, StringComparer.Ordinal);
        if (!assets.ContainsKey("subtitles-tool.js")) throw new InvalidOperationException("缺少前端资源。");
        var version = assembly.GetName().Version!.ToString();
        app.Use(async (context, following) =>
        {
            var baseUrl = configuration.GetNetworkConfiguration().BaseUrl.TrimEnd('/');
            var path = context.Request.Path.Value ?? "";
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) { await following(); return; }
            if (path == baseUrl + "/web") { context.Response.Redirect(baseUrl + "/web/"); return; }
            var assetPrefix = baseUrl + "/SubtitlesTool/";
            var assetName = path.StartsWith(assetPrefix, StringComparison.Ordinal) ? path[assetPrefix.Length..] : "";
            if (assetName == "ui.js") assetName = "subtitles-tool.js";
            if (assets.TryGetValue(assetName, out var asset))
            {
                context.Response.ContentType = "text/javascript; charset=utf-8";
                context.Response.Headers.CacheControl = "public, max-age=3600";
                context.Response.ContentLength = asset.Length;
                if (HttpMethods.IsGet(context.Request.Method)) await context.Response.Body.WriteAsync(asset, context.RequestAborted);
                return;
            }
            if (path != baseUrl + "/web/index.html" && path != baseUrl + "/web/") { await following(); return; }
            var indexPath = Path.Combine(configuration.ApplicationPaths.WebPath, "index.html");
            if (!File.Exists(indexPath)) { await following(); return; }
            var html = await File.ReadAllTextAsync(indexPath, context.RequestAborted);
            var marker = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) { await following(); return; }
            var url = System.Net.WebUtility.HtmlEncode(baseUrl + "/SubtitlesTool/ui.js?v=" + version);
            html = html.Insert(marker, $"<script type=\"module\" src=\"{url}\"></script>");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(html);
            if (HttpMethods.IsGet(context.Request.Method)) await context.Response.WriteAsync(html, context.RequestAborted);
        });
        next(app);
    };
}
