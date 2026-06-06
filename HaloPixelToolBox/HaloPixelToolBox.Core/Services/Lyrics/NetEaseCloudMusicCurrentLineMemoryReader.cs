using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HaloPixelToolBox.Core.Utilities;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public sealed class NetEaseCloudMusicCurrentLineMemoryReader
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const int MaxChunkBytes = 2 * 1024 * 1024;
    private const int ReadAroundBytes = 2048;
    private const int MaxCachedCandidates = 8;

    private static readonly byte[] StyledMarker = Encoding.Unicode.GetBytes("<c #");
    private static readonly Regex StyledLineRegex = new(@"<c\s+#[0-9A-Fa-f]{6,8}>(?<text>.*?)</c>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    private readonly List<CurrentLineCandidate> candidates = [];
    private DateTimeOffset lastRelocatedAt;
    private string lastLine = string.Empty;

    public NetEaseCloudMusicCurrentLineReadResult ReadCurrentLine(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cachedLine = TryReadCachedCandidates();
        if (!string.IsNullOrWhiteSpace(cachedLine))
        {
            lastLine = cachedLine;
            return NetEaseCloudMusicCurrentLineReadResult.Succeeded(cachedLine, "cached-current-line");
        }

        var relocated = Relocate(cancellationToken);
        if (!relocated.Success)
            return relocated;

        lastLine = relocated.Line;
        return relocated;
    }

    public void Reset()
    {
        candidates.Clear();
        lastLine = string.Empty;
        lastRelocatedAt = default;
    }

    private NetEaseCloudMusicCurrentLineReadResult Relocate(CancellationToken cancellationToken)
    {
        candidates.Clear();
        var processes = GetCloudMusicProcesses().ToList();
        if (processes.Count == 0)
            return NetEaseCloudMusicCurrentLineReadResult.Failed("未检测到网易云音乐进程");

        var diagnostics = new List<string>();
        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var processCandidates = ScanProcessForStyledLines(process, cancellationToken, diagnostics);
            foreach (var candidate in processCandidates)
            {
                if (candidates.Any(existing => existing.ProcessId == candidate.ProcessId && existing.Address == candidate.Address))
                    continue;

                candidates.Add(candidate);
                if (candidates.Count >= MaxCachedCandidates)
                    break;
            }

            if (candidates.Count >= MaxCachedCandidates)
                break;
        }

        lastRelocatedAt = DateTimeOffset.Now;
        var line = TryReadCachedCandidates();
        if (!string.IsNullOrWhiteSpace(line))
            return NetEaseCloudMusicCurrentLineReadResult.Succeeded(line, $"relocated-current-line；{string.Join("；", diagnostics)}");

        return NetEaseCloudMusicCurrentLineReadResult.Failed($"未定位到网易云当前桌面歌词缓存；{string.Join("；", diagnostics)}");
    }

    private List<CurrentLineCandidate> ScanProcessForStyledLines(Process process, CancellationToken cancellationToken, List<string> diagnostics)
    {
        var result = new List<CurrentLineCandidate>();
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
        if (handle == nint.Zero)
        {
            diagnostics.Add($"PID {process.Id} openFailed={Marshal.GetLastWin32Error()}");
            return result;
        }

        var readableRegions = 0;
        var decodedChunks = 0;
        try
        {
            var address = 0UL;
            var querySize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
            while (VirtualQueryEx(handle, ToIntPtr(address), out var memoryInfo, querySize) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var baseAddress = ToUInt64(memoryInfo.BaseAddress);
                var regionSize = (ulong)memoryInfo.RegionSize;
                if (regionSize == 0)
                    break;

                if (IsReadable(memoryInfo))
                {
                    readableRegions++;
                    ScanRegionForStyledLines(handle, process, baseAddress, regionSize, result, ref decodedChunks, cancellationToken);
                }

                if (result.Count >= MaxCachedCandidates)
                    break;

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

        diagnostics.Add($"PID {process.Id}({NormalizeWindowTitle(process.MainWindowTitle)}) readable={readableRegions}, decoded={decodedChunks}, currentCandidates={result.Count}");
        return result;
    }

    private static void ScanRegionForStyledLines(
        nint handle,
        Process process,
        ulong baseAddress,
        ulong regionSize,
        List<CurrentLineCandidate> result,
        ref int decodedChunks,
        CancellationToken cancellationToken)
    {
        var offset = 0UL;
        while (offset < regionSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min((ulong)MaxChunkBytes, regionSize - offset);
            var buffer = new byte[bytesToRead];
            var readAddress = baseAddress + offset;
            if (ReadProcessMemory(handle, ToIntPtr(readAddress), buffer, (nuint)buffer.Length, out var bytesRead) &&
                bytesRead >= (nuint)StyledMarker.Length)
            {
                var validBytes = (int)bytesRead;
                if (ContainsMarker(buffer, validBytes, StyledMarker))
                {
                    decodedChunks++;
                    var decoded = Encoding.Unicode.GetString(buffer, 0, validBytes);
                    foreach (Match match in StyledLineRegex.Matches(decoded))
                    {
                        var line = NormalizeLyricText(match.Groups["text"].Value);
                        if (!IsPlausibleLyricLine(line))
                            continue;

                        result.Add(new CurrentLineCandidate(process.Id, readAddress + (ulong)match.Index * 2UL));
                        if (result.Count >= MaxCachedCandidates)
                            return;
                    }
                }
            }

            if (regionSize - offset <= (ulong)bytesToRead)
                break;

            offset += (ulong)bytesToRead;
        }
    }

    private string TryReadCachedCandidates()
    {
        foreach (var candidate in candidates.ToList())
        {
            var process = TryGetProcess(candidate.ProcessId);
            if (process is null)
            {
                candidates.Remove(candidate);
                continue;
            }

            var line = TryReadCandidate(process, candidate);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            return line;
        }

        return string.Empty;
    }

    private static string TryReadCandidate(Process process, CurrentLineCandidate candidate)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
        if (handle == nint.Zero)
            return string.Empty;

        try
        {
            var buffer = new byte[ReadAroundBytes];
            if (!ReadProcessMemory(handle, ToIntPtr(candidate.Address), buffer, (nuint)buffer.Length, out var bytesRead) ||
                bytesRead < (nuint)StyledMarker.Length)
                return string.Empty;

            var decoded = Encoding.Unicode.GetString(buffer, 0, (int)bytesRead);
            var styledMatch = StyledLineRegex.Match(decoded);
            if (styledMatch.Success)
                return NormalizeLyricText(styledMatch.Groups["text"].Value);

            return NormalizeLyricText(ReadUntilTerminator(decoded));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string ReadUntilTerminator(string decoded)
    {
        var builder = new StringBuilder();
        foreach (var character in decoded)
        {
            if (character == '\0' || char.IsControl(character) && character is not '\t')
                break;

            builder.Append(character);
            if (builder.Length >= 128)
                break;
        }

        return builder.ToString();
    }

    private static IEnumerable<Process> GetCloudMusicProcesses()
    {
        var desktopLyricsProcess = CloudMusicLyricsReader.GetCloudMusicLyricsProcess();
        return Process.GetProcessesByName("cloudmusic")
            .OrderByDescending(process => desktopLyricsProcess is not null && process.Id == desktopLyricsProcess.Id)
            .ThenByDescending(process => string.Equals(process.MainWindowTitle, "桌面歌词", StringComparison.Ordinal))
            .ThenByDescending(process => !string.IsNullOrWhiteSpace(process.MainWindowTitle))
            .ThenBy(process => process.Id);
    }

    private static Process? TryGetProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeLyricText(string text)
    {
        var decoded = WebUtility.HtmlDecode(text);
        var withoutTags = HtmlTagRegex.Replace(decoded, string.Empty);
        return new string(withoutTags.Where(character => !char.IsControl(character)).ToArray()).Trim();
    }

    private static bool IsPlausibleLyricLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 96)
            return false;

        return text.Any(character => char.IsLetterOrDigit(character) || IsCjkCharacter(character));
    }

    private static bool IsCjkCharacter(char character)
    {
        return character is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\u3040' and <= '\u30FF'
            or >= '\uF900' and <= '\uFAFF';
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

    private static bool IsReadable(MemoryBasicInformation memoryInfo)
    {
        return memoryInfo.State == MemCommit &&
            (memoryInfo.Protect & PageNoAccess) == 0 &&
            (memoryInfo.Protect & PageGuard) == 0;
    }

    private static string NormalizeWindowTitle(string title)
    {
        return string.IsNullOrWhiteSpace(title) ? "<empty>" : title;
    }

    private static nint ToIntPtr(ulong value) => unchecked((nint)(long)value);

    private static ulong ToUInt64(nint value) => unchecked((ulong)value.ToInt64());

    private sealed record CurrentLineCandidate(int ProcessId, ulong Address);

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

public sealed record NetEaseCloudMusicCurrentLineReadResult(
    bool Success,
    string Line,
    string Diagnostics,
    string? ErrorMessage)
{
    public static NetEaseCloudMusicCurrentLineReadResult Succeeded(string line, string diagnostics)
    {
        return new NetEaseCloudMusicCurrentLineReadResult(true, line, diagnostics, null);
    }

    public static NetEaseCloudMusicCurrentLineReadResult Failed(string errorMessage)
    {
        return new NetEaseCloudMusicCurrentLineReadResult(false, string.Empty, errorMessage, errorMessage);
    }
}
