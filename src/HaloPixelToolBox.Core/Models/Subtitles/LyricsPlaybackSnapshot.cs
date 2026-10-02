namespace HaloPixelToolBox.Core.Models.Subtitles;

public enum LyricsPlaybackState
{
    NotRunning,
    Playing,
    Paused,
    RunningUnknown
}

public sealed class LyricsPlaybackSnapshot
{
    public LyricsPlaybackState State { get; init; } = LyricsPlaybackState.NotRunning;

    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public string Album { get; init; } = string.Empty;

    public string? PlatformTrackId { get; init; }

    public TimeSpan? Position { get; init; }

    public TimeSpan? Duration { get; init; }

    public string Source { get; init; } = string.Empty;

    public bool HasTrack => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist);

    public bool HasReliablePosition => Position is not null;

    public bool IsPlaying => State == LyricsPlaybackState.Playing;

    public bool IsPaused => State == LyricsPlaybackState.Paused;

    public static LyricsPlaybackSnapshot NotRunning { get; } = new()
    {
        State = LyricsPlaybackState.NotRunning
    };
}
