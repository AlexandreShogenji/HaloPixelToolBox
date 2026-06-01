namespace HaloPixelToolBox.Core.Models.Subtitles;

/// <summary>
/// 字幕时间轴条目。
/// </summary>
public class SubtitleCue
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; } = string.Empty;

    public override string ToString() => $"{Start:hh\\:mm\\:ss\\.fff} --> {End:hh\\:mm\\:ss\\.fff} {Text}";
}
