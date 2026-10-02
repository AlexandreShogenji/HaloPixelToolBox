using HaloPixelToolBox.Core.Models.Display;

namespace HaloPixelToolBox.Core.Models.Subtitles;

public sealed class PotPlayerSubtitleSyncConfiguration
{
    public string SubtitleOutputPath { get; set; } = string.Empty;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public DisplayTextOptions DisplayOptions { get; set; } = new()
    {
        Source = DisplayContentKind.VideoSubtitle,
        Layout = HaloPixelTextLayout.Center,
        ScrollDirection = TextScrollDirection.RightToLeft
    };
}
