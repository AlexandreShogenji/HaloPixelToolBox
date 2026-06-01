using HaloPixelToolBox.Core.Models.Display;

namespace HaloPixelToolBox.Core.Models.Subtitles;

public class VideoSubtitleConfiguration
{
    public string VideoPath { get; set; } = string.Empty;
    public string SubtitlePath { get; set; } = string.Empty;
    public DisplayTextOptions DisplayOptions { get; set; } = new();
}
