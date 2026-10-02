using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.DeviceControl;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.DeviceControl;
using HaloPixelToolBox.Core.Services.Lyrics;
using HaloPixelToolBox.Core.Services.Scenes;

namespace HaloPixelToolBox.Services;

/// <summary>
/// Owns the process-wide lyrics session used by both the UI and external control adapters.
/// </summary>
public sealed class LyricsSubtitleControlService : IDisposable
{
    private const int MinimumOffsetMilliseconds = -30_000;
    private const int MaximumOffsetMilliseconds = 30_000;
    private const double MinimumAutomaticMatchConfidence = 0.65;

    private readonly object stateLock = new();
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private readonly SpotifyMediaSessionPlaybackProvider spotifyPlaybackProvider = new();
    private readonly SpotifyLyricsProvider spotifyLyricsProvider;
    private readonly HaloPixelDisplayService displayService = new();
    private readonly PersonalSceneRestoreService restoreService = new();
    private LyricsTimelineSyncService? timelineSyncService;

    private CancellationTokenSource? preparationCancellationTokenSource;
    private long operationEpoch;
    private long generation;
    private bool desiredRunning;
    private bool disposed;
    private int reloadInProgress;
    private int offsetMilliseconds;
    private bool scrollEnabled = true;
    private string localLyricsFilePath = string.Empty;
    private LyricsTrack? currentTrack;
    private string currentTrackKey = string.Empty;
    private LyricsSubtitleSessionStatus status = LyricsSubtitleSessionStatus.Stopped();

    public LyricsSubtitleControlService()
    {
        spotifyLyricsProvider = new SpotifyLyricsProvider(spotifyPlaybackProvider);
    }

    public event EventHandler<LyricsSubtitleSessionStatus>? StatusChanged;

    public LyricsSubtitleSessionStatus CurrentStatus
    {
        get
        {
            lock (stateLock)
                return status with { ObservedAt = DateTimeOffset.Now };
        }
    }

    public async Task<DeviceCommandResult<LyricsSubtitleSessionStatus>> StartSpotifyAsync(
        int requestedOffsetMilliseconds = 0,
        bool enableScroll = true,
        string? localLrcPath = null,
        LyricsTrack? preparedTrack = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (requestedOffsetMilliseconds is < MinimumOffsetMilliseconds or > MaximumOffsetMilliseconds)
        {
            return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                $"歌词偏移必须在 {MinimumOffsetMilliseconds} 到 {MaximumOffsetMilliseconds} 毫秒之间");
        }

        long startOperationEpoch;
        lock (stateLock)
            startOperationEpoch = ++operationEpoch;

