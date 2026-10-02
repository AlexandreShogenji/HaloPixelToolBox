using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Utilities;

namespace HaloPixelToolBox.Core.Services.Translation;

public sealed class BrowserTimelineSynchronizer
{
    private const int MissingBrowserVideoStopThreshold = 8;
    private readonly BrowserVideoPlaybackStateReader playbackStateReader;

    public BrowserTimelineSynchronizer(BrowserVideoPlaybackStateReader playbackStateReader)
    {
        this.playbackStateReader = playbackStateReader;
    }

    public async Task<BrowserTimelineSyncResult> SyncAsync(BrowserTimelineSyncRequest request, CancellationToken cancellationToken)
    {
        if (request.Cues.Count == 0 || string.IsNullOrWhiteSpace(request.TargetVideoUrl))
            return BrowserTimelineSyncResult.NotStarted;

        var targetBvid = BilibiliVideoUrlHelper.ExtractBvid(request.TargetVideoUrl);
        if (string.IsNullOrWhiteSpace(targetBvid))
            return BrowserTimelineSyncResult.NotStarted;

        while (!cancellationToken.IsCancellationRequested)
        {
            var initialProbe = await playbackStateReader.ProbeCurrentBilibiliSnapshotAsync(
                request.BrowserProcessName,
                targetBvid,
                cancellationToken);
            var initialSnapshot = initialProbe.Snapshot;
            if (IsDifferentBilibiliVideo(initialSnapshot, targetBvid))
                return BrowserTimelineSyncResult.TargetChanged(initialSnapshot!);

            if (IsMatchingBilibiliSnapshot(initialSnapshot, targetBvid) && initialSnapshot!.HasReliablePosition)
                break;

            request.ReportStatus($"已加载 {request.Cues.Count} 条{request.SourceName}字幕，正在等待浏览器播放进度（{initialProbe.Message}）");
            await Task.Delay(300, cancellationToken);
        }

        request.ReportStatus($"已加载 {request.Cues.Count} 条{request.SourceName}字幕，正在跟随浏览器播放进度");
        var lastSentIndex = -1;
        TimeSpan? lastBrowserPosition = null;
        TimeSpan? lastTimelineOffset = null;
        long? lastTimelineOffsetVersion = null;
        var missingBrowserVideoReads = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var probe = await playbackStateReader.ProbeCurrentBilibiliSnapshotAsync(
                request.BrowserProcessName,
                targetBvid,
                cancellationToken);
            if (!probe.HasDevToolsConnection || !probe.HasBilibiliDevToolsTab)
            {
                missingBrowserVideoReads++;
                if (missingBrowserVideoReads >= MissingBrowserVideoStopThreshold)
                    return BrowserTimelineSyncResult.BrowserClosed;
            }
            else
            {
                missingBrowserVideoReads = 0;
            }

            var snapshot = probe.Snapshot;
            if (IsDifferentBilibiliVideo(snapshot, targetBvid))
                return BrowserTimelineSyncResult.TargetChanged(snapshot!);

            if (snapshot?.Position is null)
            {
                request.ReportStatus($"浏览器播放进度读取中断：{probe.Message}");
                await Task.Delay(500, cancellationToken);
                continue;
            }

            if (snapshot.IsPaused)
            {
                request.ReportStatus($"浏览器视频暂停：{FormatTime(snapshot.Position.Value)}");
                await Task.Delay(500, cancellationToken);
                continue;
            }

            var position = snapshot.Position.Value;
            if (lastBrowserPosition is { } previousPosition
                && position + TimeSpan.FromSeconds(1.5) < previousPosition)
            {
                lastSentIndex = -1;
                request.ReportStatus(snapshot.IsLooping
                    ? "检测到浏览器循环播放回到前段，字幕同步游标已重置"
                    : "检测到浏览器播放进度回退，字幕同步游标已重置");
            }

            lastBrowserPosition = position;
            var timelineOffset = request.TimelineOffsetProvider();
            var timelineOffsetVersion = request.TimelineOffsetVersionProvider();
            if (lastTimelineOffset is null
                || lastTimelineOffsetVersion is null
                || timelineOffsetVersion != lastTimelineOffsetVersion.Value
                || Math.Abs((timelineOffset - lastTimelineOffset.Value).TotalMilliseconds) > 0.1)
            {
                lastSentIndex = -1;
                lastTimelineOffset = timelineOffset;
                lastTimelineOffsetVersion = timelineOffsetVersion;
                if (Math.Abs(timelineOffset.TotalMilliseconds) > 0.1)
                    request.ReportStatus($"已应用歌词同步偏移：{FormatOffset(timelineOffset)}，字幕游标已重置");
            }

            var timelinePosition = ApplyTimelineOffset(position, timelineOffset);
            if (timelinePosition > request.Cues[^1].End + TimeSpan.FromSeconds(2))
            {
                request.ReportStatus(snapshot.IsLooping
                    ? "Browser video reached the end; waiting for loop restart"
                    : "Browser video reached the end; waiting for seek, loop, or next video");
                await Task.Delay(500, cancellationToken);
                continue;
            }

            var cueIndex = FindCueIndexForBrowserPosition(request.Cues, timelinePosition);
            if (cueIndex >= 0)
            {
                request.SetCueIndex(cueIndex);
                request.SetNextCueIndex(Math.Min(cueIndex + 1, request.Cues.Count));
            }

            if (cueIndex >= 0 && cueIndex != lastSentIndex)
            {
                var cue = request.Cues[cueIndex];
                var sentText = await request.SendCueAsync(cue, cancellationToken);
                lastSentIndex = cueIndex;
                request.ReportStatus($"已按浏览器进度发送{request.SourceName}字幕：{sentText}");
            }

            await Task.Delay(300, cancellationToken);
        }

