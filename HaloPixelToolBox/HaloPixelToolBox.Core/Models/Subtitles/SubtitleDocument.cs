namespace HaloPixelToolBox.Core.Models.Subtitles;

/// <summary>
/// 字幕文档模型，供视频字幕、浏览器字幕、歌词同步共用。
/// </summary>
public class SubtitleDocument
{
    public string SourceName { get; set; } = string.Empty;
    public IList<SubtitleCue> Cues { get; set; } = new List<SubtitleCue>();
}
