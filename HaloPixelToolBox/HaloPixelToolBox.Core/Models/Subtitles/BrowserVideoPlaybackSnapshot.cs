namespace HaloPixelToolBox.Core.Models.Subtitles;

public sealed class BrowserVideoPlaybackSnapshot
{
    public string Url { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public TimeSpan? Position { get; init; }

    public TimeSpan? Duration { get; init; }

    public bool IsPaused { get; init; }

    public bool IsEnded { get; init; }

    public bool IsLooping { get; init; }

    public double PlaybackRate { get; init; } = 1;

    public string Source { get; init; } = string.Empty;

    public bool HasReliablePosition => Position is not null;
}
