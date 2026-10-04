using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

internal static class MediaProcess
{
    public static async Task<string> CaptureAsync(string executable, string[] arguments, CancellationToken token)
    {
        var result = new StringBuilder();
        await RunAsync(executable, arguments, line =>
        {
            if (result.Length > 8 * 1024 * 1024) throw new ToolException("媒体分析结果异常大，请选择另一处位置。");
            result.AppendLine(line);
        }, token);
        return result.ToString();
    }

    public static async Task RunAsync(string executable, string[] arguments, Action<string> output, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { throw new ToolException("无法启动媒体分析程序，请检查服务器媒体编码器及插件安装。"); }
        using var killed = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = ReadErrorsAsync(process.StandardError, token);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token) is { } line) output(line);
            await process.WaitForExitAsync(token);
            var detail = await errors;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("媒体分析程序失败：" + detail[..Math.Min(detail.Length, 1500)]);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
            if (result.Length < 1500) result.Append(buffer, 0, Math.Min(count, 1500 - result.Length));
        return result.ToString();
    }
}
