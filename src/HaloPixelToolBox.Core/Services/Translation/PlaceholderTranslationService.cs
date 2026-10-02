namespace HaloPixelToolBox.Core.Services.Translation;

public class PlaceholderTranslationService : ITranslationService
{
    public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        // TODO: 后续在这里接入真实翻译 API，例如 OpenAI、Azure、腾讯、百度等。
        return Task.FromResult($"[{targetLanguage}] {text}");
    }
}
