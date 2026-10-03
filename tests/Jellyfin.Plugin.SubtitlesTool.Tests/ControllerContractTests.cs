using System.Text.Json;
using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SubtitlesTool.Tests;

[Collection("Jellyfin models")]
public sealed class ControllerContractTests
{
    [Fact]
    public async Task OwnedVideoPartsAreAccessibleAndAllNestedFieldsUseTheUiContract()
    {
        using var fixture = new MediaFixture();
        var root = fixture.Movie("ABC-123-cd1.mp4");
        var part = fixture.Part(root, "ABC-123-cd2.mp4");
        var store = new SubtitleStore(Path.Combine(fixture.Directory, "data"));
        var controller = new SubtitlesController(fixture.Library, fixture.Sources, null!, fixture.Targets(), null!, store, NullLogger<SubtitlesController>.Instance, new ApiAuthorization(), null!)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = Assert.IsType<JsonResult>(await controller.Info(part.Id, null));
        var json = JsonSerializer.SerializeToElement(result.Value, (JsonSerializerOptions)result.SerializerSettings!);
        Assert.Equal(part.Id.ToString("N"), json.GetProperty("selectedTargetId").GetString());
        Assert.True(json.GetProperty("canTrim").GetBoolean());
        var targets = json.GetProperty("targets");
        Assert.Equal(2, targets.GetArrayLength());
        Assert.Equal("ABC-123-cd2.mp4", targets[1].GetProperty("fileName").GetString());
        Assert.Equal(2, targets[1].GetProperty("partNumber").GetInt32());
        Assert.Equal(2, targets[1].GetProperty("partCount").GetInt32());
    }

    private sealed class ApiAuthorization : IAuthorizationContext
    {
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpContext context) => Task.FromResult(new AuthorizationInfo { IsAuthenticated = true, IsApiKey = true });
        public Task<AuthorizationInfo> GetAuthorizationInfo(HttpRequest context) => GetAuthorizationInfo(new DefaultHttpContext());
    }

    [Fact]
    public async Task PermanentTrimCannotStartWithoutAnExplicitConfirmation()
    {
        using var fixture = new MediaFixture();
        var movie = fixture.Movie("ABC-123.mp4");
        var controller = new SubtitlesController(fixture.Library, fixture.Sources, null!, fixture.Targets(), null!, new SubtitleStore(Path.Combine(fixture.Directory, "data")),
            NullLogger<SubtitlesController>.Instance, new ApiAuthorization(), null!)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var response = Assert.IsType<ObjectResult>(await controller.StartTrim(movie.Id, new SubtitlesController.TrimStartRequest(movie.Id, Guid.NewGuid()), default));
        Assert.Equal(400, response.StatusCode);
    }
}
