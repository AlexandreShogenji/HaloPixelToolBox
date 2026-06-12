namespace HaloPixelToolBox.Core.Models.Subtitles;

public enum BrowserMusicPlatform
{
    Unknown,
    Bilibili
}

public sealed class BrowserMusicTrackMetadata
{
    public BrowserMusicPlatform Platform { get; init; }

    public string SourceTitle { get; init; } = string.Empty;

    public string SourceUrl { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public string Album { get; init; } = string.Empty;

    public TimeSpan? Duration { get; init; }

    public string PlatformTrackId { get; init; } = string.Empty;

    public bool HasPageMusicTitle { get; init; }

    public bool HasPageMusicArtist { get; init; }

    public bool HasPageMusicAlbum { get; init; }

    public bool HasPageMusicMetadata => HasPageMusicTitle || HasPageMusicArtist || HasPageMusicAlbum;

    public bool HasUsableQuery => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(SourceTitle);
}
