using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

internal static class DshTaskFeedback
{
    internal const string RequestDisplayCue = "task_display_requested";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HaloPixelDisplayService Display = new();
    private sealed record PageDelivery(string SnapshotIdentity, CancellationTokenSource Cancellation);
    private static PageDelivery? pageDelivery;
    private static string lastIdentity = string.Empty;
    private static string lastSentIdentity = string.Empty;
    private static bool observing;
    private static string attentionIdentity = string.Empty;
    private static string displaySession = string.Empty;
    private static long? taskDisplayRevision;

    public static async Task PublishAsync(DshTaskSnapshot snapshot, string cue, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            ObserveTaskChanges();
            token.ThrowIfCancellationRequested();
            if (!snapshot.IsMonitoring || SnapshotIdentity(App.DshTasks.Current) != SnapshotIdentity(snapshot)) return;
            // Only an explicit task action may acquire the screen. Restoring a
            // monitor, progress, completion and approvals never reacquire it.
            if (cue == RequestDisplayCue)
            {
                if (snapshot.DisplayRequestRevision is not { } requestedRevision
                    || requestedRevision != HaloPixelDisplayService.ForegroundRevision)
                    return;
                if (App.LyricsSubtitleControl.CurrentStatus.IsRunning)
                    await App.LyricsSubtitleControl.StopAsync(restoreScene: false, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                if (requestedRevision != HaloPixelDisplayService.ForegroundRevision
                    || SnapshotIdentity(App.DshTasks.Current) != SnapshotIdentity(snapshot))
                    return;
                CancelPages();
                displaySession = snapshot.SessionId;
                taskDisplayRevision = requestedRevision;
                lastSentIdentity = string.Empty;
                return;
            }
            var detail = snapshot.NeedsAttention ? snapshot.Detail
                : snapshot.State == "completed" ? snapshot.FinalText : snapshot.Detail;
            if (displaySession == snapshot.SessionId && taskDisplayRevision is { } revision)
                await BeginPagesAsync(snapshot, DshTaskSubtitleFormatter.ForTask(snapshot), revision, token);
            if (SnapshotIdentity(App.DshTasks.Current) != SnapshotIdentity(snapshot)
                || App.DshTasks.Current.VoiceAnswerRevision != snapshot.VoiceAnswerRevision) return;
            if (cue.Length > 0)
            {
                if (snapshot.NeedsAttention)
                    await App.VoiceAgent.NotifyTaskAsync(cue, detail, true, snapshot, token);
                else if (snapshot.State is "completed" or "failed" or "cancelled")
                    await App.VoiceAgent.NotifyTaskAsync(cue, snapshot.StatusText + "。" + SpeechSummary(detail), false, snapshot, token);
                else await App.VoiceAgent.NotifyTaskAsync(cue, token);
            }
        }
        finally { Gate.Release(); }
    }

    // Replies also appear on the device when no task has been created yet.
    public static async Task PublishVoiceReplyAsync(string text, CancellationToken token, bool isTaskReply = false,
        long? expectedForegroundRevision = null)
    {
        // A device result is spoken and retained in chat. Sending it as text
        // would immediately replace the clock/scene/lyrics just requested.
        if (!isTaskReply || expectedForegroundRevision is null) return;
        await Gate.WaitAsync(token);
        try
        {
            ObserveTaskChanges();
            var snapshot = App.DshTasks.Current;
            await BeginPagesAsync(snapshot, DshTaskSubtitleFormatter.ForReply(snapshot, text, isTaskReply),
                expectedForegroundRevision.Value, token);
        }
        finally { Gate.Release(); }
    }

