using HaloPixelToolBox.Core.Models.Display;

namespace HaloPixelToolBox.Core.Models.Subtitles;

public class BrowserTranslationConfiguration
{
    public string BrowserProcessName { get; set; } = "chrome";
    public string BilibiliVideoUrl { get; set; } = string.Empty;
    public string TranslationApiEndpoint { get; set; } = string.Empty;
    public string TranslationApiKey { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh-CN";
    public BrowserSubtitleOutputMode OutputMode { get; set; } = BrowserSubtitleOutputMode.Translated;
    public BrowserSubtitleAsrEngine AsrEngine { get; set; } = BrowserSubtitleAsrEngine.WhisperLargeV3Turbo;
    public DisplayTextOptions DisplayOptions { get; set; } = new();
}
