using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public sealed class BrowserMusicLyricsService
{
    private const double FastAcceptConfidence = 0.88;
    private const double SimilarConfidenceTolerance = 0.03;

    private readonly LrcLibLyricsProvider lrcLibLyricsProvider = new();
    private readonly NetEaseCloudMusicSearchLyricsProvider netEaseLyricsProvider = new();
    private readonly KugouLyricsProvider kugouLyricsProvider = new();

    public async Task<LyricsTrack?> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var providerCancellationToken = linkedCancellationTokenSource.Token;
        var pendingTasks = new List<Task<ProviderSearchResult>>
        {
            SearchProviderAsync("LRCLIB", 0, token => lrcLibLyricsProvider.SearchAsync(query, token), providerCancellationToken),
            SearchProviderAsync("网易云音乐", 1, token => netEaseLyricsProvider.SearchAsync(query, token), providerCancellationToken),
            SearchProviderAsync("酷狗音乐", 2, token => kugouLyricsProvider.SearchAsync(query, token), providerCancellationToken)
        };

        ProviderSearchResult? bestResult = null;
        while (pendingTasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(pendingTasks);
            pendingTasks.Remove(completedTask);

            var result = await completedTask;
            if (!IsUsableTrack(result.Track))
                continue;

            if (bestResult is null || IsBetterResult(result, bestResult.Value))
                bestResult = result;

            if (result.Track!.Confidence >= FastAcceptConfidence)
            {
                linkedCancellationTokenSource.Cancel();
                await CompleteRemainingSearchesSilentlyAsync(pendingTasks);
                return result.Track;
            }
        }

        return bestResult?.Track;
    }

    private static async Task<ProviderSearchResult> SearchProviderAsync(
        string sourceName,
        int priority,
        Func<CancellationToken, Task<LyricsTrack?>> searchAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            var track = await searchAsync(cancellationToken);
            return new ProviderSearchResult(sourceName, priority, track, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ProviderSearchResult(sourceName, priority, null, null);
        }
        catch (Exception ex)
        {
            return new ProviderSearchResult(sourceName, priority, null, ex);
        }
    }

    private static async Task CompleteRemainingSearchesSilentlyAsync(IReadOnlyCollection<Task<ProviderSearchResult>> tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
        }
    }

    private static bool IsUsableTrack(LyricsTrack? track)
    {
        return track is not null
            && track.IsSynced
            && track.Lines.Count > 0
            && track.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Text));
    }

    private static bool IsBetterResult(ProviderSearchResult candidate, ProviderSearchResult current)
    {
        if (candidate.Track is null)
            return false;
        if (current.Track is null)
            return true;

        var confidenceDiff = candidate.Track.Confidence - current.Track.Confidence;
        if (Math.Abs(confidenceDiff) > SimilarConfidenceTolerance)
            return confidenceDiff > 0;

        return candidate.Priority < current.Priority;
    }

    private readonly record struct ProviderSearchResult(string SourceName, int Priority, LyricsTrack? Track, Exception? Error);
}
