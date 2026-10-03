using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

public sealed class ToolException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record HashPair(string Cid, string Gcid)
{
    public string Serialize() => $"CID={Cid}\nGCID={Gcid}\n";
}

public sealed record HashProgress(string Phase, long Read = 0, long Total = 0);

/// <summary>记录文件保存视频的 CID、GCID，计算以只读方式访问视频。</summary>
public sealed class HashRecords : IDisposable
{
    private readonly SemaphoreSlim _calculation = new(1, 1);
    private int _interactiveRequests;
    private static readonly Regex RecordPattern = new("\\ACID=([0-9A-F]{40})\\r?\\nGCID=([0-9A-F]{40})\\r?\\n\\z", RegexOptions.CultureInvariant);

    public static string RecordPath(string mediaPath) => mediaPath + ".cidgcid";

    public static async Task<HashPair?> ReadAsync(string mediaPath, CancellationToken cancellationToken)
    {
        var path = RecordPath(mediaPath);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 256) throw InvalidRecord();
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var match = RecordPattern.Match(text);
        if (!match.Success) throw InvalidRecord();
        return new HashPair(match.Groups[1].Value, match.Groups[2].Value);
    }

    private static ToolException InvalidRecord() => new("CID、GCID 记录文件格式不正确，请检查同名 .cidgcid 文件。现有记录未被覆盖。");

    public async Task<HashPair> GetOrCreateAsync(string mediaPath, Func<HashProgress, Task> progress, CancellationToken cancellationToken)
        => (await GetOrCreateCoreAsync(mediaPath, progress, null, cancellationToken)).Pair;

    public Task<(HashPair Pair, bool Created)> GetOrCreateWhenIdleAsync(string mediaPath, Func<HashProgress, Task> progress, Func<bool> isIdle, CancellationToken cancellationToken)
        => GetOrCreateCoreAsync(mediaPath, progress, isIdle, cancellationToken);

    private async Task<(HashPair Pair, bool Created)> GetOrCreateCoreAsync(string mediaPath, Func<HashProgress, Task> progress, Func<bool>? isIdle, CancellationToken cancellationToken)
    {
        VideoTrimmer.CheckAvailable(mediaPath);
        var existing = isIdle is null ? await ReadAsync(mediaPath, cancellationToken) : null;
        if (existing is not null) return (existing, false);
        var interactive = isIdle is null;
        if (interactive) Interlocked.Increment(ref _interactiveRequests);
        var ownsGate = false;

        bool ShouldWait() => isIdle is not null && (!isIdle() || Volatile.Read(ref _interactiveRequests) > 0);

        async Task EnterAsync(HashProgress current)
        {
            var reportedPause = false;
            while (true)
            {
                while (ShouldWait())
                {
                    if (!reportedPause) { await progress(current with { Phase = "paused" }); reportedPause = true; }
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                await _calculation.WaitAsync(cancellationToken);
                ownsGate = true;
                if (!ShouldWait()) return;
                _calculation.Release(); ownsGate = false;
            }
        }

        async Task ReportAsync(HashProgress current)
        {
            if (ShouldWait())
            {
                // 保留流位置和 SHA-1 状态，释放计算资源供手动请求使用。
                _calculation.Release(); ownsGate = false;
                await EnterAsync(current);
            }
            await progress(current);
        }

        try
        {
            await progress(new HashProgress("waiting"));
            await EnterAsync(new HashProgress("waiting"));
            existing = await ReadAsync(mediaPath, cancellationToken);
            if (existing is not null) return (existing, false);
            var temp = RecordPath(mediaPath) + $".{Guid.NewGuid():N}.tmp";
            try
            {
                HashPair pair;
                var mediaStamp = MediaFiles.Stamp(mediaPath);
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                {
                    pair = await CalculateAsync(mediaPath, ReportAsync, cancellationToken);
                    // 暂停期间手动请求可能已完成同一视频的记录，直接采用已保存的值。
                    existing = await ReadAsync(mediaPath, cancellationToken);
                    if (existing is not null) return (existing, false);
                    await output.WriteAsync(Encoding.UTF8.GetBytes(pair.Serialize()), cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                using var publish = await MediaFiles.EnterAsync(mediaPath, cancellationToken);
                VideoTrimmer.CheckAvailable(mediaPath);
                if (MediaFiles.Stamp(mediaPath) != mediaStamp) throw new ToolException("计算期间视频发生变化，请重新计算。");
                File.Move(temp, RecordPath(mediaPath));
                return (pair, true);
            }
            finally { File.Delete(temp); }
        }
        finally
        {
            if (ownsGate) _calculation.Release();
            if (interactive) Interlocked.Decrement(ref _interactiveRequests);
        }
    }

    public static async Task<HashPair> CalculateAsync(string mediaPath, Func<HashProgress, Task> progress, CancellationToken cancellationToken)
    {
        VideoTrimmer.CheckAvailable(mediaPath);
        var before = new FileInfo(mediaPath);
        var size = before.Length;
        var modified = before.LastWriteTimeUtc;
        await using var input = new FileStream(mediaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, true);
        // SHA-1 是迅雷协议的组成部分，不用于安全校验。
        using var cidHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        if (size < 0xF000)
        {
            var small = new byte[(int)size];
            await input.ReadExactlyAsync(small, cancellationToken);
            cidHash.AppendData(small);
        }
        else
        {
            var segment = new byte[0x5000];
            foreach (var offset in new[] { 0, size / 3, size - segment.Length })
            {
                input.Position = offset;
                await input.ReadExactlyAsync(segment, cancellationToken);
                cidHash.AppendData(segment);
            }
        }
        var cid = Convert.ToHexString(cidHash.GetHashAndReset());
        input.Position = 0;
        var pieceSize = 0x40000;
        while (size / pieceSize > 0x200 && pieceSize < 0x200000) pieceSize <<= 1;
        var piece = new byte[pieceSize];
        using var gcidHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        long read = 0;
        var reportClock = Stopwatch.StartNew();
        await progress(new HashProgress("hashing", 0, size));
        while (true)
        {
            var count = await input.ReadAtLeastAsync(piece, piece.Length, false, cancellationToken);
            if (count == 0) break;
            gcidHash.AppendData(SHA1.HashData(piece.AsSpan(0, count)));
            read += count;
            if (reportClock.ElapsedMilliseconds >= 200 || read >= size)
            {
                await progress(new HashProgress("hashing", Math.Min(read, size), size));
                reportClock.Restart();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        VideoTrimmer.CheckAvailable(mediaPath);
        var after = new FileInfo(mediaPath);
        if (!after.Exists || after.Length != size || after.LastWriteTimeUtc != modified || read != size)
            throw new ToolException("计算期间视频文件发生变化，请等待文件写入完成后重试。");
        return new HashPair(cid, Convert.ToHexString(gcidHash.GetHashAndReset()));
    }

    public void Dispose() => _calculation.Dispose();
}
