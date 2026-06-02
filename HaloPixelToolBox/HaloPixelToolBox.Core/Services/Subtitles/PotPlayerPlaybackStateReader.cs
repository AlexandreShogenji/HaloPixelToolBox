using System.Diagnostics;
using HaloPixelToolBox.Core.Models.Subtitles;
using Windows.Media.Control;

namespace HaloPixelToolBox.Core.Services.Subtitles;

public sealed class PotPlayerPlaybackStateReader
{
    private static GlobalSystemMediaTransportControlsSessionManager? mediaSessionManager;

    private static readonly string[] ProcessNames =
    [
        "PotPlayerMini64",
        "PotPlayerMini",
        "PotPlayer64",
        "PotPlayer"
    ];

    private static readonly string[] PauseKeywords =
    [
        "暂停",
        "paused",
        "pause"
    ];

    public PotPlayerPlaybackState GetState()
        => GetSnapshotAsync().GetAwaiter().GetResult().State;

    public async Task<PotPlayerPlaybackSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var processSnapshot = GetProcessSnapshot();
        if (processSnapshot.State == PotPlayerPlaybackState.NotRunning)
            return processSnapshot;

        return await TryReadMediaSessionSnapshotAsync(cancellationToken) ?? processSnapshot;
    }

    private static PotPlayerPlaybackSnapshot GetProcessSnapshot()
    {
        var processes = ProcessNames
            .SelectMany(Process.GetProcessesByName)
            .ToList();

        if (processes.Count == 0)
            return PotPlayerPlaybackSnapshot.NotRunning;

        var titles = processes
            .Select(process => SafeReadTitle(process))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToList();

        if (titles.Any(title => PauseKeywords.Any(keyword => title.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
            return new PotPlayerPlaybackSnapshot
            {
                State = PotPlayerPlaybackState.Paused,
                Source = "PotPlayer 进程窗口"
            };

        return new PotPlayerPlaybackSnapshot
        {
            State = titles.Count > 0 ? PotPlayerPlaybackState.Playing : PotPlayerPlaybackState.RunningUnknown,
            Source = "PotPlayer 进程窗口"
        };
    }

    private static async Task<PotPlayerPlaybackSnapshot?> TryReadMediaSessionSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            // PotPlayer 若接入了系统媒体控制中心，可通过 Windows 媒体会话读取真实播放进度。
            var manager = await GetMediaSessionManagerAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var session = manager.GetSessions()
                .FirstOrDefault(item => IsPotPlayerSession(item.SourceAppUserModelId));
            if (session is null)
                return null;

            var playbackInfo = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var state = ConvertPlaybackStatus(playbackInfo.PlaybackStatus);
            var duration = NormalizeTimelinePosition(timeline.EndTime);
            var position = NormalizeTimelinePosition(timeline.Position);
            if (position is not null && state == PotPlayerPlaybackState.Playing)
            {
                var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                if (elapsed is { Ticks: > 0 } && elapsed < TimeSpan.FromSeconds(5))
                    position += elapsed;

                if (duration is not null && position > duration)
                    position = duration;
            }

            return new PotPlayerPlaybackSnapshot
            {
                State = state,
                Position = position,
                Duration = duration,
                Source = $"Windows 媒体会话：{session.SourceAppUserModelId}"
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionManager> GetMediaSessionManagerAsync()
    {
        mediaSessionManager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return mediaSessionManager;
    }

    private static bool IsPotPlayerSession(string? sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
            return false;

        return ProcessNames.Any(name => sourceAppUserModelId.Contains(name, StringComparison.OrdinalIgnoreCase))
            || sourceAppUserModelId.Contains("PotPlayer", StringComparison.OrdinalIgnoreCase);
    }

    private static PotPlayerPlaybackState ConvertPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PotPlayerPlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PotPlayerPlaybackState.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PotPlayerPlaybackState.Paused,
            _ => PotPlayerPlaybackState.RunningUnknown
        };
    }

    private static TimeSpan? NormalizeTimelinePosition(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            return null;

        return value;
    }

    private static string SafeReadTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
