using HaloPixelToolBox.Core.Models.Subtitles;
using Windows.Media.Control;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public sealed class SpotifyMediaSessionPlaybackProvider : IPlaybackMetadataProvider
{
    private static GlobalSystemMediaTransportControlsSessionManager? mediaSessionManager;

    public async Task<LyricsPlaybackSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var manager = await GetMediaSessionManagerAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var session = FindSpotifySession(manager);
            if (session is null)
                return LyricsPlaybackSnapshot.NotRunning;

            var playbackInfo = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var state = ConvertPlaybackStatus(playbackInfo.PlaybackStatus);
            var duration = NormalizeTimelinePosition(timeline.EndTime);
            var position = NormalizeTimelinePosition(timeline.Position);
            if (position is not null && state == LyricsPlaybackState.Playing)
            {
                var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                if (elapsed is { Ticks: > 0 } && elapsed < TimeSpan.FromSeconds(5))
                    position += elapsed;

                if (duration is not null && position > duration)
                    position = duration;
            }

            return new LyricsPlaybackSnapshot
            {
                State = state,
                Title = mediaProperties.Title ?? string.Empty,
                Artist = mediaProperties.Artist ?? string.Empty,
                Album = mediaProperties.AlbumTitle ?? string.Empty,
                Position = position,
                Duration = duration,
                Source = $"Windows 媒体会话：{session.SourceAppUserModelId}"
            };
        }
        catch
        {
            return LyricsPlaybackSnapshot.NotRunning;
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionManager> GetMediaSessionManagerAsync()
    {
        mediaSessionManager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return mediaSessionManager;
    }

    private static GlobalSystemMediaTransportControlsSession? FindSpotifySession(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
        return sessions.FirstOrDefault(session => IsSpotifySession(session.SourceAppUserModelId))
            ?? (IsSpotifySession(manager.GetCurrentSession()?.SourceAppUserModelId) ? manager.GetCurrentSession() : null);
    }

    private static bool IsSpotifySession(string? sourceAppUserModelId)
    {
        return !string.IsNullOrWhiteSpace(sourceAppUserModelId)
            && sourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase);
    }

    private static LyricsPlaybackState ConvertPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => LyricsPlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => LyricsPlaybackState.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => LyricsPlaybackState.Paused,
            _ => LyricsPlaybackState.RunningUnknown
        };
    }

    private static TimeSpan? NormalizeTimelinePosition(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            return null;

        return value;
    }
}
