using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Translation;

/// <summary>
/// 浏览器字幕捕获扩展点。后续可通过 OCR、浏览器扩展、DevTools Protocol 或无障碍树实现。
/// </summary>
public interface IBrowserSubtitleCapture
{
    IAsyncEnumerable<SubtitleCue> CaptureAsync(BrowserTranslationConfiguration configuration, CancellationToken cancellationToken = default);
}