        return BrowserTimelineSyncResult.Completed;
    }

    public static bool IsMatchingBilibiliSnapshot(BrowserVideoPlaybackSnapshot? snapshot, string targetBvid)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Url))
            return false;

        var snapshotBvid = BilibiliVideoUrlHelper.ExtractBvid(snapshot.Url);
        return !string.IsNullOrWhiteSpace(targetBvid)
               && snapshotBvid.Equals(targetBvid, StringComparison.OrdinalIgnoreCase);
    }

    public static int FindCueIndex(IReadOnlyList<SubtitleCue> cues, TimeSpan position)
    {
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            if (position >= cue.Start && position <= cue.End)
                return index;
        }

        return -1;
    }

    public static int FindCueIndexForBrowserPosition(IReadOnlyList<SubtitleCue> cues, TimeSpan position)
    {
        var activeIndex = FindCueIndex(cues, position);
        if (activeIndex >= 0)
            return activeIndex;

        if (cues.Count == 0 || position < cues[0].Start)
            return -1;

        for (var index = cues.Count - 1; index >= 0; index--)
        {
            if (position >= cues[index].Start)
                return index;
        }

        return -1;
    }

    private static bool IsDifferentBilibiliVideo(BrowserVideoPlaybackSnapshot? snapshot, string targetBvid)
    {
        return snapshot is not null
               && !IsMatchingBilibiliSnapshot(snapshot, targetBvid)
               && !string.IsNullOrWhiteSpace(BilibiliVideoUrlHelper.ExtractBvid(snapshot.Url));
    }

    private static TimeSpan ApplyTimelineOffset(TimeSpan browserPosition, TimeSpan timelineOffset)
    {
        var adjustedPosition = browserPosition - timelineOffset;
        return adjustedPosition < TimeSpan.Zero ? TimeSpan.Zero : adjustedPosition;
    }

    private static string FormatTime(TimeSpan value)
    {
        return value.ToString(value.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
    }

    private static string FormatOffset(TimeSpan value)
    {
        var milliseconds = Math.Round(value.TotalMilliseconds);
        return milliseconds > 0
            ? $"延后 {milliseconds:0} ms"
            : $"提前 {Math.Abs(milliseconds):0} ms";
    }
}

public sealed class BrowserTimelineSyncRequest
{
    public string BrowserProcessName { get; init; } = string.Empty;

    public string TargetVideoUrl { get; init; } = string.Empty;

    public string SourceName { get; init; } = string.Empty;

    public IReadOnlyList<SubtitleCue> Cues { get; init; } = [];

    public Func<TimeSpan> TimelineOffsetProvider { get; init; } = () => TimeSpan.Zero;

    public Func<long> TimelineOffsetVersionProvider { get; init; } = () => 0L;

    public Func<SubtitleCue, CancellationToken, Task<string>> SendCueAsync { get; init; } =
        (_, _) => Task.FromResult(string.Empty);

    public Action<int> SetCueIndex { get; init; } = _ => { };

    public Action<int> SetNextCueIndex { get; init; } = _ => { };

    public Action<string> ReportStatus { get; init; } = _ => { };
}

public sealed record BrowserTimelineSyncResult(BrowserTimelineSyncResultKind Kind, BrowserVideoPlaybackSnapshot? Snapshot = null)
{
    public static BrowserTimelineSyncResult Completed { get; } = new(BrowserTimelineSyncResultKind.Completed);

    public static BrowserTimelineSyncResult BrowserClosed { get; } = new(BrowserTimelineSyncResultKind.BrowserClosed);

    public static BrowserTimelineSyncResult NotStarted { get; } = new(BrowserTimelineSyncResultKind.NotStarted);

    public static BrowserTimelineSyncResult TargetChanged(BrowserVideoPlaybackSnapshot snapshot) =>
        new(BrowserTimelineSyncResultKind.TargetChanged, snapshot);
}

public enum BrowserTimelineSyncResultKind
{
    Completed,
    TargetChanged,
    BrowserClosed,
    NotStarted
}
