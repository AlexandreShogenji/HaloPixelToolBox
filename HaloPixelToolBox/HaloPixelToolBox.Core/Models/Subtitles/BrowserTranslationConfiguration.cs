using HaloPixelToolBox.Core.Models.Display;

namespace HaloPixelToolBox.Core.Models.Subtitles;

public class BrowserTranslationConfiguration
{
    public string BrowserProcessName { get; set; } = "msedge";
    public string TranslationApiEndpoint { get; set; } = string.Empty;
    public string TranslationApiKey { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh-CN";
    public DisplayTextOptions DisplayOptions { get; set; } = new();
}
