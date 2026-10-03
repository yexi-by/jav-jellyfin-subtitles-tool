using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UtfUnknown;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed record SubtitleCue(int Index, long StartMilliseconds, long EndMilliseconds, string Text);

/// <summary>修正 SRT 时间行并调整时间字段，保留其他文本、样式、换行及原始编码。</summary>
public sealed class SubtitleDocument
{
    private const string Clock = @"(?:\d{1,3}:)?\d{2}:\d{2}[,.]\d{2,3}";
    private static readonly Regex Timing = new(@"(?m)^(?<start>" + Clock + @")[ \t]+-->[ \t]+(?<end>" + Clock + @")[^\r\n]*", RegexOptions.CultureInvariant);
    private static readonly Regex RepairTiming = new(@"(?m)^(?<start>[0-9:：,.\u200B]+)[ \t]*(?:-->|->)[ \t]*(?<end>[0-9:：,.\u200B]+)(?<suffix>[^\r\n]*)", RegexOptions.CultureInvariant);
    private static readonly Regex InlineTime = new("<(?<time>" + Clock + ")>", RegexOptions.CultureInvariant);
    private readonly byte[] _baseline;
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    private readonly string _format;
    private readonly List<Piece> _pieces = [];
    public IReadOnlyList<SubtitleCue> Cues { get; }

    private sealed record Piece(string Text, int StartIndex = -1, int StartLength = 0, int EndIndex = 0, int EndLength = 0, long Start = 0, long End = 0, string CueText = "");

    public SubtitleDocument(byte[] bytes, string format)
    {
        if (!SubtitleFormats.Supports(format)) throw new ToolException("当前格式不支持校准。");
        if (bytes.Length == 0 || bytes.Length > 20 * 1024 * 1024) throw new ToolException("字幕文件为空或异常大。", 502);
        _format = format;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text;
        try
        {
            (_encoding, _preamble) = DetectEncoding(bytes);
            text = _encoding.GetString(bytes, _preamble.Length, bytes.Length - _preamble.Length);
            if (!_encoding.GetBytes(text).AsSpan().SequenceEqual(bytes.AsSpan(_preamble.Length)))
                throw new ToolException("字幕编码无法完整保留，请选择另一份字幕。", 502);
        }
        catch (Exception ex) when (ex is DecoderFallbackException or EncoderFallbackException)
        { throw new ToolException("字幕包含无法识别的编码内容，请选择另一份字幕。", 502); }
        if (Regex.IsMatch(text.TrimStart(), @"\A(?:<!doctype\s+html|<html\b)", RegexOptions.IgnoreCase))
            throw new ToolException("下载内容是网页，并非有效字幕。", 502);
        var decoded = text;
        if (format == "srt") text = RepairTiming.Replace(text, match =>
        {
            var start = match.Groups["start"].Value.Replace('：', ':').Replace("\u200B", "");
            var end = match.Groups["end"].Value.Replace('：', ':').Replace("\u200B", "");
            if (!Regex.IsMatch(start, @"\A" + Clock + @"\z") || !Regex.IsMatch(end, @"\A" + Clock + @"\z")) return match.Value;
            if (start == match.Groups["start"].Value && end == match.Groups["end"].Value && Timing.IsMatch(match.Value)) return match.Value;
            return start + " --> " + end + match.Groups["suffix"].Value;
        });
        _baseline = text == decoded ? bytes : [.. _preamble, .. _encoding.GetBytes(text)];
        if (format is "ass" or "ssa") ParseAss(text);
        else ParseBlocks(text);
        Cues = _pieces.Where(piece => piece.StartIndex >= 0).Select((piece, index) => new SubtitleCue(index, piece.Start, piece.End, PlainText(piece.CueText))).ToArray();
        if (Cues.Count == 0) throw new ToolException("字幕没有可识别的有效时间轴。", 502);
    }

    public byte[] Shift(long milliseconds)
    {
        if (Math.Abs((decimal)milliseconds) > 86_400_000) throw new ToolException("校准范围为提前或延后 24 小时以内。");
        if (milliseconds == 0) return _baseline;
        var builder = new StringBuilder();
        var remaining = 0;
        foreach (var piece in _pieces)
        {
            if (piece.StartIndex < 0) { builder.Append(piece.Text); continue; }
            var start = Math.Max(0, piece.Start + milliseconds);
            var end = piece.End + milliseconds;
            if (end <= 0) continue;
            var startText = FormatTime(start, piece.Text.Substring(piece.StartIndex, piece.StartLength));
            var endText = FormatTime(end, piece.Text.Substring(piece.EndIndex, piece.EndLength));
            if (ParseTime(endText) <= ParseTime(startText)) continue;
            remaining++;
            var output = piece.Text[..piece.StartIndex] + startText
                + piece.Text[(piece.StartIndex + piece.StartLength)..piece.EndIndex] + endText
                + piece.Text[(piece.EndIndex + piece.EndLength)..];
            if (_format == "vtt") output = InlineTime.Replace(output, match =>
            {
                var value = ParseTime(match.Groups["time"].Value) + milliseconds;
                return value <= start || value >= end ? "" : "<" + FormatTime(value, match.Groups["time"].Value) + ">";
            });
            builder.Append(output);
        }
        if (remaining == 0) throw new ToolException("调整后所有字幕都落在视频开始之前，请检查偏移值。");
        return [.. _preamble, .. _encoding.GetBytes(builder.ToString())];
    }