        LyricsPlaybackSnapshot snapshot;
        try
        {
            snapshot = await spotifyPlaybackProvider.GetSnapshotAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                DeviceCommandStatus.Cancelled,
                "Spotify 歌词启动已取消");
        }

        if (!snapshot.HasTrack)
        {
            var unavailable = UpdateStatusIfOperationEpoch(startOperationEpoch, current => current with
            {
                State = LyricsSubtitleSessionState.WaitingForSpotify,
                IsRunning = false,
                Provider = "spotify",
                Message = "未检测到 Spotify 当前播放歌曲，请先打开 Spotify 并播放音乐"
            });
            if (unavailable is null)
            {
                return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                    DeviceCommandStatus.Cancelled,
                    "Spotify 歌词启动已被新的控制请求取代");
            }

            return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                DeviceCommandStatus.NotFound,
                unavailable.Message);
        }

        var snapshotKey = BuildTrackKey(snapshot);
        LyricsSubtitleSessionStatus current;
        lock (stateLock)
        {
            if (operationEpoch != startOperationEpoch)
            {
                return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                    DeviceCommandStatus.Cancelled,
                    "Spotify 歌词启动已被新的控制请求取代");
            }

            current = status with { ObservedAt = DateTimeOffset.Now };
            if (desiredRunning
                && current.State is LyricsSubtitleSessionState.Preparing or LyricsSubtitleSessionState.Running or LyricsSubtitleSessionState.Paused
                && currentTrackKey.Equals(snapshotKey, StringComparison.OrdinalIgnoreCase)
                && offsetMilliseconds == requestedOffsetMilliseconds
                && scrollEnabled == enableScroll)
            {
                return DeviceCommandResult<LyricsSubtitleSessionStatus>.Succeeded(
                    "Spotify 歌词已在使用相同设置运行",
                    current);
            }
        }

        var started = await BeginSpotifySessionAsync(
            snapshot,
            requestedOffsetMilliseconds,
            enableScroll,
            localLrcPath,
            preparedTrack,
            cancellationToken,
            expectedOperationEpoch: startOperationEpoch);
        if (!started)
        {
            return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                DeviceCommandStatus.Cancelled,
                "Spotify 歌词启动已被停止请求取消");
        }

        return DeviceCommandResult<LyricsSubtitleSessionStatus>.Succeeded(
            $"已切换到 Spotify 歌词，正在匹配“{BuildTrackName(snapshot)}”",
            CurrentStatus);
    }

    public async Task<DeviceCommandResult<LyricsSubtitleSessionStatus>> SetOffsetAsync(
        int requestedOffsetMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (requestedOffsetMilliseconds is < MinimumOffsetMilliseconds or > MaximumOffsetMilliseconds)
        {
            return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                $"歌词偏移必须在 {MinimumOffsetMilliseconds} 到 {MaximumOffsetMilliseconds} 毫秒之间");
        }

        // A later offset request takes precedence over any external start that is still
        // waiting for its first Spotify snapshot.
        lock (stateLock)
            operationEpoch++;

        var gateEntered = false;
        try
        {
            await transitionGate.WaitAsync(cancellationToken);
            gateEntered = true;

            LyricsTrack capturedTrack;
            string capturedTrackKey;
            long capturedGeneration;
            LyricsTimelineSyncService capturedTimeline;
            lock (stateLock)
            {
                offsetMilliseconds = requestedOffsetMilliseconds;
                if (!desiredRunning || currentTrack is null || timelineSyncService is null)
                {
                    var saved = status with
                    {
                        OffsetMilliseconds = requestedOffsetMilliseconds,
                        Message = $"Spotify 歌词偏移已保存为 {FormatOffset(requestedOffsetMilliseconds)}",
                        ObservedAt = DateTimeOffset.Now
                    };
                    status = saved;
                    StatusChanged?.Invoke(this, saved);
                    return DeviceCommandResult<LyricsSubtitleSessionStatus>.Succeeded(saved.Message, saved);
                }

                capturedTrack = currentTrack;
                capturedTrackKey = currentTrackKey;
                capturedGeneration = generation;
                capturedTimeline = timelineSyncService;
                // Detach first so terminal callbacks from the old timeline cannot publish
                // while it is being replaced with the adjusted timeline.
                timelineSyncService = null;
            }

            await capturedTimeline.StopAsync();

            LyricsSubtitleSessionStatus updated;
            lock (stateLock)
            {
                if (!desiredRunning
                    || generation != capturedGeneration
                    || !ReferenceEquals(currentTrack, capturedTrack)
                    || !currentTrackKey.Equals(capturedTrackKey, StringComparison.OrdinalIgnoreCase)
                    || timelineSyncService is not null)
                {
                    return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                        DeviceCommandStatus.Cancelled,
                        "Spotify 歌词偏移调节已被停止或新的会话取代");
                }

                StartTimelineLocked(capturedTrack, capturedTrackKey, capturedGeneration);
                updated = status with
                {
                    State = LyricsSubtitleSessionState.Running,
                    IsRunning = true,
                    OffsetMilliseconds = requestedOffsetMilliseconds,
                    Message = $"Spotify 歌词偏移已调整为 {FormatOffset(requestedOffsetMilliseconds)}",
                    ObservedAt = DateTimeOffset.Now
                };
                status = updated;
                StatusChanged?.Invoke(this, updated);
            }
            return DeviceCommandResult<LyricsSubtitleSessionStatus>.Succeeded(updated.Message, updated);
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                DeviceCommandStatus.Cancelled,
                "Spotify 歌词偏移调节已取消");
        }
        finally
        {
            if (gateEntered)
                transitionGate.Release();
        }
    }

    public async Task<DeviceCommandResult<LyricsSubtitleSessionStatus>> StopAsync(
        bool restoreScene = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CancellationTokenSource? preparation;
        LyricsTimelineSyncService? timeline;
        bool wasActive;
        long stoppedGeneration;
        lock (stateLock)
        {
            operationEpoch++;
            wasActive = desiredRunning
                        || status.State is LyricsSubtitleSessionState.Preparing
                            or LyricsSubtitleSessionState.Running
                            or LyricsSubtitleSessionState.Paused;
            desiredRunning = false;
            generation++;
            stoppedGeneration = generation;
            preparation = preparationCancellationTokenSource;
            preparationCancellationTokenSource = null;
            timeline = timelineSyncService;
            timelineSyncService = null;
            currentTrack = null;
            currentTrackKey = string.Empty;

            var stopping = status with
            {
                State = LyricsSubtitleSessionState.Stopping,
                IsRunning = false,
                Message = "正在停止 Spotify 歌词",
                ObservedAt = DateTimeOffset.Now
            };
            status = stopping;
            StatusChanged?.Invoke(this, stopping);
        }

        // The preparation task owns disposal of its CTS. It may finish and dispose it
        // between detaching it above and this cancellation request.
        CancelPreparation(preparation);
        // Cancel display work immediately. Acquiring the transition gate can itself be
        // cancelled, but the requested stop must still take effect in that case.
        timeline?.Stop();

        var gateEntered = false;
        try
        {
            await transitionGate.WaitAsync(cancellationToken);
            gateEntered = true;

            lock (stateLock)
            {
                if (generation != stoppedGeneration || desiredRunning)
                {
                    return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                        DeviceCommandStatus.Cancelled,
                        "停止请求已被新的 Spotify 歌词会话取代");
                }
            }

            var restored = false;
            if (restoreScene && wasActive)
                restored = await restoreService.RestoreAsync(displayService, cancellationToken);

            var stopped = UpdateStatusIfGeneration(stoppedGeneration, current => current with
            {
                SessionId = null,
                State = LyricsSubtitleSessionState.Stopped,
                IsRunning = false,
                CurrentLine = null,
                PositionMilliseconds = null,
                Message = !wasActive
                    ? "Spotify 歌词当前未运行"
                    : restoreScene
                        ? restored ? "Spotify 歌词已停止，并已恢复当前个性场景" : "Spotify 歌词已停止，未能恢复个性场景"
                        : "Spotify 歌词已停止"
            });
            if (stopped is null)
            {
                return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                    DeviceCommandStatus.Cancelled,
                    "停止请求已被新的 Spotify 歌词会话取代");
            }

            return DeviceCommandResult<LyricsSubtitleSessionStatus>.Succeeded(stopped.Message, stopped);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusIfGeneration(stoppedGeneration, current => current with
            {
                SessionId = null,
                State = LyricsSubtitleSessionState.Stopped,
                IsRunning = false,
                CurrentLine = null,
                PositionMilliseconds = null,
                Message = "Spotify 歌词已停止；场景恢复已取消"
            });
            return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                DeviceCommandStatus.Cancelled,
                "Spotify 歌词已停止；场景恢复已取消");
        }
        catch (Exception exception)
        {
            var failed = UpdateStatusIfGeneration(stoppedGeneration, current => current with
            {
                SessionId = null,
                State = LyricsSubtitleSessionState.Stopped,
                IsRunning = false,
                CurrentLine = null,
                PositionMilliseconds = null,
                Message = $"Spotify 歌词已停止，但恢复场景失败：{exception.Message}"
            });
            return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                DeviceCommandStatus.Failed,
                failed?.Message ?? $"Spotify 歌词已停止，但恢复场景失败：{exception.Message}");
        }
        finally
        {
            if (gateEntered)
                transitionGate.Release();
        }
    }

    public void RequestStop()
    {
        if (disposed)
            return;

        CancellationTokenSource? preparation;
        LyricsTimelineSyncService? timeline;
        lock (stateLock)
        {
            operationEpoch++;
            desiredRunning = false;
            generation++;
            preparation = preparationCancellationTokenSource;
            preparationCancellationTokenSource = null;
            timeline = timelineSyncService;
            timelineSyncService = null;
            currentTrack = null;
            currentTrackKey = string.Empty;

            var stopped = status with
            {
                SessionId = null,
                State = LyricsSubtitleSessionState.Stopped,
                IsRunning = false,
                CurrentLine = null,
                PositionMilliseconds = null,
                Message = "Spotify 歌词已停止",
                ObservedAt = DateTimeOffset.Now
            };
            status = stopped;
            StatusChanged?.Invoke(this, stopped);
        }

        CancelPreparation(preparation);
        timeline?.Stop();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        CancellationTokenSource? preparation;
        LyricsTimelineSyncService? timeline;
        lock (stateLock)
        {
            operationEpoch++;
            desiredRunning = false;
            generation++;
            preparation = preparationCancellationTokenSource;
            preparationCancellationTokenSource = null;
            timeline = timelineSyncService;
            timelineSyncService = null;
            currentTrack = null;
            currentTrackKey = string.Empty;
        }
        CancelPreparation(preparation);
        timeline?.Stop();
        transitionGate.Dispose();
    }

    private async Task<bool> BeginSpotifySessionAsync(
        LyricsPlaybackSnapshot snapshot,
        int requestedOffsetMilliseconds,
        bool enableScroll,
        string? localLrcPath,
        LyricsTrack? preparedTrack,
        CancellationToken cancellationToken,
        long? expectedGeneration = null,
        long? expectedOperationEpoch = null)
    {
        CancellationTokenSource? previousPreparation;
        LyricsTimelineSyncService? previousTimeline;
        long sessionGeneration;
        lock (stateLock)
        {
            if (expectedOperationEpoch.HasValue
                && operationEpoch != expectedOperationEpoch.Value)
            {
                return false;
            }

            if (expectedGeneration.HasValue
                && (!desiredRunning || generation != expectedGeneration.Value))
            {
                return false;
            }

            desiredRunning = true;
            generation++;
            sessionGeneration = generation;
            previousPreparation = preparationCancellationTokenSource;
            preparationCancellationTokenSource = null;
            previousTimeline = timelineSyncService;
            timelineSyncService = null;
        }
        CancelPreparation(previousPreparation);
        previousTimeline?.Stop();

        var gateEntered = false;
        try
        {
            await transitionGate.WaitAsync(cancellationToken);
            gateEntered = true;
            cancellationToken.ThrowIfCancellationRequested();

            var source = new CancellationTokenSource();
            lock (stateLock)
            {
                if (!desiredRunning || generation != sessionGeneration)
                {
                    source.Dispose();
                    return false;
                }

                preparationCancellationTokenSource = source;
                offsetMilliseconds = requestedOffsetMilliseconds;
                scrollEnabled = enableScroll;
                localLyricsFilePath = localLrcPath?.Trim() ?? string.Empty;
                currentTrack = null;
                currentTrackKey = BuildTrackKey(snapshot);
            }

            var preparing = UpdateStatusIfGeneration(sessionGeneration, _ => new LyricsSubtitleSessionStatus(
                Guid.NewGuid().ToString("N"),
                LyricsSubtitleSessionState.Preparing,
                true,
                "spotify",
                snapshot.Title,
                snapshot.Artist,
                null,
                0,
                null,
                snapshot.Position is null ? null : Math.Max(0, (long)Math.Round(snapshot.Position.Value.TotalMilliseconds)),
                requestedOffsetMilliseconds,
                enableScroll,
                $"正在匹配 Spotify 歌词：{BuildTrackName(snapshot)}",
                DateTimeOffset.Now));
            if (preparing is null)
            {
                lock (stateLock)
                {
                    if (ReferenceEquals(preparationCancellationTokenSource, source))
                        preparationCancellationTokenSource = null;
                }
                source.Cancel();
                source.Dispose();
                return false;
            }

            _ = PrepareAndStartSpotifyAsync(sessionGeneration, snapshot, preparedTrack, source);
            return true;
        }
        catch (OperationCanceledException)
        {
            lock (stateLock)
            {
                if (generation == sessionGeneration)
                {
                    desiredRunning = false;
                    var cancelled = status with
                    {
                        State = LyricsSubtitleSessionState.Stopped,
                        IsRunning = false,
                        Message = "Spotify 歌词启动已取消",
                        ObservedAt = DateTimeOffset.Now
                    };
                    status = cancelled;
                    StatusChanged?.Invoke(this, cancelled);
                }
            }
            return false;
        }
        finally
        {
            if (gateEntered)
                transitionGate.Release();
        }
    }

    private async Task PrepareAndStartSpotifyAsync(
        long sessionGeneration,
        LyricsPlaybackSnapshot snapshot,
        LyricsTrack? preparedTrack,
        CancellationTokenSource source)
    {
        try
        {
            var cancellationToken = source.Token;
            var track = IsPreparedTrackUsable(preparedTrack, snapshot)
                ? preparedTrack
                : await spotifyLyricsProvider.SearchAsync(new LyricsQuery
                {
                    Provider = LyricsProviderKind.Spotify,
                    Keyword = BuildTrackName(snapshot),
                    Title = snapshot.Title,
                    Artist = snapshot.Artist,
                    Album = snapshot.Album,
                    Duration = snapshot.Duration,
                    FilePath = string.IsNullOrWhiteSpace(localLyricsFilePath) ? null : localLyricsFilePath,
                    PreferSyncedLyrics = true
                }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (track is null || track.Lines.Count == 0)
            {
                CompletePreparation(source, sessionGeneration, current => current with
                {
                    State = LyricsSubtitleSessionState.NoLyrics,
                    IsRunning = false,
                    Message = $"没有匹配到 Spotify 当前歌曲歌词：{BuildTrackName(snapshot)}"
                });
                return;
            }

            if (!track.IsSynced || track.Confidence < MinimumAutomaticMatchConfidence)
            {
                CompletePreparation(source, sessionGeneration, current => current with
                {
                    State = LyricsSubtitleSessionState.NoLyrics,
                    IsRunning = false,
                    LyricsSource = track.SourceName,
                    LineCount = track.Lines.Count,
                    Message = $"找到的歌词匹配度不足，已阻止自动显示：{track.SourceName}（{track.Confidence:P0}）"
                });
                return;
            }

            var deviceReady = await HaloPixelDeviceOperationQueue.RunAsync(
                () => displayService.EnsureDeviceReady(),
                cancellationToken);
            if (!deviceReady)
            {
                CompletePreparation(source, sessionGeneration, current => current with
                {
                    State = LyricsSubtitleSessionState.Failed,
                    IsRunning = false,
                    Message = "PixelBar 未连接，Spotify 歌词未启动"
                });
                return;
            }

            await transitionGate.WaitAsync(cancellationToken);
            try
            {
                LyricsSubtitleSessionStatus started;
                lock (stateLock)
                {
                    if (!desiredRunning || generation != sessionGeneration || source.IsCancellationRequested)
                        return;

                    currentTrack = track;
                    currentTrackKey = BuildTrackKey(snapshot);
                    if (ReferenceEquals(preparationCancellationTokenSource, source))
                        preparationCancellationTokenSource = null;

                    // RequestStop uses the same lock before detaching the timeline. Keeping the
                    // final validation, generation-scoped timeline start and status publication
                    // together prevents a stop from leaving a revived background loop or stale UI.
                    StartTimelineLocked(track, currentTrackKey, sessionGeneration);
                    started = status with
                    {
                        State = snapshot.IsPaused ? LyricsSubtitleSessionState.Paused : LyricsSubtitleSessionState.Running,
                        IsRunning = true,
                        Track = string.IsNullOrWhiteSpace(track.Title) ? snapshot.Title : track.Title,
                        Artist = string.IsNullOrWhiteSpace(track.Artist) ? snapshot.Artist : track.Artist,
                        LyricsSource = track.SourceName,
                        LineCount = track.Lines.Count,
                        Message = $"Spotify 歌词同步已启动：{BuildTrackName(snapshot)}（{track.SourceName}）",
                        ObservedAt = DateTimeOffset.Now
                    };
                    status = started;
                    StatusChanged?.Invoke(this, started);
                }
            }
            finally
            {
                transitionGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CompletePreparation(source, sessionGeneration, current => current with
            {
                State = LyricsSubtitleSessionState.Failed,
                IsRunning = false,
                Message = $"Spotify 歌词启动失败：{exception.Message}"
            });
        }
        finally
        {
            lock (stateLock)
            {
                if (ReferenceEquals(preparationCancellationTokenSource, source))
                    preparationCancellationTokenSource = null;
            }
            source.Dispose();
        }
    }

    private static void CancelPreparation(CancellationTokenSource? source)
    {
        if (source is null)
            return;

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The detached preparation task completed and disposed its own source.
        }
    }

    private void StartTimelineLocked(
        LyricsTrack track,
        string expectedTrackKey,
        long sessionGeneration)
    {
        var timeline = CreateTimelineService(sessionGeneration);
        timelineSyncService = timeline;
        timeline.StartExternal(
            track,
            spotifyPlaybackProvider.GetSnapshotAsync,
            snapshot => BuildTrackKey(snapshot).Equals(expectedTrackKey, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromMilliseconds(offsetMilliseconds),
            cue => new DisplayTextOptions
            {
                Source = DisplayContentKind.Lyrics,
                Layout = HaloPixelTextLayout.Center,
                ScrollDirection = scrollEnabled && ShouldScroll(cue.Text)
                    ? TextScrollDirection.RightToLeft
                    : TextScrollDirection.None
            });
    }

    private LyricsTimelineSyncService CreateTimelineService(long sessionGeneration)
    {
        var timeline = new LyricsTimelineSyncService(displayService);
        timeline.CueSent += (_, cue) => UpdateTimelineStatus(
            timeline,
            sessionGeneration,
            publish: true,
            current => current with
            {
                State = LyricsSubtitleSessionState.Running,
                IsRunning = true,
                CurrentLine = cue.Text,
                Message = $"Spotify 歌词同步中：{cue.Text}"
            });
        timeline.PositionChanged += (_, position) => UpdateTimelineStatus(
            timeline,
            sessionGeneration,
            publish: false,
            current => current with
            {
                PositionMilliseconds = Math.Max(0, (long)Math.Round(position.TotalMilliseconds))
            });
        timeline.StatusChanged += (_, message) => HandleTimelineStatus(
            timeline,
            sessionGeneration,
            message);
        timeline.ExternalTrackChanged += (_, snapshot) =>
        {
            lock (stateLock)
            {
                if (!desiredRunning
                    || generation != sessionGeneration
                    || !ReferenceEquals(timelineSyncService, timeline))
                {
                    return;
                }
            }

            if (Interlocked.CompareExchange(ref reloadInProgress, 1, 0) != 0)
                return;

            _ = ReloadAfterTrackChangeAsync(snapshot, sessionGeneration);
        };
        return timeline;
    }

    private async Task ReloadAfterTrackChangeAsync(
        LyricsPlaybackSnapshot snapshot,
        long expectedGeneration)
    {
        try
        {
            int requestedOffset;
            bool requestedScroll;
            lock (stateLock)
            {
                if (!desiredRunning || generation != expectedGeneration)
                    return;

                requestedOffset = offsetMilliseconds;
                requestedScroll = scrollEnabled;
            }

            if (!snapshot.HasTrack)
                return;

            await BeginSpotifySessionAsync(
                snapshot,
                requestedOffset,
                requestedScroll,
                null,
                null,
                CancellationToken.None,
                expectedGeneration);
        }
        catch (Exception exception)
        {
            UpdateStatusIfGeneration(expectedGeneration, current => current with
            {
                State = LyricsSubtitleSessionState.Failed,
                IsRunning = false,
                Message = $"Spotify 切歌后重新加载歌词失败：{exception.Message}"
            });
        }
        finally
        {
            Interlocked.Exchange(ref reloadInProgress, 0);
        }
    }

    private void HandleTimelineStatus(
        LyricsTimelineSyncService timeline,
        long sessionGeneration,
        string message)
    {
        lock (stateLock)
        {
            if (!desiredRunning
                || generation != sessionGeneration
                || !ReferenceEquals(timelineSyncService, timeline))
            {
                return;
            }

            var nextState = status.State;
            var isRunning = status.IsRunning;
            if (message.StartsWith("Spotify 已暂停", StringComparison.Ordinal))
            {
                nextState = LyricsSubtitleSessionState.Paused;
                isRunning = true;
            }
            else if (message.StartsWith("歌词同步已启动", StringComparison.Ordinal))
            {
                nextState = LyricsSubtitleSessionState.Running;
                isRunning = true;
            }
            else if (message.Contains("失败", StringComparison.Ordinal))
            {
                nextState = LyricsSubtitleSessionState.Failed;
                isRunning = false;
                desiredRunning = false;
                timelineSyncService = null;
            }
            else if (message is "歌词同步已停止" or "歌词同步已到达末尾")
            {
                nextState = LyricsSubtitleSessionState.Stopped;
                isRunning = false;
                desiredRunning = false;
                timelineSyncService = null;
            }

            var next = status with
            {
                State = nextState,
                IsRunning = isRunning,
                Message = message,
                ObservedAt = DateTimeOffset.Now
            };
            status = next;
            StatusChanged?.Invoke(this, next);
        }
    }

    private LyricsSubtitleSessionStatus? UpdateTimelineStatus(
        LyricsTimelineSyncService timeline,
        long sessionGeneration,
        bool publish,
        Func<LyricsSubtitleSessionStatus, LyricsSubtitleSessionStatus> update)
    {
        lock (stateLock)
        {
            if (!desiredRunning
                || generation != sessionGeneration
                || !ReferenceEquals(timelineSyncService, timeline))
            {
                return null;
            }

            var next = update(status) with { ObservedAt = DateTimeOffset.Now };
            status = next;
            if (publish)
                StatusChanged?.Invoke(this, next);
            return next;
        }
    }

    private void CompletePreparation(
        CancellationTokenSource source,
        long sessionGeneration,
        Func<LyricsSubtitleSessionStatus, LyricsSubtitleSessionStatus> update)
    {
        lock (stateLock)
        {
            if (!desiredRunning || generation != sessionGeneration || source.IsCancellationRequested)
                return;

            desiredRunning = false;
            var next = update(status) with { ObservedAt = DateTimeOffset.Now };
            status = next;
            StatusChanged?.Invoke(this, next);
        }
    }

    private LyricsSubtitleSessionStatus UpdateStatus(
        Func<LyricsSubtitleSessionStatus, LyricsSubtitleSessionStatus> update)
    {
        lock (stateLock)
        {
            var next = update(status) with { ObservedAt = DateTimeOffset.Now };
            status = next;
            StatusChanged?.Invoke(this, next);
            return next;
        }
    }

    private LyricsSubtitleSessionStatus? UpdateStatusIfGeneration(
        long expectedGeneration,
        Func<LyricsSubtitleSessionStatus, LyricsSubtitleSessionStatus> update)
    {
        lock (stateLock)
        {
            if (generation != expectedGeneration)
                return null;

            var next = update(status) with { ObservedAt = DateTimeOffset.Now };
            status = next;
            StatusChanged?.Invoke(this, next);
            return next;
        }
    }

    private LyricsSubtitleSessionStatus? UpdateStatusIfOperationEpoch(
        long expectedOperationEpoch,
        Func<LyricsSubtitleSessionStatus, LyricsSubtitleSessionStatus> update)
    {
        lock (stateLock)
        {
            if (operationEpoch != expectedOperationEpoch)
                return null;

            var next = update(status) with { ObservedAt = DateTimeOffset.Now };
            status = next;
            StatusChanged?.Invoke(this, next);
            return next;
        }
    }

    private static bool IsPreparedTrackUsable(LyricsTrack? track, LyricsPlaybackSnapshot snapshot)
    {
        if (track is null || track.Lines.Count == 0)
            return false;

        var trackTitle = NormalizeTrackText(track.Title);
        var snapshotTitle = NormalizeTrackText(snapshot.Title);
        return string.IsNullOrWhiteSpace(trackTitle)
               || string.IsNullOrWhiteSpace(snapshotTitle)
               || trackTitle.Equals(snapshotTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldScroll(string text)
    {
        var asciiCount = 0;
        var cjkCount = 0;
        foreach (var character in text)
        {
            if (char.IsAsciiLetterOrDigit(character))
                asciiCount++;
            else if (character is >= '\u3400' and <= '\u4DBF'
                     or >= '\u4E00' and <= '\u9FFF'
                     or >= '\u3040' and <= '\u30FF'
                     or >= '\uF900' and <= '\uFAFF')
                cjkCount++;
        }

        return asciiCount >= 32 || cjkCount >= 16;
    }

    private static string BuildTrackKey(LyricsPlaybackSnapshot snapshot)
        => $"{NormalizeTrackText(snapshot.Title)}|{NormalizeTrackText(snapshot.Artist)}|{NormalizeTrackText(snapshot.Album)}";

    private static string BuildTrackName(LyricsPlaybackSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Artist) && !string.IsNullOrWhiteSpace(snapshot.Title))
            return $"{snapshot.Title} - {snapshot.Artist}";
        return string.IsNullOrWhiteSpace(snapshot.Title) ? snapshot.Artist : snapshot.Title;
    }

    private static string NormalizeTrackText(string value)
        => new(value.Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character)).ToArray());

    private static string FormatOffset(int milliseconds)
        => milliseconds switch
        {
            > 0 => $"延后 {milliseconds} ms",
            < 0 => $"提前 {Math.Abs(milliseconds)} ms",
            _ => "不偏移"
        };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}

public enum LyricsSubtitleSessionState
{
    Stopped,
    Preparing,
    Running,
    Paused,
    WaitingForSpotify,
    NoLyrics,
    Stopping,
    Failed
}

public sealed record LyricsSubtitleSessionStatus(
    string? SessionId,
    LyricsSubtitleSessionState State,
    bool IsRunning,
    string Provider,
    string? Track,
    string? Artist,
    string? LyricsSource,
    int LineCount,
    string? CurrentLine,
    long? PositionMilliseconds,
    int OffsetMilliseconds,
    bool ScrollEnabled,
    string Message,
    DateTimeOffset ObservedAt)
{
    public static LyricsSubtitleSessionStatus Stopped()
        => new(
            null,
            LyricsSubtitleSessionState.Stopped,
            false,
            "spotify",
            null,
            null,
            null,
            0,
            null,
            null,
            0,
            true,
            "Spotify 歌词当前未运行",
            DateTimeOffset.Now);
}
