using System.Buffers.Binary;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed record SpeechWord(int Id, string Text, long StartMilliseconds, long EndMilliseconds);
public sealed record SpeechText(string Text, IReadOnlyList<SpeechWord> Words);
internal sealed record SpeechPhrase(int WordStartId, int WordEndId, string Text);

internal static class SpeechRecognizer
{
    internal static SpeechPhrase[] Phrases(SpeechText speech)
    {
        var result = new List<SpeechPhrase>(); var start = 0;
        for (var index = 0; index < speech.Words.Count; index++)
        {
            var text = string.Concat(speech.Words.Skip(start).Take(index - start + 1).Select(word => word.Text));
            var normalized = AnchorMatcher.Normalize(text);
            var question = normalized.EndsWith("ですか", StringComparison.Ordinal) || normalized.EndsWith("ますか", StringComparison.Ordinal);
            if (index + 1 < speech.Words.Count && speech.Words[index + 1].StartMilliseconds - speech.Words[index].EndMilliseconds < 650 && index - start < 40 && !question && !speech.Words[index].Text.Any(c => "。？！?!".Contains(c))) continue;
            result.Add(new(speech.Words[start].Id, speech.Words[index].Id, string.Concat(speech.Words.Skip(start).Take(index - start + 1).Select(word => word.Text))));
            start = index + 1;
        }
        return result.ToArray();
    }
    public static async Task<SpeechText> RecognizeAsync(string assets, string pcmPath, string temporary, long clipStart, SpeechDetector detector, CancellationToken token)
    {
        var pcm = await File.ReadAllBytesAsync(pcmPath, token);
        var segments = await detector.DetectAsync(pcmPath, 0, token);
        var groups = new List<(long Start, long End)>();
        foreach (var segment in segments)
        {
            var start = Math.Max(0, segment.StartMilliseconds - 200);
            var end = Math.Min(pcm.Length / 32, segment.EndMilliseconds + 200);
            if (groups.Count > 0 && start - groups[^1].End < 700 && end - groups[^1].Start <= 18000)
                groups[^1] = (groups[^1].Start, end);
            else
                for (var position = start; position < end; position += 18000) groups.Add((position, Math.Min(end, position + 18000)));
        }
        var words = new List<SpeechWord>();
        var texts = new List<string>();
        var executable = Path.Combine(assets, OperatingSystem.IsWindows() ? "win-x64/sherpa-onnx-offline.exe" : "linux-x64/bin/sherpa-onnx-offline");
        foreach (var (start, end) in groups)
        {
            token.ThrowIfCancellationRequested();
            var wav = Path.Combine(temporary, "speech.wav");
            await File.WriteAllBytesAsync(wav, Wave(pcm.AsSpan((int)start * 32, (int)(end - start) * 32)), token);
            var output = await MediaProcess.CaptureAsync(executable, ["--print-args=false", "--num-threads=2", "--provider=cpu", "--sense-voice-language=auto", "--sense-voice-use-itn=true", "--tokens=" + Path.Combine(assets, "tokens.txt"), "--sense-voice-model=" + Path.Combine(assets, "sensevoice.onnx"), wav], token);
            var line = output.Split('\n').LastOrDefault(line => line.TrimStart().StartsWith("{\"lang\"", StringComparison.Ordinal));
            if (line is null) throw new ToolException("语音识别没有返回可读取的台词，请换一处播放位置。");
            var recognized = Parse(line, clipStart + start, end - start, words.Count);
            if (recognized.Words.Count == 0) continue;
            texts.Add(recognized.Text);
            words.AddRange(recognized.Words);
        }
        return new(string.Join("\n", texts), words);
    }

    public static SpeechText Parse(string json, long start, long duration, int firstId = 0)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("event").GetString() != "<|Speech|>") return new("", []);
        var tokens = root.GetProperty("tokens").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var times = root.GetProperty("timestamps").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        if (tokens.Length != times.Length || times.Any(time => !double.IsFinite(time) || time < 0 || time * 1000 > duration) || times.Zip(times.Skip(1)).Any(pair => pair.First > pair.Second))
            throw new ToolException("语音识别时间戳异常，请换一处播放位置。");
        var words = tokens.Select((text, index) => new SpeechWord(firstId + index, text.Replace('▁', ' '), start + (long)Math.Round(times[index] * 1000), start + (long)Math.Round(index + 1 < times.Length ? Math.Min(times[index + 1] * 1000, times[index] * 1000 + 180) : Math.Min(duration, times[index] * 1000 + 180)))).ToArray();
        return new(string.Concat(words.Select(word => word.Text)).Trim(), words);
    }

    private static byte[] Wave(ReadOnlySpan<byte> pcm)
    {
        var data = new byte[pcm.Length + 44];
        "RIFF"u8.CopyTo(data); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
        "WAVEfmt "u8.CopyTo(data.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 16000); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), pcm.Length);
        pcm.CopyTo(data.AsSpan(44)); return data;
    }
}
