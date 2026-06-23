using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

/// <summary>
/// 从 QQ 音乐进程读取其已解密的 UTF-8 QRC 歌词。
/// QRC 不保证保存为连续字符串，因此按时间行收集并重建时间轴。
/// </summary>
public sealed class QQMusicLyricsMemoryScanner
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const int MaxChunkBytes = 4 * 1024 * 1024;
    private const int ChunkOverlapBytes = 64 * 1024;
    private const int MaxCandidateChars = 512 * 1024;
    private const int MinimumTimedLines = 5;
    // QQ 的 QRC 在某些版本中会跨多个堆块保存；只要收集到足够的有效行即可
    // 锁定当前曲目，避免为了等待完整缓存而长时间扫描整个进程。
    private const int PreferredTrackLineCount = 12;

    private static readonly Regex TitleRegex = new(@"\[ti:(?<title>[^\]\r\n]+)\]", RegexOptions.Compiled);
    private static readonly Regex ArtistRegex = new(@"\[ar:(?<artist>[^\]\r\n]+)\]", RegexOptions.Compiled);
    private static readonly Regex QrcLineRegex = new(@"\[(?<start>\d{1,7}),(?<duration>\d{1,7})\](?<text>[^\r\n]{1,512})", RegexOptions.Compiled);
    private static readonly Regex WordTimingRegex = new(@"\(\d{1,7},\d{1,7}\)", RegexOptions.Compiled);

    public QQMusicLyricsScanResult Scan(string preferredTitle, string preferredArtist, CancellationToken cancellationToken = default)
    {
        var processes = GetQQMusicProcesses().ToList();
        if (processes.Count == 0)
            return QQMusicLyricsScanResult.Failed("未检测到 QQ 音乐进程");

        var candidates = new List<QrcTrackCandidate>();
        var diagnostics = new List<string>();

        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = ScanProcess(process, preferredTitle, preferredArtist, cancellationToken, candidates);
                diagnostics.Add(result);

                var preferredCandidate = candidates
                    .Where(candidate => IsPreferredCandidate(candidate, preferredTitle, preferredArtist))
                    .OrderByDescending(candidate => candidate.Lines.Count)
                    .FirstOrDefault();
                if (preferredCandidate?.Lines.Count >= PreferredTrackLineCount)
                    return BuildSuccess(preferredCandidate, diagnostics);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"PID {process.Id} 扫描异常：{ex.Message}");
            }
        }

        var selectedCandidate = candidates
            .OrderByDescending(candidate => IsPreferredCandidate(candidate, preferredTitle, preferredArtist))
            .ThenByDescending(candidate => candidate.Lines.Count)
            .ThenByDescending(candidate => candidate.Title.Length)
            .FirstOrDefault();

        return selectedCandidate is null
            ? QQMusicLyricsScanResult.Failed($"已扫描 {processes.Count} 个 QQ 音乐进程，未找到可解析的 QRC 歌词；{string.Join("；", diagnostics)}")
            : BuildSuccess(selectedCandidate, diagnostics);
    }

    private static QQMusicLyricsScanResult BuildSuccess(QrcTrackCandidate candidate, IReadOnlyList<string> diagnostics)
    {
        var lines = candidate.Lines
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToList();
        for (var index = 0; index < lines.Count - 1; index++)
        {
            if (lines[index].End <= lines[index].Start)
                lines[index].End = lines[index + 1].Start;
        }

        if (lines.Count > 0 && lines[^1].End <= lines[^1].Start)
            lines[^1].End = lines[^1].Start + TimeSpan.FromSeconds(3);

        return QQMusicLyricsScanResult.Succeeded(
            candidate.Title,
            candidate.Artist,
            lines,
            $"QQ 音乐内存 QRC（{lines.Count} 行）",
            string.Join("；", diagnostics));
    }

    private static string ScanProcess(
        Process process,
        string preferredTitle,
        string preferredArtist,
        CancellationToken cancellationToken,
        ICollection<QrcTrackCandidate> candidates)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
        if (handle == nint.Zero)
            return $"PID {process.Id} 无法打开进程内存（Win32Error={Marshal.GetLastWin32Error()}）";

        var readableRegions = 0;
        var decodedChunks = 0;
        var candidateCountBefore = candidates.Count;
        try
        {
            var address = 0UL;
            var querySize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
            while (VirtualQueryEx(handle, ToIntPtr(address), out var memoryInfo, querySize) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // QQ 音乐的已解密 QRC 位于 32 位进程的私有堆中；跳过映像/映射区，
                // 既避开资源中的歌词模板，也避免首次加载时做无意义的大范围扫描。
                if (address >= 0x80000000)
                    break;

                var baseAddress = ToUInt64(memoryInfo.BaseAddress);
                var regionSize = (ulong)memoryInfo.RegionSize;
                if (regionSize == 0)
                    break;

                if (IsReadable(memoryInfo))
                {
                    readableRegions++;
                    var foundPreferredTrack = ReadRegion(handle, baseAddress, regionSize, cancellationToken, decoded =>
                    {
                        decodedChunks++;
                        CollectCandidates(decoded, preferredTitle, preferredArtist, candidates);
                        return candidates.Any(candidate =>
                            IsPreferredCandidate(candidate, preferredTitle, preferredArtist) &&
                            candidate.Lines.Count >= PreferredTrackLineCount);
                    });
                    if (foundPreferredTrack)
                        break;
                }

                var nextAddress = baseAddress + regionSize;
                if (nextAddress <= address)
                    break;

                address = nextAddress;
            }
        }
        finally
        {
            CloseHandle(handle);
        }

        return $"PID {process.Id}({NormalizeWindowTitle(process.MainWindowTitle)}) readable={readableRegions}, decoded={decodedChunks}, candidates={candidates.Count - candidateCountBefore}";
    }

    private static bool ReadRegion(nint processHandle, ulong baseAddress, ulong regionSize, CancellationToken cancellationToken, Func<string, bool> onDecodedChunk)
    {
        var offset = 0UL;
        while (offset < regionSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min((ulong)MaxChunkBytes, regionSize - offset);
            var buffer = new byte[bytesToRead];
            if (ReadProcessMemory(processHandle, ToIntPtr(baseAddress + offset), buffer, (nuint)buffer.Length, out var bytesRead) &&
                bytesRead > 0)
            {
                var decoded = Encoding.UTF8.GetString(buffer, 0, (int)bytesRead);
                if (decoded.Contains("[ti:", StringComparison.Ordinal) && decoded.Contains("[", StringComparison.Ordinal))
                    if (onDecodedChunk(decoded))
                        return true;
            }

            if (regionSize - offset <= (ulong)bytesToRead)
                break;

            offset += (ulong)Math.Max(1, bytesToRead - ChunkOverlapBytes);
        }

        return false;
    }

    private static void CollectCandidates(string decoded, string preferredTitle, string preferredArtist, ICollection<QrcTrackCandidate> candidates)
    {
        var titleMatches = TitleRegex.Matches(decoded);
        for (var titleIndex = 0; titleIndex < titleMatches.Count; titleIndex++)
        {
            var titleMatch = titleMatches[titleIndex];
            var nextTitleIndex = titleIndex + 1 < titleMatches.Count
                ? titleMatches[titleIndex + 1].Index
                : decoded.Length;
            var candidateEnd = Math.Min(nextTitleIndex, titleMatch.Index + MaxCandidateChars);
            if (candidateEnd <= titleMatch.Index)
                continue;

            var content = decoded[titleMatch.Index..candidateEnd];
            var title = NormalizeMetadata(titleMatch.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            if (!string.IsNullOrWhiteSpace(preferredTitle) &&
                !NormalizeForComparison(title).Equals(NormalizeForComparison(preferredTitle), StringComparison.Ordinal))
            {
                continue;
            }

            var artist = NormalizeMetadata(ArtistRegex.Match(content).Groups["artist"].Value);
            var lines = ParseQrcLines(content);
            if (lines.Count < MinimumTimedLines)
                continue;

            var candidate = new QrcTrackCandidate(title, artist, lines);
            if (IsDuplicateCandidate(candidate, candidates))
                continue;

            candidates.Add(candidate);
        }
    }

    private static Dictionary<long, SubtitleCue> ParseQrcLines(string content)
    {
        var lines = new Dictionary<long, SubtitleCue>();
        foreach (Match match in QrcLineRegex.Matches(content))
        {
            if (!long.TryParse(match.Groups["start"].Value, out var startMilliseconds) ||
                !long.TryParse(match.Groups["duration"].Value, out var durationMilliseconds) ||
                startMilliseconds < 0 || startMilliseconds > TimeSpan.FromHours(2).TotalMilliseconds ||
                durationMilliseconds < 0 || durationMilliseconds > TimeSpan.FromMinutes(2).TotalMilliseconds)
            {
                continue;
            }

            var text = NormalizeLineText(match.Groups["text"].Value);
            if (!IsPlausibleLyricLine(text))
                continue;

            var start = TimeSpan.FromMilliseconds(startMilliseconds);
            var end = start + TimeSpan.FromMilliseconds(durationMilliseconds);
            var cue = new SubtitleCue
            {
                Start = start,
                End = end,
                Text = text
            };

            if (!lines.TryGetValue(startMilliseconds, out var existing) || cue.Text.Length > existing.Text.Length)
                lines[startMilliseconds] = cue;
        }

        return lines;
    }

    private static bool IsDuplicateCandidate(QrcTrackCandidate candidate, IEnumerable<QrcTrackCandidate> candidates)
    {
        return candidates.Any(existing =>
            NormalizeForComparison(existing.Title).Equals(NormalizeForComparison(candidate.Title), StringComparison.Ordinal) &&
            existing.Lines.Count >= candidate.Lines.Count &&
            existing.Lines.First().Value.Text.Equals(candidate.Lines.First().Value.Text, StringComparison.Ordinal));
    }

    private static bool IsPreferredCandidate(QrcTrackCandidate candidate, string preferredTitle, string preferredArtist)
    {
        var candidateTitle = NormalizeForComparison(candidate.Title);
        var title = NormalizeForComparison(preferredTitle);
        if (string.IsNullOrWhiteSpace(title) || !candidateTitle.Equals(title, StringComparison.Ordinal))
            return false;

        var artist = NormalizeForComparison(preferredArtist);
        return string.IsNullOrWhiteSpace(artist) ||
            string.IsNullOrWhiteSpace(candidate.Artist) ||
            NormalizeForComparison(candidate.Artist).Contains(artist, StringComparison.Ordinal) ||
            artist.Contains(NormalizeForComparison(candidate.Artist), StringComparison.Ordinal);
    }

    private static string NormalizeMetadata(string text)
    {
        return new string(text.Where(character => !char.IsControl(character)).ToArray()).Trim();
    }

    private static string NormalizeLineText(string text)
    {
        var withoutTiming = WordTimingRegex.Replace(text, string.Empty);
        // QQ 音乐会把 QRC 的逐字时间信息和歌词对象交错放进内存。
        // 时间数字附近偶尔会夹进无效 UTF-8 字节；保留可显示歌词字符即可，
        // 不让这些字节导致整句歌词被丢弃。
        var lyricCharacters = withoutTiming.Where(character =>
            !char.IsControl(character) &&
            character != '\uFFFD' &&
            (char.IsLetter(character) ||
             char.IsWhiteSpace(character) ||
             character is '\'' or '’' or '-' or '—' or '…' or '，' or '。' or '！' or '？' or '、' or '：' or '；' or '(' or ')' or '（' or '）'));
        return Regex.Replace(new string(lyricCharacters.ToArray()), @"\s+", " ").Trim();
    }

    private static string NormalizeForComparison(string text)
    {
        return new string(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)).ToArray())
            .Trim()
            .ToUpperInvariant();
    }

    private static bool IsPlausibleLyricLine(string text)
    {
        return text.Length is > 0 and <= 160 &&
            text.Any(character => char.IsLetterOrDigit(character) || IsCjkCharacter(character));
    }

    private static bool IsCjkCharacter(char character)
    {
        return character is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\u3040' and <= '\u30FF'
            or >= '\uF900' and <= '\uFAFF';
    }

    private static IEnumerable<Process> GetQQMusicProcesses()
    {
        return Process.GetProcessesByName("QQMusic")
            .OrderByDescending(process => string.Equals(process.MainWindowTitle, "桌面歌词", StringComparison.Ordinal))
            .ThenByDescending(process => !string.IsNullOrWhiteSpace(process.MainWindowTitle))
            .ThenBy(process => process.Id);
    }

    private static bool IsReadable(MemoryBasicInformation memoryInfo)
    {
        return memoryInfo.State == MemCommit &&
            memoryInfo.Type == MemPrivate &&
            (memoryInfo.Protect & PageNoAccess) == 0 &&
            (memoryInfo.Protect & PageGuard) == 0;
    }

    private static string NormalizeWindowTitle(string? title) => string.IsNullOrWhiteSpace(title) ? "<empty>" : title.Trim();

    private static nint ToIntPtr(ulong value) => unchecked((nint)(long)value);

    private static ulong ToUInt64(nint value) => unchecked((ulong)value.ToInt64());

    private sealed record QrcTrackCandidate(string Title, string Artist, Dictionary<long, SubtitleCue> Lines);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(nint processHandle, nint address, out MemoryBasicInformation buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint processHandle, nint baseAddress, byte[] buffer, nuint size, out nuint bytesRead);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}

public sealed record QQMusicLyricsScanResult(
    bool Success,
    string Title,
    string Artist,
    IReadOnlyList<SubtitleCue> Lines,
    string SourceName,
    string Diagnostics,
    string? ErrorMessage)
{
    public static QQMusicLyricsScanResult Succeeded(string title, string artist, IReadOnlyList<SubtitleCue> lines, string sourceName, string diagnostics)
    {
        return new QQMusicLyricsScanResult(true, title, artist, lines, sourceName, diagnostics, null);
    }

    public static QQMusicLyricsScanResult Failed(string errorMessage)
    {
        return new QQMusicLyricsScanResult(false, string.Empty, string.Empty, [], string.Empty, errorMessage, errorMessage);
    }
}
