using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Utilities;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public class NetEaseCloudMusicLiveLineProvider : ILyricsProvider, ILyricsLiveLineProvider
{
    private static readonly TimeSpan FastReaderRefreshInterval = TimeSpan.FromMilliseconds(500);
    private readonly CloudMusicLyricsReader reader = new();
    private readonly NetEaseCloudMusicLyricsMemoryScanner memoryScanner = new();
    private readonly NetEaseCloudMusicCurrentLineMemoryReader currentLineReader = new();
    private readonly LrcLyricsParser parser = new();
    private bool initialized;
    private DateTimeOffset lastFastReaderRefreshAt;

    public LyricsProviderKind ProviderKind => LyricsProviderKind.NetEaseCloudMusic;

    public Task<LyricsTrack?> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        NetEaseCloudMusicLyricsScanResult scanResult;
        try
        {
            scanResult = memoryScanner.Scan(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"网易云内存 LRC 扫描异常：{ex.Message}", ex);
        }

        if (scanResult.Success)
        {
            LyricsTrack track;
            try
            {
                track = parser.Parse(scanResult.LyricsContent, scanResult.SourceName);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"网易云内存 LRC 已找到，但解析失败：{ex.Message}", ex);
            }

            if (track.Lines.Count == 0)
                throw new InvalidOperationException("网易云内存 LRC 已找到，但没有解析出有效歌词行");

            track.Provider = ProviderKind;
            track.SourceName = string.IsNullOrWhiteSpace(scanResult.Diagnostics)
                ? scanResult.SourceName
                : $"{scanResult.SourceName}；{scanResult.Diagnostics}";
            track.RawSource = scanResult.LyricsContent;
            track.IsSynced = true;
            track.Confidence = 0.95;
            ApplyTrackWindowTitle(track, scanResult.TrackWindowTitle);
            ApplyCurrentPosition(track, scanResult.CurrentLine);
            return Task.FromResult<LyricsTrack?>(track);
        }

        throw new InvalidOperationException($"网易云内存 LRC 扫描失败：{scanResult.ErrorMessage}");
    }

    public Task<LyricsTrack?> ReadCurrentLineAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RefreshFastReaderAddress();
        if (initialized && reader.TryReadLyrics(out var lyrics) && !string.IsNullOrWhiteSpace(lyrics))
            return Task.FromResult<LyricsTrack?>(BuildSingleLineTrack(lyrics.Trim(), BuildSourceName()));

        var currentLineResult = currentLineReader.ReadCurrentLine(cancellationToken);
        if (currentLineResult.Success && !string.IsNullOrWhiteSpace(currentLineResult.Line))
            return Task.FromResult<LyricsTrack?>(BuildSingleLineTrack(
                currentLineResult.Line,
                $"网易云音乐当前行；{currentLineResult.Diagnostics}"));

        throw new InvalidOperationException($"网易云实时当前行定位失败：{currentLineResult.ErrorMessage}；旧版指针读取也不可用：{BuildInitializationFailureMessage()}");
    }

    private LyricsTrack BuildSingleLineTrack(string lyrics, string sourceName)
    {
        return new LyricsTrack
        {
            Provider = ProviderKind,
            SourceName = sourceName,
            Title = "网易云音乐桌面歌词",
            Artist = "当前播放",
            IsSynced = false,
            Confidence = 1,
            Lines =
            {
                new SubtitleCue
                {
                    Start = TimeSpan.Zero,
                    End = TimeSpan.FromSeconds(3),
                    Text = lyrics
                }
            }
        };
    }

    private static void ApplyTrackWindowTitle(LyricsTrack track, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle) ||
            !windowTitle.Contains(" - ", StringComparison.Ordinal))
            return;

        var parts = windowTitle.Split(" - ", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return;

        if (string.IsNullOrWhiteSpace(track.Title))
            track.Title = parts[0];
        if (string.IsNullOrWhiteSpace(track.Artist))
            track.Artist = parts[1];
    }

    private static void ApplyCurrentPosition(LyricsTrack track, string? currentLine)
    {
        if (string.IsNullOrWhiteSpace(currentLine))
            return;

        var matchedCue = track.Lines.FirstOrDefault(line =>
            string.Equals(NormalizeLineText(line.Text), NormalizeLineText(currentLine), StringComparison.Ordinal));
        if (matchedCue is not null)
            track.CurrentPosition = matchedCue.Start;
    }

    private static string NormalizeLineText(string text)
    {
        return new string(text.Where(character => !char.IsControl(character) && !char.IsWhiteSpace(character)).ToArray());
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = reader.Initialize();
        lastFastReaderRefreshAt = initialized ? DateTimeOffset.Now : default;
    }

    private void RefreshFastReaderAddress()
    {
        EnsureInitialized();
        if (!initialized || reader.UseInputedAddress)
            return;

        var now = DateTimeOffset.Now;
        if (now - lastFastReaderRefreshAt < FastReaderRefreshInterval)
            return;

        if (reader.ReresolveAddress())
        {
            lastFastReaderRefreshAt = now;
            return;
        }

        initialized = false;
        lastFastReaderRefreshAt = default;
        currentLineReader.Reset();
        EnsureInitialized();
    }

    private string BuildSourceName()
    {
        var version = reader.Version.Major == 0 ? "unknown" : reader.Version.ToString(3);
        return $"网易云音乐桌面歌词 {version} addr=0x{reader.Address:X}";
    }

    private static string BuildInitializationFailureMessage()
    {
        var process = CloudMusicLyricsReader.GetCloudMusicLyricsProcess();
        if (process is null)
            return "未检测到网易云音乐桌面歌词窗口，请先打开网易云音乐并启用桌面歌词";

        var version = "unknown";
        try
        {
            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(process.MainModule?.FileName ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(versionInfo.FileVersion) &&
                Version.TryParse(versionInfo.FileVersion, out var parsedVersion))
                version = parsedVersion.ToString(3);
        }
        catch
        {
        }

        var supportedVersions = string.Join(", ", CloudMusicLyricsReader.VersionResolverDictionary.Keys.OrderBy(key => key));
        return $"检测到网易云音乐版本 {version}，但当前未适配该版本的桌面歌词内存地址；已适配版本：{supportedVersions}";
    }
}
