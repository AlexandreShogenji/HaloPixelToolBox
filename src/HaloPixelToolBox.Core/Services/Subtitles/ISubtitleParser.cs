using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Subtitles;

public interface ISubtitleParser
{
    string FormatName { get; }

    bool CanParse(string filePath);

    SubtitleDocument Parse(string filePath);
}
