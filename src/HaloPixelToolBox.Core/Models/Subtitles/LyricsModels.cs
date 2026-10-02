namespace HaloPixelToolBox.Core.Models.Subtitles;

public enum LyricsProviderKind
{
    NetEaseCloudMusic,
    QQMusic,
    Spotify,
    LocalFile,
    Custom
}

/// <summary>
/// 多平台歌词查询条件。具体平台签名、Cookie、版本适配在 Provider 中扩展。
/// </summary>
public class LyricsQuery
{
    public LyricsProviderKind Provider { get; set; }
    public string Keyword { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public TimeSpan? Duration { get; set; }
    public string? Isrc { get; set; }
    public string? PlatformTrackId { get; set; }
    public string? SongId { get; set; }
    public string? SourceUrl { get; set; }
    public string? FilePath { get; set; }
    public bool PreferSyncedLyrics { get; set; } = true;
}

public class LyricsTrack
{
    public LyricsProviderKind Provider { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string? PlatformTrackId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public TimeSpan? Duration { get; set; }
    public bool IsSynced { get; set; }
    public bool IsTranslation { get; set; }
    public TimeSpan Offset { get; set; }
    public TimeSpan? CurrentPosition { get; set; }
    public double Confidence { get; set; }
    public string RawSource { get; set; } = string.Empty;
    public IList<SubtitleCue> Lines { get; set; } = new List<SubtitleCue>();
}
