using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public class LyricsTimelineSyncService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ExternalSyncInterval = TimeSpan.FromMilliseconds(300);
    private readonly HaloPixelDisplayService displayService;
    private CancellationTokenSource? cancellationTokenSource;

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<SubtitleCue>? CueSent;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<LyricsPlaybackSnapshot>? ExternalTrackChanged;

    public bool IsRunning => cancellationTokenSource is not null && !cancellationTokenSource.IsCancellationRequested;

    public LyricsTimelineSyncService(HaloPixelDisplayService displayService)
    {
        this.displayService = displayService;
    }

    public void Start(LyricsTrack track, TimeSpan startPosition, TimeSpan offset, Func<SubtitleCue, DisplayTextOptions> optionsFactory)
    {
        Stop();
        cancellationTokenSource = new CancellationTokenSource();
        _ = PlayAsync(track, startPosition, offset, optionsFactory, cancellationTokenSource.Token);
    }

    public void StartExternal(
        LyricsTrack track,
        Func<CancellationToken, Task<LyricsPlaybackSnapshot>> snapshotFactory,
        Func<LyricsPlaybackSnapshot, bool> isExpectedTrack,
        TimeSpan offset,
        Func<SubtitleCue, DisplayTextOptions> optionsFactory)
    {
        Stop();
        cancellationTokenSource = new CancellationTokenSource();
        _ = PlayExternalAsync(track, snapshotFactory, isExpectedTrack, offset, optionsFactory, cancellationTokenSource.Token);
    }

    public void Stop()
    {
        if (cancellationTokenSource is null)
            return;

        cancellationTokenSource.Cancel();
        cancellationTokenSource.Dispose();
        cancellationTokenSource = null;
        StatusChanged?.Invoke(this, "歌词同步已停止");
    }

    private async Task PlayAsync(LyricsTrack track, TimeSpan startPosition, TimeSpan offset, Func<SubtitleCue, DisplayTextOptions> optionsFactory, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now - startPosition;
        var duration = ResolveDuration(track);
        var lastCueKey = string.Empty;
        StatusChanged?.Invoke(this, $"歌词同步已启动：{track.Title}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var position = DateTimeOffset.Now - startedAt;
                if (position > duration)
                {
                    StatusChanged?.Invoke(this, "歌词同步已到达末尾");
                    Stop();
                    return;
                }

                PositionChanged?.Invoke(this, position);
                var cue = GetCurrentCue(track, position, offset);
                if (cue is not null)
                {
                    var options = optionsFactory(cue);
                    var cueKey = BuildCueKey(cue, options);
                    if (cueKey != lastCueKey)
                    {
                        lastCueKey = cueKey;
                        await displayService.SendSubtitleCueAsync(cue, options, cancellationToken);
                        CueSent?.Invoke(this, cue);
                    }
                }

                await Task.Delay(SyncInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"歌词同步失败：{ex.Message}");
            Stop();
        }
    }

    private async Task PlayExternalAsync(
        LyricsTrack track,
        Func<CancellationToken, Task<LyricsPlaybackSnapshot>> snapshotFactory,
        Func<LyricsPlaybackSnapshot, bool> isExpectedTrack,
        TimeSpan offset,
        Func<SubtitleCue, DisplayTextOptions> optionsFactory,
        CancellationToken cancellationToken)
    {
        var lastCueKey = string.Empty;
        TimeSpan? lastPosition = null;
        StatusChanged?.Invoke(this, $"歌词同步已启动：{track.Title}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = await snapshotFactory(cancellationToken);
                if (!snapshot.HasTrack)
                {
                    StatusChanged?.Invoke(this, "未检测到 Spotify 当前播放歌曲");
                    await Task.Delay(ExternalSyncInterval, cancellationToken);
                    continue;
                }

                if (!isExpectedTrack(snapshot))
                {
                    StatusChanged?.Invoke(this, "检测到 Spotify 已切歌，正在自动重新加载歌词");
                    Stop();
                    ExternalTrackChanged?.Invoke(this, snapshot);
                    return;
                }

                if (snapshot.Position is not { } position)
                {
                    StatusChanged?.Invoke(this, "Spotify 播放进度暂不可用");
                    await Task.Delay(ExternalSyncInterval, cancellationToken);
                    continue;
                }

                PositionChanged?.Invoke(this, position);
                if (snapshot.IsPaused)
                {
                    StatusChanged?.Invoke(this, $"Spotify 已暂停：{FormatTime(position)}");
                    await Task.Delay(ExternalSyncInterval, cancellationToken);
                    continue;
                }

                if (lastPosition is { } previousPosition && position + TimeSpan.FromSeconds(1.5) < previousPosition)
                {
                    lastCueKey = string.Empty;
                    StatusChanged?.Invoke(this, "检测到 Spotify 播放进度回退，歌词同步游标已重置");
                }

                lastPosition = position;
                var cue = GetCurrentCue(track, position, offset);
                if (cue is not null)
                {
                    var options = optionsFactory(cue);
                    var cueKey = BuildCueKey(cue, options);
                    if (cueKey != lastCueKey)
                    {
                        lastCueKey = cueKey;
                        await displayService.SendSubtitleCueAsync(cue, options, cancellationToken);
                        CueSent?.Invoke(this, cue);
                    }
                }

                await Task.Delay(ExternalSyncInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"歌词同步失败：{ex.Message}");
            Stop();
        }
    }

    private static SubtitleCue? GetCurrentCue(LyricsTrack track, TimeSpan position, TimeSpan offset)
    {
        return track.Lines.FirstOrDefault(line =>
        {
            var start = ClampToZero(line.Start + offset);
            var end = ClampToZero(line.End + offset);
            return start <= position && end >= position;
        });
    }

    private static TimeSpan ResolveDuration(LyricsTrack track)
    {
        var duration = track.Duration ?? track.Lines.LastOrDefault()?.End ?? TimeSpan.Zero;
        return duration + TimeSpan.FromSeconds(1);
    }

    private static string BuildCueKey(SubtitleCue cue, DisplayTextOptions options)
    {
        return $"{cue.Start.Ticks}:{cue.End.Ticks}:{options.ScrollDirection}:{cue.Text}";
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString(@"hh\:mm\:ss")
            : time.ToString(@"mm\:ss");
    }

    private static TimeSpan ClampToZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