    private void ParseBlocks(string text)
    {
        if (_format == "vtt" && !text.StartsWith("WEBVTT", StringComparison.Ordinal)) throw new ToolException("VTT 文件缺少 WEBVTT 标识。", 502);
        if (_format == "vtt" && text.Contains("X-TIMESTAMP-MAP", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("这份 VTT 使用分片时间映射，当前无法校准，请选择完整时间轴的字幕。", 502);
        foreach (var block in Regex.Split(text, @"(\r?\n[ \t]*\r?\n)"))
        {
            if (_format == "vtt" && (block.StartsWith("NOTE", StringComparison.Ordinal) || block.StartsWith("STYLE", StringComparison.Ordinal) || block.StartsWith("REGION", StringComparison.Ordinal)))
            { _pieces.Add(new Piece(block)); continue; }
            var match = Timing.Match(block);
            if (!match.Success) { _pieces.Add(new Piece(block)); continue; }
            var start = match.Groups["start"];
            var end = match.Groups["end"];
            AddTimed(block, start.Index, start.Length, end.Index, end.Length, block[(match.Index + match.Length)..].Trim());
        }
    }

    private void ParseAss(string text)
    {
        var events = false;
        string[]? fields = null;
        foreach (Match lineMatch in Regex.Matches(text, @"[^\r\n]*(?:\r\n|\n|\r|$)"))
        {
            var line = lineMatch.Value;
            if (line.Length == 0) continue;
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[')) events = trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
            if (events && trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase)) fields = trimmed[7..].Split(',').Select(field => field.Trim()).ToArray();
            if (!events || !trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) { _pieces.Add(new Piece(line)); continue; }
            fields ??= ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];
            var contentStart = line.IndexOf(':') + 1;
            var values = line[contentStart..].Split(',', fields.Length);
            var startColumn = Array.FindIndex(fields, field => field.Equals("Start", StringComparison.OrdinalIgnoreCase));
            var endColumn = Array.FindIndex(fields, field => field.Equals("End", StringComparison.OrdinalIgnoreCase));
            var textColumn = Array.FindIndex(fields, field => field.Equals("Text", StringComparison.OrdinalIgnoreCase));
            if (values.Length != fields.Length || startColumn < 0 || endColumn <= startColumn || textColumn != fields.Length - 1)
                throw new ToolException("ASS／SSA 的事件字段无法识别，原文件未修改。", 502);
            int Index(int column) => contentStart + values.Take(column).Sum(value => value.Length + 1) + values[column].Length - values[column].TrimStart().Length;
            AddTimed(line, Index(startColumn), values[startColumn].Trim().Length, Index(endColumn), values[endColumn].Trim().Length, values[textColumn].Trim());
        }
    }

    private void AddTimed(string raw, int startIndex, int startLength, int endIndex, int endLength, string text)
    {
        var start = ParseTime(raw.Substring(startIndex, startLength));
        var end = ParseTime(raw.Substring(endIndex, endLength));
        if (end <= start) throw new ToolException("字幕包含结束时间早于开始时间的条目，原文件未修改。", 502);
        _pieces.Add(new Piece(raw, startIndex, startLength, endIndex, endLength, start, end, text));
    }

    private static long ParseTime(string text)
    {
        var fields = text.Replace(',', '.').Split(':');
        if (fields.Length is not (2 or 3)) throw new ToolException("字幕时间格式无法识别。", 502);
        if (!decimal.TryParse(fields[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(fields[^2], out var minutes) || seconds < 0 || seconds >= 60 || minutes is < 0 or >= 60)
            throw new ToolException("字幕时间格式无法识别。", 502);
        var hours = fields.Length == 3 && int.TryParse(fields[0], out var value) ? value : 0;
        return checked(hours * 3_600_000L + minutes * 60_000L + (long)(seconds * 1000));
    }

    private static string FormatTime(long value, string original)
    {
        var time = TimeSpan.FromMilliseconds(value);
        var separator = original.Contains(',') ? ',' : '.';
        var precision = original.Length - original.LastIndexOf(separator) - 1;
        var fraction = precision == 2 ? (time.Milliseconds / 10).ToString("D2", CultureInfo.InvariantCulture) : time.Milliseconds.ToString("D3", CultureInfo.InvariantCulture);
        var hours = (long)time.TotalHours;
        var prefix = original.Count(character => character == ':') == 2 || hours > 0
            ? hours.ToString(original.IndexOf(':') == 1 ? "D1" : "D2", CultureInfo.InvariantCulture) + ":" : "";
        return $"{prefix}{time.Minutes:D2}:{time.Seconds:D2}{separator}{fraction}";
    }

    private static (Encoding Encoding, byte[] Preamble) DetectEncoding(byte[] bytes)
    {
        Encoding[] encodings = [new UTF8Encoding(true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true)];
        foreach (var encoding in encodings)
        {
            var prefix = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(prefix)) return (encoding, prefix);
        }
        // 无 BOM 的 UTF-16 字幕仍以 ASCII 序号或格式头开头。
        if (bytes.Length >= 4 && bytes.Length % 2 == 0)
        {
            if (bytes[0] != 0 && bytes[1] == 0 && bytes[2] != 0 && bytes[3] == 0) return (new UnicodeEncoding(false, false, true), []);
            if (bytes[0] == 0 && bytes[1] != 0 && bytes[2] == 0 && bytes[3] != 0) return (new UnicodeEncoding(true, false, true), []);
        }
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            if (utf8.GetString(bytes).Contains('\0')) throw new DecoderFallbackException();
            return (utf8, []);
        }
        catch (DecoderFallbackException)
        {
            var detected = CharsetDetector.DetectFromBytes(bytes).Detected?.Encoding;
            if (detected is null) throw new ToolException("无法识别字幕编码，请选择另一份字幕。", 502);
            return (Encoding.GetEncoding(detected.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), []);
        }
    }

    private static string PlainText(string value) => System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(value.Replace("\\N", "\n").Replace("\\n", "\n"), @"\{[^}]*\}", ""), "<[^>]*>", "")).Trim();
}
