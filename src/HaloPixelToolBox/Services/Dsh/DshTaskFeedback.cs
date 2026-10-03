using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

internal static class DshTaskFeedback
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HaloPixelDisplayService Display = new();
    private static CancellationTokenSource? pageCancellation;
    private static string lastIdentity = string.Empty;
    private static bool observing;
    private static string attentionIdentity = string.Empty;

    public static async Task PublishAsync(DshTaskSnapshot snapshot, string cue, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            ObserveTaskChanges();
            token.ThrowIfCancellationRequested();
            if (App.DshTasks.Current.SessionId != snapshot.SessionId || !App.DshTasks.Current.IsMonitoring) return;
            if (cue == "task_started" && App.LyricsSubtitleControl.CurrentStatus.IsRunning)
                await App.LyricsSubtitleControl.StopAsync(restoreScene: false, cancellationToken: token);
            var detail = snapshot.NeedsAttention ? snapshot.Detail
                : snapshot.State == "completed" ? snapshot.FinalText : snapshot.Detail;
            var text = snapshot.NeedsAttention ? detail : snapshot.StatusText + (string.IsNullOrWhiteSpace(detail) ? "" : "：" + detail);
            await BeginPagesAsync(snapshot, text, token);
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
    public static async Task PublishVoiceReplyAsync(string text, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try { ObserveTaskChanges(); await BeginPagesAsync(App.DshTasks.Current, text, token); }
        finally { Gate.Release(); }
    }

    private static void ObserveTaskChanges()
    {
        if (observing) return;
        observing = true;
        attentionIdentity = AttentionIdentity(App.DshTasks.Current);
        App.DshTasks.Changed += (_, snapshot) =>
        {
            var next = AttentionIdentity(snapshot);
            var previous = Interlocked.Exchange(ref attentionIdentity, next);
            if (previous.Length > 0 && previous != next) _ = InvalidatePromptAsync();
            if (!snapshot.IsMonitoring)
            {
                try { pageCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            }
        };
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
        => snapshot.SessionId + "\n" + snapshot.State + "\n" + snapshot.Detail + "\n" + JsonSerializer.Serialize(snapshot.PendingInteractions);

    private static async Task BeginPagesAsync(DshTaskSnapshot snapshot, string text, CancellationToken token)
    {
        var snapshotIdentity = SnapshotIdentity(snapshot);
        var identity = snapshotIdentity + "\n" + text;
        if (lastIdentity == identity && pageCancellation is { IsCancellationRequested: false }) return;
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pageToken = pageCancellation.Token;
        lastIdentity = identity;
        var pages = DshSpokenInteraction.BuildSubtitlePages(text, 45);
        if (pages.Count == 0) return;
        if (!await SendPageAsync(pages[0], 0, pages.Count, pageToken)) lastIdentity = string.Empty;
        _ = ContinuePagesAsync(snapshotIdentity, pages, pageToken);
    }

    private static async Task ContinuePagesAsync(string identity, IReadOnlyList<string> pages, CancellationToken token)
    {
        try
        {
            for (var index = 1; index < pages.Count; index++)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), token);
                if (SnapshotIdentity(App.DshTasks.Current) != identity) return;
                await SendPageAsync(pages[index], index, pages.Count, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine("任务字幕分页失败：" + exception.Message); }
    }

    private static Task<bool> SendPageAsync(string text, int index, int total, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!DisplayFeatureProfile.PixelScreenEnabled || DisplayFeatureProfile.LightsTurnedOffByAutomation)
            return Task.FromResult(false);
        return Display.SendTextAsync(new DisplayTextOptions
        {
            Text = FitSubtitle(total > 1 ? $"{index + 1}/{total} {text}" : text), Source = DisplayContentKind.TaskStatus,
            Layout = HaloPixelTextLayout.Center, ScrollDirection = TextScrollDirection.RightToLeft, Speed = 5
        }, token);
    }

    private static string SpeechSummary(string text)
    {
        text = Regex.Replace(text, @"[`*#\r\n\t]+", " ").Trim();
        return text.Length <= 160 ? text : text[..160] + "。完整结果已保存在会话，可说朗读任务结果。";
    }

    internal static string FitSubtitle(string text)
    {
        text = Regex.Replace(text, @"[`*#\r\n\t]+", " ").Trim();
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 55) break;
            result.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
