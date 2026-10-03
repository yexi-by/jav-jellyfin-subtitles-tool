using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public static class SubtitleFormats
{
    public static readonly string[] Supported = ["srt", "ass", "ssa", "vtt"];
    public static string Normalize(string value) => value.Trim().TrimStart('.').ToLowerInvariant();
    public static bool Supports(string format) => Supported.Contains(Normalize(format));
    public static string FileLanguage(string language) => language.ToLowerInvariant() switch
    {
        "zh-cn" => "zh-Hans",
        "zh-tw" => "zh-Hant",
        _ => Regex.IsMatch(language, "^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$") ? language : "und"
    };
}

public sealed record SourceSubtitle(string Source, string Name, string Format, string? Language, Uri? Url);
public sealed record Candidate(string Id, string Source, string Name, string Format, string? Language, string Match, int? PartNumber, bool CanDownload, string? UnavailableReason);
public sealed record CandidateDownload(string MediaPath, SourceSubtitle Subtitle);
public sealed record SearchQuery(string Text, string? Code, int PartNumber, int PartCount)
{
    public string[] Terms => new[] { Text, Code }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public string[] SubtitleCatFallbackTerms
    {
        get
        {
            if (Code is null) return [];
            var separator = Code.LastIndexOf('-');
            var prefix = Code[..separator];
            var digits = Code[(separator + 1)..];
            var number = digits.TrimStart('0');
            if (number.Length == 0) number = "0";
            return new[] { prefix + digits, prefix + " " + digits, prefix + "-" + number, prefix + number, prefix + " " + number }
                .Except(Terms, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}

public static class JavIdentity
{
    private static readonly Regex Fc2 = new(@"(?<![A-Z0-9])FC2[-_\s]*(?:PPV[-_\s]*)?(\d{5,9})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Code = new(@"(?<![A-Z0-9])([A-Z]{2,10})[-_\s]?(\d{2,7})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Part = new(@"(?:^|[-_.\s])(?:CD|DVD|PART|PT|DISC|DISK)[-_\s]?([1-9]\d?|[A-D])(?=$|[-_.\s])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LetterPart = new(@"[-_\s]([AB])(?=$|[.\s])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? Extract(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var fc2 = Fc2.Match(value);
        if (fc2.Success) return "FC2-PPV-" + fc2.Groups[1].Value;
        var code = Code.Matches(value).FirstOrDefault(match => match.Groups[1].Value.ToUpperInvariant() is not ("CD" or "PART" or "DISC" or "DVD"));
        return code is null ? null : code.Groups[1].Value.ToUpperInvariant() + "-" + code.Groups[2].Value;
    }

    public static int? ExtractPart(string value, bool allowLetter = false)
    {
        var match = Part.Match(value);
        if (!match.Success && allowLetter) match = LetterPart.Match(value);
        if (!match.Success) return null;
        var token = match.Groups[1].Value.ToUpperInvariant();
        return int.TryParse(token, out var number) ? number : token[0] - 'A' + 1;
    }

    public static bool SameCode(string? first, string? second)
    {
        if (first is null || second is null) return false;
        static string Key(string value)
        {
            var separator = value.LastIndexOf('-');
            return value[..(separator + 1)] + value[(separator + 1)..].TrimStart('0');
        }
        return string.Equals(Key(first), Key(second), StringComparison.OrdinalIgnoreCase);
    }

    public static string DefaultQuery(string path, string title, int part, int count)
    {
        var code = Extract(Path.GetFileNameWithoutExtension(path)) ?? Extract(Path.GetFileName(Path.GetDirectoryName(path))) ?? Extract(title);
        return code is null ? "" : count > 1 ? $"{code} CD{part}" : code;
    }

    public static string? Match(SourceSubtitle subtitle, SearchQuery query, bool byFile)
    {
        var code = Extract(subtitle.Name);
        if (!byFile && query.Code is not null)
        {
            if (code is null && subtitle.Source == "subtitlecat") return null;
            if (code is not null && !SameCode(query.Code, code)) return null;
        }
        var part = ExtractPart(subtitle.Name, query.PartCount > 1);
        if (query.PartCount > 1 && part is not null && part != query.PartNumber) return $"标记为第 {part} 段";
        if (byFile) return code is not null && query.Code is not null && !SameCode(code, query.Code) ? "按文件查询 · 番号不符，待确认" : "按文件查询 · 请核对内容";
        if (query.PartCount > 1) return part is null ? "番号查询 · 分段待确认" : $"第 {part} 段标记 · 请核对切分位置";
        return code is null ? "匹配待确认" : "番号一致 · 请核对版本";
    }
}
