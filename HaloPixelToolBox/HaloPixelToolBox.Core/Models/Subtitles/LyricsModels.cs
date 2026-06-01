namespace HaloPixelToolBox.Core.Models.Subtitles;

public enum LyricsProviderKind
{
    NetEaseCloudMusic,
    QQMusic,
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
    public string? SongId { get; set; }
}

public class LyricsTrack
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public IList<SubtitleCue> Lines { get; set; } = new List<SubtitleCue>();
}