    private static void ObserveTaskChanges()
    {
        if (observing) return;
        observing = true;
        HaloPixelDisplayService.ContentSent += (_, content) =>
        {
            if (content.ContentKind != DisplayContentKind.TaskStatus)
            {
                CancelPages();
                Interlocked.Exchange(ref lastSentIdentity, string.Empty);
            }
        };
        attentionIdentity = AttentionIdentity(App.DshTasks.Current);
        App.DshTasks.Changed += (_, snapshot) =>
        {
            var next = AttentionIdentity(snapshot);
            var previous = Interlocked.Exchange(ref attentionIdentity, next);
            if (previous.Length > 0 && previous != next) _ = InvalidatePromptAsync();
            if (!snapshot.IsMonitoring)
            {
                Interlocked.Exchange(ref lastSentIdentity, string.Empty);
                Interlocked.Exchange(ref displaySession, string.Empty);
            }
            var delivery = Volatile.Read(ref pageDelivery);
            if (delivery is not null && delivery.SnapshotIdentity != SnapshotIdentity(snapshot))
            {
                try { delivery.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            }
        };
    }

    private static void CancelPages()
    {
        var delivery = Volatile.Read(ref pageDelivery);
        try { delivery?.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static string AttentionIdentity(DshTaskSnapshot snapshot)
        => snapshot.IsMonitoring && snapshot.NeedsAttention
            ? snapshot.SessionId + "\n" + JsonSerializer.Serialize(snapshot.PendingInteractions) : string.Empty;

    private static async Task InvalidatePromptAsync()
    {
        try { await App.VoiceAgent.InvalidateTaskPromptAsync(); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine("取消旧语音提示失败：" + exception.Message); }
    }

    private static string SnapshotIdentity(DshTaskSnapshot snapshot)
        => snapshot.SessionId + "\n" + snapshot.IsMonitoring + "\n" + snapshot.State + "\n" + snapshot.Detail
            + "\n" + snapshot.FinalText + "\n" + snapshot.VoiceAnswerRevision + "\n" + JsonSerializer.Serialize(snapshot.PendingInteractions);

    private static async Task BeginPagesAsync(DshTaskSnapshot snapshot, IReadOnlyList<string> pages,
        long expectedForegroundRevision, CancellationToken token)
    {
        if (HaloPixelDisplayService.ForegroundRevision != expectedForegroundRevision) return;
        var snapshotIdentity = SnapshotIdentity(snapshot);
        // Deduplicate what the screen actually shows, including its interaction schema.
        // Changes to verbose progress alone must not flash the same short status again.
        var identity = snapshot.SessionId + "\n" + snapshot.State + "\n"
            + JsonSerializer.Serialize(snapshot.PendingInteractions) + "\n" + string.Join("\n", pages);
        if (pages.Count > 1) identity += "\n" + snapshotIdentity;
        if (pages.Count == 1 && lastSentIdentity == identity) return;
        if (lastIdentity == identity && pageDelivery?.Cancellation is { IsCancellationRequested: false }) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var previous = Interlocked.Exchange(ref pageDelivery, new PageDelivery(snapshotIdentity, cancellation));
        previous?.Cancellation.Cancel();
        previous?.Cancellation.Dispose();
        var pageToken = cancellation.Token;
        lastIdentity = identity;
        if (pages.Count == 0) return;
        if (SnapshotIdentity(App.DshTasks.Current) != snapshotIdentity) { cancellation.Cancel(); return; }
        try
        {
            if (await SendPageAsync(pages[0], expectedForegroundRevision, pageToken)) lastSentIdentity = identity;
            else { lastIdentity = string.Empty; lastSentIdentity = string.Empty; }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && pageToken.IsCancellationRequested)
        {
            // A newer task state superseded a page waiting in the device queue.
            return;
        }
        _ = ContinuePagesAsync(snapshotIdentity, pages, expectedForegroundRevision, pageToken);
    }

    private static async Task ContinuePagesAsync(string identity, IReadOnlyList<string> pages,
        long expectedForegroundRevision, CancellationToken token)
    {
        try
        {
            for (var index = 1; index < pages.Count; index++)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), token);
                if (SnapshotIdentity(App.DshTasks.Current) != identity) return;
                if (!await SendPageAsync(pages[index], expectedForegroundRevision, token)) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine("任务字幕分页失败：" + exception.Message); }
    }

    private static Task<bool> SendPageAsync(string text, long expectedForegroundRevision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!DisplayFeatureProfile.PixelScreenEnabled || DisplayFeatureProfile.LightsTurnedOffByAutomation)
            return Task.FromResult(false);
        return Display.SendTextAsync(new DisplayTextOptions
        {
            Text = FitSubtitle(text), Source = DisplayContentKind.TaskStatus,
            ExpectedForegroundRevision = expectedForegroundRevision,
            Layout = HaloPixelTextLayout.Center, ScrollDirection = TextScrollDirection.RightToLeft, Speed = 5
        }, token);
    }

    internal static string SpeechSummary(string text)
    {
        // Keep the spoken result to its leading outcome, rather than reading
        // tables, code, URLs and a long implementation report aloud.
        text = Regex.Replace(text ?? string.Empty, @"```[\s\S]*?```", " ");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"https?://\S+|file:///\S+", "");
        text = Regex.Replace(text, @"[`*#|\r\n\t]+", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        const int limit = 96;
        if (text.Length <= limit) return text;
        var prefix = string.Concat(text.EnumerateRunes().Take(limit).Select(rune => rune.ToString()));
        var boundary = prefix.LastIndexOfAny(['。', '！', '？', ';', '；', '.', '!', '?']);
        if (boundary >= 12) prefix = prefix[..(boundary + 1)];
        else prefix = prefix.TrimEnd('，', ',', '：', ':', ' ') + "…";
        return prefix + "详情在会话中。";
    }

    internal static string FitSubtitle(string text) => DshTaskSubtitleFormatter.Fit(text);
}
