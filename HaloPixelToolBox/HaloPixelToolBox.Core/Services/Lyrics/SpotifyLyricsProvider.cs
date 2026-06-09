using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public sealed class SpotifyLyricsProvider : ILyricsProvider
{
    private const double FastAcceptConfidence = 0.88;
    private const double SimilarConfidenceTolerance = 0.03;

    private readonly IPlaybackMetadataProvider playbackProvider;
    private readonly LrcLibLyricsProvider lrcLibLyricsProvider = new();
    private readonly NetEaseCloudMusicSearchLyricsProvider netEaseLyricsProvider = new();
    private readonly KugouLyricsProvider kugouLyricsProvider = new();
    private readonly LocalFileLyricsProvider localFileLyricsProvider = new();

    public SpotifyLyricsProvider(IPlaybackMetadataProvider playbackProvider)
    {
        this.playbackProvider = playbackProvider;
    }

    public LyricsProviderKind ProviderKind => LyricsProviderKind.Spotify;

    public async Task<LyricsTrack?> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        var snapshot = await playbackProvider.GetSnapshotAsync(cancellationToken);
        if (!snapshot.HasTrack)
            throw new InvalidOperationException("未检测到 Spotify 当前播放歌曲，请先打开 Spotify 并播放音乐");

        var spotifyQuery = BuildSpotifyLyricsQuery(query, snapshot);
        var track = await SearchOnlineLyricsProvidersAsync(spotifyQuery, cancellationToken);
        if (track is null && !string.IsNullOrWhiteSpace(query.FilePath))
            track = await localFileLyricsProvider.SearchAsync(spotifyQuery, cancellationToken);
        if (track is null)
            return null;

        var isLocalLyrics = !track.SourceName.Equals("LRCLIB", StringComparison.OrdinalIgnoreCase)
            && !track.SourceName.Equals("网易云音乐", StringComparison.OrdinalIgnoreCase)
            && !track.SourceName.Equals("酷狗音乐", StringComparison.OrdinalIgnoreCase);
        track.Provider = ProviderKind;
        track.SourceName = isLocalLyrics
            ? $"Spotify 当前播放 + 本地 LRC：{Path.GetFileName(query.FilePath)}"
            : $"Spotify 当前播放 + {track.SourceName}";
        track.PlatformTrackId = snapshot.PlatformTrackId;
        track.Title = string.IsNullOrWhiteSpace(track.Title) ? snapshot.Title : track.Title;
        track.Artist = string.IsNullOrWhiteSpace(track.Artist) ? snapshot.Artist : track.Artist;
        track.Album = string.IsNullOrWhiteSpace(track.Album) ? snapshot.Album : track.Album;
        track.Duration ??= snapshot.Duration;
        track.CurrentPosition = snapshot.Position;
        return track;
    }

    private async Task<LyricsTrack?> SearchOnlineLyricsProvidersAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var shouldDisposeCancellationTokenSource = true;
        var providerCancellationToken = linkedCancellationTokenSource.Token;
        var pendingTasks = new List<Task<ProviderSearchResult>>
        {
            SearchProviderAsync("LRCLIB", 0, token => lrcLibLyricsProvider.SearchAsync(query, token), providerCancellationToken),
            SearchProviderAsync("网易云音乐", 1, token => netEaseLyricsProvider.SearchAsync(query, token), providerCancellationToken),
            SearchProviderAsync("酷狗音乐", 2, token => kugouLyricsProvider.SearchAsync(query, token), providerCancellationToken)
        };

        try
        {
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
                    _ = DisposeCancellationTokenSourceAfterCompletionAsync(pendingTasks, linkedCancellationTokenSource);
                    shouldDisposeCancellationTokenSource = false;
                    return result.Track;
                }
            }

            return bestResult?.Track;
        }
        finally
        {
            if (shouldDisposeCancellationTokenSource)
                linkedCancellationTokenSource.Dispose();
        }
    }

    private static async Task DisposeCancellationTokenSourceAfterCompletionAsync(
        IReadOnlyCollection<Task<ProviderSearchResult>> tasks,
        CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }
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

    private static bool IsUsableTrack(LyricsTrack? track)
    {
        return track is not null
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

        if (candidate.Track.IsSynced != current.Track.IsSynced)
            return candidate.Track.IsSynced;

        return candidate.Priority < current.Priority;
    }

    private static LyricsQuery BuildSpotifyLyricsQuery(LyricsQuery query, LyricsPlaybackSnapshot snapshot)
    {
        var title = string.IsNullOrWhiteSpace(snapshot.Title) ? query.Title : snapshot.Title;
        var artist = string.IsNullOrWhiteSpace(snapshot.Artist) ? query.Artist : snapshot.Artist;
        var album = string.IsNullOrWhiteSpace(snapshot.Album) ? query.Album : snapshot.Album;
        return new LyricsQuery
        {
            Provider = LyricsProviderKind.Spotify,
            Keyword = string.IsNullOrWhiteSpace(query.Keyword) ? $"{title} {artist}".Trim() : query.Keyword,
            Title = title,
            Artist = artist,
            Album = album,
            Duration = snapshot.Duration ?? query.Duration,
            PlatformTrackId = snapshot.PlatformTrackId,
            Isrc = query.Isrc,
            FilePath = query.FilePath,
            PreferSyncedLyrics = query.PreferSyncedLyrics
        };
    }

    private readonly record struct ProviderSearchResult(string SourceName, int Priority, LyricsTrack? Track, Exception? Error);
}
