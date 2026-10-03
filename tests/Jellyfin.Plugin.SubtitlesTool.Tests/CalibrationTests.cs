using System.Text;
using Jellyfin.Plugin.SubtitlesTool.Core;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SubtitlesTool.Tests;

public sealed class CalibrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jav-calibration-test-" + Guid.NewGuid().ToString("N"));
    public CalibrationTests() => Directory.CreateDirectory(_directory);
    private static string Sample(string format) => format switch
    {
        "srt" => "1\r\n00:00:00,500 --> 00:00:01,000\r\n开头\r\n\r\n2\r\n00:00:10,000 --> 00:00:12,000\r\n<b>你好</b>\r\n",
        "vtt" => "WEBVTT\n\nSTYLE\n::cue { color: yellow; }\n\none\n00:00:00.500 --> 00:00:01.000 align:start\n开头\n\ntwo\n00:00:10.000 --> 00:00:12.000\n你好\n",
        _ => "[Script Info]\r\nTitle: 保留标题\r\n[V4+ Styles]\r\nStyle: Default,Arial,20\r\n[Events]\r\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\nDialogue: 0,0:00:00.50,0:00:01.00,Default,,0,0,0,,{\\b1}开头\r\nDialogue: 0,0:00:10.00,0:00:12.00,Default,,0,0,0,,你好,保留逗号\r\n"
    };
    private MediaTarget Target(string file)
    {
        var path = Path.Combine(_directory, file); File.WriteAllText(path, "video remains unchanged");
        var id = Guid.NewGuid(); return new MediaTarget(id, id, file, new Video { Id = id, Path = path }, path, 1, 1, 60_000_000);
    }

    [Theory]
    [InlineData("srt")][InlineData("ass")][InlineData("ssa")][InlineData("vtt")]
    public void ShiftsTimingsClipsBeforeZeroAndPreservesStyles(string format)
    {
        var bytes = Encoding.UTF8.GetBytes(Sample(format));
        var original = new SubtitleDocument(bytes, format);
        var movedBytes = original.Shift(30_000); var moved = new SubtitleDocument(movedBytes, format);
        Assert.Equal(30_500, moved.Cues[0].StartMilliseconds); Assert.Equal(42_000, moved.Cues[1].EndMilliseconds);
        Assert.Equal(original.Cues[1].Text, moved.Cues[1].Text);
        var clipped = new SubtitleDocument(original.Shift(-750), format);
        Assert.Equal(0, clipped.Cues[0].StartMilliseconds); Assert.Equal(250, clipped.Cues[0].EndMilliseconds);
        Assert.Single(new SubtitleDocument(original.Shift(-1000), format).Cues);
        Assert.Equal(bytes, original.Shift(0));
        var text = Encoding.UTF8.GetString(movedBytes);
        if (format is "ass" or "ssa")
        {
            Assert.Contains("Style: Default,Arial,20", text); Assert.Contains("{\\b1}开头", text);
            Assert.Single(new SubtitleDocument(original.Shift(-995), format).Cues);
        }
        if (format == "vtt") { Assert.Contains("::cue { color: yellow; }", text); Assert.Contains("align:start", text); }
    }

    [Theory]
    [InlineData("utf-16", true)][InlineData("utf-16", false)][InlineData("utf-16BE", false)][InlineData("gb18030", false)][InlineData("big5", false)]
    public void KeepsOriginalEncodingAndByteOrderMark(string name, bool bom)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(name);
        var text = "1\r\n00:00:10,000 --> 00:00:12,000\r\n" + string.Concat(Enumerable.Repeat("字幕時間校準測試，請保留原始文字與編碼。", 30)) + "\r\n";
        var preamble = bom ? encoding.GetPreamble() : [];
        var original = preamble.Concat(encoding.GetBytes(text)).ToArray();
        var expected = preamble.Concat(encoding.GetBytes(text.Replace("00:00:10,000", "00:00:40,000").Replace("00:00:12,000", "00:00:42,000"))).ToArray();
        Assert.Equal(expected, new SubtitleDocument(original, "srt").Shift(30_000));
    }

    [Fact]
    public async Task AdjustsCurrentSubtitleAfterRestartWithoutRetainingOriginalAndStaysBoundToTheFile()
    {
        var first = Target("ABC-123-cd1.mp4"); var second = Target("ABC-123-cd2.mp4");
        var bytes = Encoding.UTF8.GetBytes(Sample("srt"));
        var candidate = new SourceSubtitle("xunlei", "ABC-123.srt", "srt", null, new Uri("https://subtitle.v.geilijiasu.com/a.srt"));
        var data = Path.Combine(_directory, "plugin-data");
        string path;

        {
            var store = new SubtitleStore(data);
            path = await store.SaveDownloadAsync(first, candidate, bytes, false, default);
            await store.SaveDownloadAsync(second, candidate, bytes, false, default);
            var info = await store.OpenAsync(first, Path.GetFileName(path), false, default);
            await store.CalibrateAsync(first, info.FileName, 30_000, info.CurrentHash, info.MediaStamp, default);
            await Assert.ThrowsAsync<ToolException>(() => store.CalibrateAsync(first, info.FileName, 32_000, info.CurrentHash, info.MediaStamp, default));
        }

        {
            var store = new SubtitleStore(data);
            var info = await store.OpenAsync(first, Path.GetFileName(path), false, default);
            Assert.Equal(40_000, info.Cues[1].StartMilliseconds);
            await store.CalibrateAsync(first, info.FileName, 2_000, info.CurrentHash, info.MediaStamp, default);
            Assert.Equal(42_000, new SubtitleDocument(await File.ReadAllBytesAsync(path), "srt").Cues[1].StartMilliseconds);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_directory, "ABC-123-cd2.xunlei.srt")));
            info = await store.OpenAsync(first, info.FileName, false, default);
            await store.CalibrateAsync(first, info.FileName, -32_000, info.CurrentHash, info.MediaStamp, default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            await File.AppendAllTextAsync(first.Path, "changed");
            Assert.Equal(409, (await Assert.ThrowsAsync<ToolException>(() => store.OpenAsync(first, info.FileName, false, default))).Status);
            Assert.Equal(10_000, (await store.OpenAsync(first, info.FileName, true, default)).Cues[1].StartMilliseconds);
            await Assert.ThrowsAsync<ToolException>(() => store.OpenAsync(first, "../ABC-123-cd2.xunlei.srt", false, default));
            Assert.Empty(Directory.GetFiles(data, "original", SearchOption.AllDirectories));
        }
    }

    [Theory]
    [InlineData("utf-8", false)][InlineData("utf-8", true)][InlineData("utf-16", true)]
    public async Task RepairsSrtTimeLinesAndAdjustsCurrentBytesWithoutKeepingTheDownload(string name, bool bom)
    {
        var target = Target("ABC-123.mp4");
        var text = "1\r\n00：00：3\u200B0.040-> 00：00：3\u200B2.040\r\n<b>正文：保留\u200B全角，字样 -> 也保留</b>\r\n\r\n2\r\n00:01:00,000  -->\t00:01:02,000\r\n第二条\r\n";
        var normalized = text.Replace("00：00：3\u200B0.040-> 00：00：3\u200B2.040", "00:00:30.040 --> 00:00:32.040");
        var encoding = Encoding.GetEncoding(name);
        var preamble = bom ? encoding.GetPreamble() : [];
        var original = preamble.Concat(encoding.GetBytes(text)).ToArray();
        var expected = preamble.Concat(encoding.GetBytes(normalized)).ToArray();
        var data = Path.Combine(_directory, "plugin-data");
        var store = new SubtitleStore(data);
        var candidate = new SourceSubtitle("subtitlecat", "ABC-123", "srt", "zh-CN", null);
        var path = await store.SaveDownloadAsync(target, candidate, original, false, default);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        long applied = 0;
        foreach (var offset in new long[] { 30_000, 2_000 })
        {
            var info = await store.OpenAsync(target, Path.GetFileName(path), false, default);
            Assert.Equal(2, info.Cues.Count);
            Assert.Equal(30_040 + applied, info.Cues[0].StartMilliseconds);
            await store.CalibrateAsync(target, info.FileName, offset, info.CurrentHash, info.MediaStamp, default);
            applied += offset;
            Assert.Equal(30_040 + applied, new SubtitleDocument(await File.ReadAllBytesAsync(path), "srt").Cues[0].StartMilliseconds);
        }
        var restore = await store.OpenAsync(target, Path.GetFileName(path), false, default);
        await store.CalibrateAsync(target, restore.FileName, -32_000, restore.CurrentHash, restore.MediaStamp, default);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(data, "original", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("<html>Error</html>")][InlineData("error: source did not provide a subtitle")]
    public async Task DownloadRejectsInvalidContentBeforeReplacingTheExistingSubtitle(string invalid)
    {
        var target = Target("ABC-123.mp4"); var bytes = Encoding.UTF8.GetBytes(Sample("srt"));
        var store = new SubtitleStore(Path.Combine(_directory, "plugin-data"));
        var candidate = new SourceSubtitle("subtitlecat", "name", "srt", "zh-CN", null);
        var path = await store.SaveDownloadAsync(target, candidate, bytes, false, default);
        Assert.EndsWith("ABC-123.subtitlecat.zh-Hans.srt", path);
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "plugin-data"), "original", SearchOption.AllDirectories));
        await Assert.ThrowsAsync<ToolException>(() => store.SaveDownloadAsync(target, candidate, Encoding.UTF8.GetBytes(invalid), true, default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AdvancingCurrentSubtitlePermanentlyRemovesClippedCues()
    {
        var target = Target("ABC-123.mp4");
        var data = Path.Combine(_directory, "data");
        var store = new SubtitleStore(data);
        var path = Path.Combine(_directory, "ABC-123.srt");
        await File.WriteAllTextAsync(path, Sample("srt"));
        Assert.Single(store.List(target));
        var current = await store.OpenAsync(target, Path.GetFileName(path), false, default);
        await store.CalibrateAsync(target, current.FileName, -2000, current.CurrentHash, current.MediaStamp, default);
        current = await new SubtitleStore(data).OpenAsync(target, current.FileName, false, default);
        Assert.Single(current.Cues);
        await store.CalibrateAsync(target, current.FileName, 2000, current.CurrentHash, current.MediaStamp, default);
        Assert.Single(new SubtitleDocument(await File.ReadAllBytesAsync(path), "srt").Cues);
        Assert.Empty(Directory.GetFiles(data, "original", SearchOption.AllDirectories));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
