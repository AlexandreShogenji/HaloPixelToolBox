using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public class LyricsTimelineSyncService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ExternalSyncInterval = TimeSpan.FromMilliseconds(300);
    private readonly HaloPixelDisplayService displayService;
    private readonly object runLock = new();
    private CancellationTokenSource? cancellationTokenSource;
    private Task? runTask;

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<SubtitleCue>? CueSent;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<LyricsPlaybackSnapshot>? ExternalTrackChanged;

    public bool IsRunning
    {
        get
        {
            lock (runLock)
                return cancellationTokenSource is not null && !cancellationTokenSource.IsCancellationRequested;
        }
    }

    public LyricsTimelineSyncService(HaloPixelDisplayService displayService)
    {
        this.displayService = displayService;
    }

    public void Start(LyricsTrack track, TimeSpan startPosition, TimeSpan offset, Func<SubtitleCue, DisplayTextOptions> optionsFactory)
    {
        StartRun(token => PlayAsync(track, startPosition, offset, optionsFactory, token));
    }

    public void StartExternal(
        LyricsTrack track,
        Func<CancellationToken, Task<LyricsPlaybackSnapshot>> snapshotFactory,
        Func<LyricsPlaybackSnapshot, bool> isExpectedTrack,
        TimeSpan offset,
        Func<SubtitleCue, DisplayTextOptions> optionsFactory)
    {
        StartRun(token => PlayExternalAsync(track, snapshotFactory, isExpectedTrack, offset, optionsFactory, token));
    }

    public void Stop()
    {
        var (source, task) = DetachRun();
        if (source is null)
            return;

        source.Cancel();
        _ = DisposeAfterCompletionAsync(source, task);
        StatusChanged?.Invoke(this, "歌词同步已停止");
    }

    public async Task StopAsync()
    {
        var (source, task) = DetachRun();
        if (source is null)
            return;

        source.Cancel();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // The playback loop reports its own failures. Stop still releases the session.
            }
        }

        source.Dispose();
        StatusChanged?.Invoke(this, "歌词同步已停止");
    }

    private void StartRun(Func<CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Stop();

        var source = new CancellationTokenSource();
        lock (runLock)
        {
            cancellationTokenSource = source;
            runTask = Task.Run(() => RunTrackedAsync(source, run));
        }
    }

    private async Task RunTrackedAsync(CancellationTokenSource source, Func<CancellationToken, Task> run)
    {
        try
        {
            await run(source.Token);
        }
        finally
        {
            var shouldDispose = false;
            lock (runLock)
            {
                if (ReferenceEquals(cancellationTokenSource, source))
                {
                    cancellationTokenSource = null;
                    runTask = null;
                    shouldDispose = true;
                }
            }

            if (shouldDispose)
                source.Dispose();
        }
    }

    private (CancellationTokenSource? Source, Task? Task) DetachRun()
    {
        lock (runLock)
        {
            var source = cancellationTokenSource;
            var task = runTask;
            cancellationTokenSource = null;
            runTask = null;
            return (source, task);
        }
    }

    private static async Task DisposeAfterCompletionAsync(CancellationTokenSource source, Task? task)
    {
        try
        {
            if (task is not null)
                await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The playback loop reports its own failures. Stop must still complete and release resources.
        }
        finally
        {
            source.Dispose();
        }
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
