namespace HaloPixelToolBox.Core.Services.Translation;

/// <summary>
/// 在线翻译扩展点。API 地址、Key、语言配置从 BrowserTranslationConfiguration 注入。
/// </summary>
public interface ITranslationService
{
    Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default);
}
