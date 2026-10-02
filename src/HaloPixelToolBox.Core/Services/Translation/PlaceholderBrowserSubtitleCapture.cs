using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Translation;

public class PlaceholderBrowserSubtitleCapture : IBrowserSubtitleCapture
{
    public async IAsyncEnumerable<SubtitleCue> CaptureAsync(BrowserTranslationConfiguration configuration, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return new SubtitleCue
            {
                Start = TimeSpan.FromSeconds(index * 3),
                End = TimeSpan.FromSeconds(index * 3 + 3),
                Text = $"浏览器字幕捕获占位 {++index}"
            };

            await Task.Delay(3000, cancellationToken);
        }
    }
}
