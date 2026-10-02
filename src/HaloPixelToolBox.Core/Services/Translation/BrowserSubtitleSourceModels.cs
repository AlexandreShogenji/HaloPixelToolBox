using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Translation;

public sealed class BrowserSubtitleSourceResult
{
    public BrowserSubtitleSourceKind Kind { get; init; }

    public string SourceName { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public IReadOnlyList<SubtitleCue> Cues { get; init; } = [];

    public static BrowserSubtitleSourceResult Timeline(
        string sourceName,
        IReadOnlyList<SubtitleCue> cues,
        string title = "",
        string artist = "")
    {
        return new BrowserSubtitleSourceResult
        {
            Kind = BrowserSubtitleSourceKind.Timeline,
            SourceName = sourceName,
            Title = title,
            Artist = artist,
            Cues = cues
        };
    }

    public static BrowserSubtitleSourceResult AsrStreaming()
    {
        return new BrowserSubtitleSourceResult
        {
            Kind = BrowserSubtitleSourceKind.AsrStreaming,
            SourceName = "ASR"
        };
    }
}

public enum BrowserSubtitleSourceKind
{
    Timeline,
    AsrStreaming
}
