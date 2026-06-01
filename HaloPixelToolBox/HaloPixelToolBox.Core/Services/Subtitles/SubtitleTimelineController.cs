using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;

namespace HaloPixelToolBox.Core.Services.Subtitles;

public class SubtitleTimelineController
{
    private readonly HaloPixelDisplayService displayService;
    private CancellationTokenSource? cancellationTokenSource;

    public SubtitleTimelineController(HaloPixelDisplayService displayService)
    {
        this.displayService = displayService;
    }

    public void Stop()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource = null;
    }

    public void Play(SubtitleDocument document, DisplayTextOptions options, TimeSpan startPosition)
    {
        Stop();
        cancellationTokenSource = new CancellationTokenSource();
        _ = PlayAsync(document, options, startPosition, cancellationTokenSource.Token);
    }

    public SubtitleCue? GetCurrentCue(SubtitleDocument document, TimeSpan position)
    {
        return document.Cues.FirstOrDefault(cue => cue.Start <= position && cue.End >= position);
    }

    private async Task PlayAsync(SubtitleDocument document, DisplayTextOptions options, TimeSpan startPosition, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now - startPosition;
        string lastText = string.Empty;

        while (!cancellationToken.IsCancellationRequested)
        {
            var position = DateTimeOffset.Now - startedAt;
            var cue = GetCurrentCue(document, position);
            if (cue is not null && cue.Text != lastText)
            {
                lastText = cue.Text;
                await displayService.SendSubtitleCueAsync(cue, options, cancellationToken);
            }

            await Task.Delay(100, cancellationToken);
        }
    }
}
