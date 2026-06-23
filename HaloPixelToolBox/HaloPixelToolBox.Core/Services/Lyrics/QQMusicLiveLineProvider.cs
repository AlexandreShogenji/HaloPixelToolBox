using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

/// <summary>
/// QQ 音乐桌面歌词 Provider：从内存重建 QRC 时间轴，再按系统媒体会话的进度输出当前行。
/// </summary>
public sealed class QQMusicLiveLineProvider : ILyricsProvider, ILyricsLiveLineProvider
{
    private static readonly TimeSpan TrackReloadRetryInterval = TimeSpan.FromMilliseconds(750);
    private readonly QQMusicMediaSessionPlaybackProvider playbackProvider;
    private readonly QQMusicLocalQrcCacheReader localQrcCacheReader = new();
    private readonly QQMusicLyricsMemoryScanner memoryScanner = new();
    private LyricsTrack? currentTrack;
    private string currentTrackKey = string.Empty;
    private DateTimeOffset nextTrackReloadAttemptAt;

    public QQMusicLiveLineProvider(QQMusicMediaSessionPlaybackProvider playbackProvider)
    {
        this.playbackProvider = playbackProvider;
    }

    public LyricsProviderKind ProviderKind => LyricsProviderKind.QQMusic;

    public async Task<LyricsTrack?> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        var snapshot = await playbackProvider.GetSnapshotAsync(cancellationToken);
        if (!snapshot.HasTrack)
            throw new InvalidOperationException("未检测到 QQ 音乐当前播放歌曲，请打开 QQ 音乐并开始播放");

        return LoadTrack(snapshot, cancellationToken);
    }

    public async Task<LyricsTrack?> ReadCurrentLineAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await playbackProvider.GetSnapshotAsync(cancellationToken);
        if (!snapshot.HasTrack)
            return null;

        var snapshotTrackKey = BuildTrackKey(snapshot);
        if (currentTrack is null || !snapshotTrackKey.Equals(currentTrackKey, StringComparison.OrdinalIgnoreCase))
        {
            // QQ 音乐切歌时，媒体会话通常会先于解密后的 QRC 写入进程内存更新。
            // 这不是同步失败；保持循环并在短暂的冷却后自动重试即可。
            if (DateTimeOffset.UtcNow >= nextTrackReloadAttemptAt)
            {
                try
                {
                    LoadTrack(snapshot, cancellationToken);
                    nextTrackReloadAttemptAt = default;
                }
                catch (InvalidOperationException)
                {
                    nextTrackReloadAttemptAt = DateTimeOffset.UtcNow + TrackReloadRetryInterval;
                }
            }

            if (currentTrack is null || !snapshotTrackKey.Equals(currentTrackKey, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        var position = snapshot.Position ?? currentTrack.CurrentPosition ?? TimeSpan.Zero;
        var cue = GetCurrentCue(currentTrack, position) ?? GetNearestPreviousCue(currentTrack, position);
        return cue is null ? null : BuildSingleLineTrack(cue.Text, currentTrack.Title, currentTrack.Artist);
    }

    private LyricsTrack LoadTrack(LyricsPlaybackSnapshot snapshot, CancellationToken cancellationToken)
    {
        // QQMusicLyricNew 是 QQ 客户端自己的落盘缓存：它有完整、未被 UI 渲染拆分的 QRC。
        // 只有客户端尚未落盘时才回退到内存扫描。
        var result = localQrcCacheReader.Read(snapshot.Title, snapshot.Artist);
        if (!result.Success)
            result = memoryScanner.Scan(snapshot.Title, snapshot.Artist, cancellationToken);
        if (!result.Success || result.Lines.Count == 0)
            throw new InvalidOperationException($"QQ 音乐 QRC 歌词扫描失败：{result.ErrorMessage}");

        currentTrack = new LyricsTrack
        {
            Provider = ProviderKind,
            SourceName = string.IsNullOrWhiteSpace(result.Diagnostics)
                ? result.SourceName
                : $"{result.SourceName}；{result.Diagnostics}",
            Title = string.IsNullOrWhiteSpace(snapshot.Title) ? result.Title : snapshot.Title,
            Artist = string.IsNullOrWhiteSpace(snapshot.Artist) ? result.Artist : snapshot.Artist,
            Album = snapshot.Album,
            Duration = snapshot.Duration ?? result.Lines.Last().End,
            CurrentPosition = snapshot.Position,
            IsSynced = true,
            Confidence = 0.95,
            RawSource = "QQ 音乐本地 QRC 缓存",
            Lines = result.Lines.ToList()
        };
        currentTrackKey = BuildTrackKey(snapshot);
        return currentTrack;
    }

    private LyricsTrack BuildSingleLineTrack(string text, string title, string artist)
    {
        return new LyricsTrack
        {
            Provider = ProviderKind,
            SourceName = "QQ 音乐桌面歌词",
            Title = title,
            Artist = artist,
            IsSynced = false,
            Confidence = 1,
            Lines =
            {
                new SubtitleCue
                {
                    Start = TimeSpan.Zero,
                    End = TimeSpan.FromSeconds(3),
                    Text = text
                }
            }
        };
    }

    private static SubtitleCue? GetCurrentCue(LyricsTrack track, TimeSpan position)
    {
        return track.Lines.LastOrDefault(cue => cue.Start <= position && cue.End > position);
    }

    private static SubtitleCue? GetNearestPreviousCue(LyricsTrack track, TimeSpan position)
    {
        return track.Lines.LastOrDefault(cue => cue.Start <= position);
    }

    private static string BuildTrackKey(LyricsPlaybackSnapshot snapshot)
    {
        return $"{snapshot.Title}\u001F{snapshot.Artist}\u001F{snapshot.Album}".Trim();
    }
}
