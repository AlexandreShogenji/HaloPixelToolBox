using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HaloPixelToolBox.Core.Utilities;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public sealed class NetEaseCloudMusicLyricsMemoryScanner
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const int MaxChunkBytes = 4 * 1024 * 1024;
    private const int ChunkOverlapBytes = 64 * 1024;
    private const int MaxCandidateChars = 128 * 1024;
    private const int MinimumTimedLines = 5;

    private static readonly byte[] UnicodeTimestampMarker = Encoding.Unicode.GetBytes("[00:");
    private static readonly Regex TimestampRegex = new(@"\[(?:\d{1,3}:)?\d{1,2}:\d{2}(?:[\.,:]\d{1,3})?\]", RegexOptions.Compiled);
    private static readonly Regex MetadataRegex = new(@"^\[(?:ti|ar|al|by|offset|length|tool|ve|re):[^\]]*\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StyledLineRegex = new(@"<c\s+#[0-9A-Fa-f]{6,8}>(?<text>.*?)</c>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    public NetEaseCloudMusicLyricsScanResult Scan(CancellationToken cancellationToken = default)
    {
        var processes = GetCloudMusicProcesses().ToList();
        if (processes.Count == 0)
            return NetEaseCloudMusicLyricsScanResult.Failed("未检测到网易云音乐进程");

        var failures = new List<string>();
        var successfulResults = new List<NetEaseCloudMusicLyricsScanResult>();
        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = ScanProcess(process, cancellationToken);
                if (result.Success)
                {
                    successfulResults.Add(result);
                    continue;
                }

                failures.Add(result.ErrorMessage ?? $"PID {process.Id} 未找到歌词");
            }
            catch (Exception ex)
            {
                failures.Add($"PID {process.Id} 扫描异常：{ex.Message}");
            }
        }

        if (successfulResults.Count > 0)
        {
            return successfulResults
                .OrderByDescending(result => !string.IsNullOrWhiteSpace(result.CurrentLine))
                .ThenByDescending(result => result.TimedLineCount)
                .ThenByDescending(result => result.LyricsContent.Length)
                .First();
        }

        return NetEaseCloudMusicLyricsScanResult.Failed($"已扫描 {processes.Count} 个 cloudmusic 进程，均未找到可解析 LRC；{string.Join("；", failures)}");
    }

    private NetEaseCloudMusicLyricsScanResult ScanProcess(Process process, CancellationToken cancellationToken)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
        if (handle == nint.Zero)
            return NetEaseCloudMusicLyricsScanResult.Failed($"PID {process.Id} 无法打开进程内存，Win32Error={Marshal.GetLastWin32Error()}");

        try
        {
            var styledLines = new Dictionary<string, int>(StringComparer.Ordinal);
            var seenCandidates = new HashSet<string>(StringComparer.Ordinal);
            var candidates = new List<LrcCandidate>();
            var stats = new ScanStats(process.Id, process.MainWindowTitle);

            ScanReadableRegions(handle, cancellationToken, [UnicodeTimestampMarker], true, stats, decoded =>
            {
                foreach (var line in ExtractStyledLines(decoded))
                    styledLines[line] = styledLines.TryGetValue(line, out var count) ? count + 1 : 1;

                foreach (var candidate in ExtractLrcCandidates(decoded))
                {
                    if (!seenCandidates.Add(candidate))
                        continue;

                    var timedLineCount = CountTimedLines(candidate);
                    stats.CandidateCount++;
                    stats.BestTimedLineCount = Math.Max(stats.BestTimedLineCount, timedLineCount);
                    candidates.Add(new LrcCandidate(candidate, timedLineCount));
                }
            });

            if (candidates.Count == 0)
                return NetEaseCloudMusicLyricsScanResult.Failed($"{stats.BuildSummary()}，未找到可解析 LRC");

            var selectedCandidate = candidates
                .Select(candidate => new SelectedLrcCandidate(
                    candidate,
                    PickCurrentLine(styledLines.Keys, candidate.Content),
                    "styled-line"))
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CurrentLine))
                .OrderByDescending(candidate => candidate.Candidate.TimedLineCount)
                .ThenByDescending(candidate => candidate.Candidate.Content.Length)
                .FirstOrDefault();

            if (selectedCandidate is null)
            {
                foreach (var candidate in candidates
                    .OrderByDescending(candidate => candidate.TimedLineCount)
                    .ThenByDescending(candidate => candidate.Content.Length)
                    .Take(12))
                {
                    var currentLine = DetectCurrentLineByOccurrence(handle, candidate.Content, cancellationToken, stats);
                    if (string.IsNullOrWhiteSpace(currentLine))
                        continue;

                    selectedCandidate = new SelectedLrcCandidate(candidate, currentLine, "current-line-occurrence");
                    break;
                }
            }

            selectedCandidate ??= candidates
                .OrderByDescending(candidate => candidate.TimedLineCount)
                .ThenByDescending(candidate => candidate.Content.Length)
                .Select(candidate => new SelectedLrcCandidate(candidate, null, "largest-candidate"))
                .First();

            return NetEaseCloudMusicLyricsScanResult.Succeeded(
                selectedCandidate.Candidate.Content,
                BuildSourceName(process, selectedCandidate.Candidate.TimedLineCount),
                ResolveTrackWindowTitle(),
                selectedCandidate.CurrentLine,
                stats.BuildSummary(selectedCandidate.SelectionReason),
                selectedCandidate.Candidate.TimedLineCount);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IEnumerable<Process> GetCloudMusicProcesses()
    {
        var desktopLyricsProcess = CloudMusicLyricsReader.GetCloudMusicLyricsProcess();
        return Process.GetProcessesByName("cloudmusic")
            .OrderByDescending(process => desktopLyricsProcess is not null && process.Id == desktopLyricsProcess.Id)
            .ThenByDescending(process => !string.IsNullOrWhiteSpace(process.MainWindowTitle))
            .ThenBy(process => process.Id);
    }

    private static void ScanReadableRegions(
        nint processHandle,
        CancellationToken cancellationToken,
        IReadOnlyList<byte[]> decodeMarkers,
        bool includeStyledMarker,
        ScanStats stats,
        Action<string> onDecodedChunk)
    {
        var address = 0UL;
        var querySize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();

        while (VirtualQueryEx(processHandle, ToIntPtr(address), out var memoryInfo, querySize) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var baseAddress = ToUInt64(memoryInfo.BaseAddress);
            var regionSize = (ulong)memoryInfo.RegionSize;
            if (regionSize == 0)
                break;

            if (IsReadable(memoryInfo))
            {
                stats.ReadableRegionCount++;
                ReadRegion(processHandle, baseAddress, regionSize, cancellationToken, decodeMarkers, includeStyledMarker, stats, onDecodedChunk);
            }

            var nextAddress = baseAddress + regionSize;
            if (nextAddress <= address)
                break;

            address = nextAddress;
        }
    }

    private static void ReadRegion(
        nint processHandle,
        ulong baseAddress,
        ulong regionSize,
        CancellationToken cancellationToken,
        IReadOnlyList<byte[]> decodeMarkers,
        bool includeStyledMarker,
        ScanStats stats,
        Action<string> onDecodedChunk)
    {
        var offset = 0UL;
        while (offset < regionSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min((ulong)MaxChunkBytes, regionSize - offset);
            var buffer = new byte[bytesToRead];
            var readAddress = ToIntPtr(baseAddress + offset);
            if (ReadProcessMemory(processHandle, readAddress, buffer, (nuint)buffer.Length, out var bytesRead) &&
                bytesRead >= (nuint)UnicodeTimestampMarker.Length)
            {
                stats.ReadChunkCount++;
                var validBytes = (int)bytesRead;
                if (ShouldDecode(buffer, validBytes, decodeMarkers, includeStyledMarker))
                {
                    stats.DecodedChunkCount++;
                    onDecodedChunk(Encoding.Unicode.GetString(buffer, 0, validBytes));
                }
            }

            if (regionSize - offset <= (ulong)bytesToRead)
                break;

            offset += (ulong)Math.Max(1, bytesToRead - ChunkOverlapBytes);
        }
    }

    private static IEnumerable<string> ExtractLrcCandidates(string decoded)
    {
        var matches = TimestampRegex.Matches(decoded);
        foreach (Match match in matches)
        {
            var start = FindCandidateStart(decoded, match.Index);
            var end = FindCandidateEnd(decoded, match.Index);
            if (end <= start)
                continue;

            var candidate = NormalizeLrcCandidate(decoded[start..end]);
            if (CountTimedLines(candidate) >= MinimumTimedLines)
                yield return candidate;
        }
    }

    private static string NormalizeLrcCandidate(string raw)
    {
        var lines = raw
            .Replace('\0', '\n')
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var normalizedLines = new List<string>();
        foreach (var line in lines)
        {
            var trimmedLine = TrimToLrcLine(line);
            if (trimmedLine.Length == 0 || trimmedLine.Length > 512)
                continue;

            if (TimestampRegex.IsMatch(trimmedLine) || MetadataRegex.IsMatch(trimmedLine))
                normalizedLines.Add(trimmedLine);
        }

        return string.Join(Environment.NewLine, normalizedLines);
    }

    private static string TrimToLrcLine(string line)
    {
        var firstBracketIndex = line.IndexOf('[');
        if (firstBracketIndex < 0)
            return string.Empty;

        var lrcLine = line[firstBracketIndex..].Trim();
        var firstHardBoundary = lrcLine.IndexOfAny(['\0', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007', '\u0008']);
        if (firstHardBoundary >= 0)
            lrcLine = lrcLine[..firstHardBoundary].Trim();

        return lrcLine;
    }

    private static IEnumerable<string> ExtractStyledLines(string decoded)
    {
        foreach (Match match in StyledLineRegex.Matches(decoded))
        {
            var text = NormalizeLyricText(match.Groups["text"].Value);
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
        }
    }

    private static string? PickCurrentLine(IEnumerable<string> styledLines, string lrcContent)
    {
        var lrcLineLookup = lrcContent
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => NormalizeLyricText(TimestampRegex.Replace(line, string.Empty)))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToHashSet(StringComparer.Ordinal);

        return styledLines
            .Select(NormalizeLyricText)
            .FirstOrDefault(line => lrcLineLookup.Contains(line));
    }

    private static string? DetectCurrentLineByOccurrence(nint processHandle, string lrcContent, CancellationToken cancellationToken, ScanStats stats)
    {
        var lyricLines = ExtractLyricTexts(lrcContent)
            .Where(line => line.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .Take(80)
            .ToList();

        if (lyricLines.Count == 0)
            return null;

        var expectedCounts = lyricLines.ToDictionary(
            line => line,
            line => CountOccurrences(lrcContent, line),
            StringComparer.Ordinal);
        var actualCounts = lyricLines.ToDictionary(line => line, _ => 0, StringComparer.Ordinal);
        var markers = lyricLines
            .Select(line => Encoding.Unicode.GetBytes(line[..Math.Min(line.Length, 8)]))
            .ToList();

        ScanReadableRegions(processHandle, cancellationToken, markers, false, stats, decoded =>
        {
            foreach (var line in lyricLines)
                actualCounts[line] += CountOccurrences(decoded, line);
        });

        return actualCounts
            .Select(pair => new
            {
                Line = pair.Key,
                ExtraCount = pair.Value - expectedCounts[pair.Key]
            })
            .Where(candidate => candidate.ExtraCount > 0)
            .OrderByDescending(candidate => candidate.ExtraCount)
            .ThenByDescending(candidate => candidate.Line.Length)
            .Select(candidate => candidate.Line)
            .FirstOrDefault();
    }

    private static IEnumerable<string> ExtractLyricTexts(string lrcContent)
    {
        return lrcContent
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => TimestampRegex.IsMatch(line))
            .Select(line => NormalizeLyricText(TimestampRegex.Replace(line, string.Empty)))
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static string NormalizeLyricText(string text)
    {
        var decoded = WebUtility.HtmlDecode(text);
        var withoutTags = HtmlTagRegex.Replace(decoded, string.Empty);
        return new string(withoutTags.Where(character => !char.IsControl(character)).ToArray()).Trim();
    }

    private static int FindCandidateStart(string decoded, int index)
    {
        var minIndex = Math.Max(0, index - MaxCandidateChars);
        for (var i = index - 1; i >= minIndex; i--)
        {
            if (IsHardBoundary(decoded[i]))
                return i + 1;
        }

        return minIndex;
    }

    private static int FindCandidateEnd(string decoded, int index)
    {
        var maxIndex = Math.Min(decoded.Length, index + MaxCandidateChars);
        for (var i = index; i < maxIndex; i++)
        {
            if (IsHardBoundary(decoded[i]))
                return i;
        }

        return maxIndex;
    }

    private static bool IsHardBoundary(char character)
    {
        return character == '\0' ||
            char.IsControl(character) &&
            character is not '\r' and not '\n' and not '\t';
    }

    private static int CountTimedLines(string content)
    {
        return content
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => TimestampRegex.IsMatch(line));
    }

    private static bool ShouldDecode(byte[] buffer, int length, IReadOnlyList<byte[]> decodeMarkers, bool includeStyledMarker)
    {
        if (decodeMarkers.Any(marker => ContainsMarker(buffer, length, marker)))
            return true;

        return includeStyledMarker && ContainsStyledMarker(buffer, length);
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while (index < content.Length)
        {
            index = content.IndexOf(value, index, StringComparison.Ordinal);
            if (index < 0)
                break;

            count++;
            index += value.Length;
        }

        return count;
    }

    private static bool ContainsMarker(byte[] buffer, int length, byte[] marker)
    {
        for (var index = 0; index <= length - marker.Length; index += 2)
        {
            var matched = true;
            for (var markerIndex = 0; markerIndex < marker.Length; markerIndex++)
            {
                if (buffer[index + markerIndex] == marker[markerIndex])
                    continue;

                matched = false;
                break;
            }

            if (matched)
                return true;
        }

        return false;
    }

    private static bool ContainsStyledMarker(byte[] buffer, int length)
    {
        var marker = Encoding.Unicode.GetBytes("<c #");
        return ContainsMarker(buffer, length, marker);
    }

    private static bool IsReadable(MemoryBasicInformation memoryInfo)
    {
        return memoryInfo.State == MemCommit &&
            (memoryInfo.Protect & PageNoAccess) == 0 &&
            (memoryInfo.Protect & PageGuard) == 0;
    }

    private static string BuildSourceName(Process process, int lineCount)
    {
        var version = "unknown";
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(process.MainModule?.FileName ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(versionInfo.FileVersion) &&
                Version.TryParse(versionInfo.FileVersion, out var parsedVersion))
                version = parsedVersion.ToString(3);
        }
        catch
        {
        }

        return $"网易云音乐内存 LRC {version}（{lineCount} 行）";
    }

    private static string ResolveTrackWindowTitle()
    {
        return Process.GetProcessesByName("cloudmusic")
            .Select(process => process.MainWindowTitle?.Trim())
            .Where(title => !string.IsNullOrWhiteSpace(title) &&
                !string.Equals(title, "桌面歌词", StringComparison.Ordinal))
            .OrderByDescending(title => title!.Contains(" - ", StringComparison.Ordinal))
            .FirstOrDefault() ?? string.Empty;
    }

    private static nint ToIntPtr(ulong value) => unchecked((nint)(long)value);

    private static ulong ToUInt64(nint value) => unchecked((ulong)value.ToInt64());

    private sealed class ScanStats
    {
        public int ProcessId { get; }
        public string WindowTitle { get; }
        public int ReadableRegionCount { get; set; }
        public int ReadChunkCount { get; set; }
        public int DecodedChunkCount { get; set; }
        public int CandidateCount { get; set; }
        public int BestTimedLineCount { get; set; }

        public ScanStats(int processId, string windowTitle)
        {
            ProcessId = processId;
            WindowTitle = string.IsNullOrWhiteSpace(windowTitle) ? "<empty>" : windowTitle;
        }

        public string BuildSummary(string selectedBy = "")
        {
            var selectedByText = string.IsNullOrWhiteSpace(selectedBy) ? string.Empty : $", selectedBy={selectedBy}";
            return $"PID {ProcessId}({WindowTitle}) readable={ReadableRegionCount}, chunks={ReadChunkCount}, decoded={DecodedChunkCount}, candidates={CandidateCount}, bestTimedLines={BestTimedLineCount}{selectedByText}";
        }
    }

    private sealed record LrcCandidate(string Content, int TimedLineCount);

    private sealed record SelectedLrcCandidate(LrcCandidate Candidate, string? CurrentLine, string SelectionReason);

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

public sealed record NetEaseCloudMusicLyricsScanResult(
    bool Success,
    string LyricsContent,
    string SourceName,
    string TrackWindowTitle,
    string? CurrentLine,
    string? ErrorMessage,
    string Diagnostics,
    int TimedLineCount)
{
    public static NetEaseCloudMusicLyricsScanResult Succeeded(string lyricsContent, string sourceName, string trackWindowTitle, string? currentLine, string diagnostics, int timedLineCount)
    {
        return new NetEaseCloudMusicLyricsScanResult(true, lyricsContent, sourceName, trackWindowTitle, currentLine, null, diagnostics, timedLineCount);
    }

    public static NetEaseCloudMusicLyricsScanResult Failed(string errorMessage)
    {
        return new NetEaseCloudMusicLyricsScanResult(false, string.Empty, string.Empty, string.Empty, null, errorMessage, errorMessage, 0);
    }
}
