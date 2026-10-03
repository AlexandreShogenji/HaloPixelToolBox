using System.Text;
using HaloPixelToolBox;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Services;

internal static class SubtitleFeedbackProbe
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        void Assert(bool value, string name) => check(value, "subtitle feedback transport: " + name);
        void ClearLogs()
        {
            HaloPixelDisplayService.Sent.Clear();
            HaloPixelDisplayService.Attempted.Clear();
            App.VoiceAgent.Spoken.Clear();
        }
        DshTaskSnapshot Snapshot(string session, string state, string detail) =>
            new(session, "测试任务", "", state, state, detail, "", [], true, false);

        // An old notification may arrive after the observable task has already changed.
        var stale = Snapshot("stale-session", "running", "旧进度");
        var newer = stale with { State = "failed", Detail = "当前任务已失败" };
        App.DshTasks.Publish(newer);
        await DshTaskFeedback.PublishAsync(stale, "task_running", CancellationToken.None);
        Assert(HaloPixelDisplayService.Attempted.IsEmpty, "old snapshot is rejected before its first display attempt");
        Assert(App.VoiceAgent.Spoken.IsEmpty, "old snapshot cannot emit its cue after a newer state");

        // The fake device queue is deliberately blocked until the real Changed observer
        // cancels this page. No delay or manual release is used to make the test pass.
        ClearLogs();
        var waiting = Snapshot("queued-session", "running", "准备执行第一个步骤");
        App.DshTasks.Publish(waiting);
        var blocked = new FeedbackBlockedSend();
        HaloPixelDisplayService.BlockNext = blocked;
        var oldSend = DshTaskFeedback.PublishAsync(waiting, "task_running", CancellationToken.None);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failed = waiting with { State = "failed", Detail = "执行失败：命令返回错误", StatusText = "任务失败" };
        App.DshTasks.Publish(failed);
        await blocked.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await oldSend.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(HaloPixelDisplayService.Attempted.Count == 1 && HaloPixelDisplayService.Sent.IsEmpty,
            "Changed cancels an old queued page before physical send");
        Assert(App.VoiceAgent.Spoken.IsEmpty, "cancelled old display cannot speak its stale cue");
        await DshTaskFeedback.PublishAsync(failed, "task_failed", CancellationToken.None);
        Assert(HaloPixelDisplayService.Sent.Count == 1 && HaloPixelDisplayService.Sent.Single().Text.Contains("失败"),
            "new failure sends after cancelling old queue and releasing the gate");
        Assert(App.VoiceAgent.Spoken.Count == 1 && App.VoiceAgent.Spoken.Single().Cue == "task_failed",
            "only newest state emits the failure cue");

        // Verbose progress can change at every poll; its stable one-send summary should
        // not flash again once successfully delivered.
        ClearLogs();
        var running = Snapshot("dedup-session", "running", "正在准备任务");
        App.DshTasks.Publish(running);
        await DshTaskFeedback.PublishAsync(running, "", CancellationToken.None);
        var firstText = HaloPixelDisplayService.Sent.Single().Text;
        var longRunning = running with { Detail = string.Concat(Enumerable.Repeat("持续检查备忘录页面的输入验证、存储与搜索功能。", 15)) };
        App.DshTasks.Publish(longRunning);
        await DshTaskFeedback.PublishAsync(longRunning, "", CancellationToken.None);
        Assert(HaloPixelDisplayService.Sent.Count == 1 && HaloPixelDisplayService.Attempted.Count == 1,
            "same delivered short status is not resent when verbose detail changes");
        Assert(firstText == DshTaskSubtitleFormatter.ForTask(longRunning).Single(), "dedup compares equal rendered summaries");
        var finalFailure = longRunning with { State = "failed", Detail = "最终检查失败" };
        App.DshTasks.Publish(finalFailure);
        await DshTaskFeedback.PublishAsync(finalFailure, "", CancellationToken.None);
        Assert(HaloPixelDisplayService.Sent.Count == 2 && HaloPixelDisplayService.Sent.Last().Text.Contains("失败"),
            "changed failure state bypasses previous running-summary dedup");
        App.DshTasks.Publish(finalFailure with { IsMonitoring = false });
        App.DshTasks.Publish(finalFailure);
        await DshTaskFeedback.PublishAsync(finalFailure, "", CancellationToken.None);
        Assert(HaloPixelDisplayService.Sent.Count == 3,
            "resuming monitoring sends status again after another scene could have restored the screen");

        // Suppressed output must remain retryable when the device is available again.
        ClearLogs();
        var screenOff = Snapshot("screen-off-session", "running", "检查设备设置");
        App.DshTasks.Publish(screenOff);
        DisplayFeatureProfile.PixelScreenEnabled = false;
        try
        {
            await DshTaskFeedback.PublishAsync(screenOff, "", CancellationToken.None);
            Assert(HaloPixelDisplayService.Attempted.IsEmpty, "disabled screen is never sent a task status");
        }
        finally { DisplayFeatureProfile.PixelScreenEnabled = true; }
        await DshTaskFeedback.PublishAsync(screenOff, "", CancellationToken.None);
        Assert(HaloPixelDisplayService.Sent.Count == 1, "suppressed send is not incorrectly marked already delivered");

        // Structured screen summaries and complete spoken questions have independent
        // length budgets. Use a long real question prompt rather than a fake summary.
        ClearLogs();
        var question = new DshTaskQuestion("theme", "选择外观", "请选择你希望备忘录使用的外观，同时确认是否需要保留现有的颜色设置。", [
            new("浅色纸张", "适合白天"), new("深色夜间", "适合晚上"), new("保留现有配色", "不改变颜色")]);
        var fullPrompt = DshSpokenInteraction.BuildQuestionPrompt(question, 0, 1);
        var request = new DshTaskInteraction("feedback-question", "question", "ask_question", "", [question]);
        var attention = Snapshot("speech-session", "waitingForInput", fullPrompt) with { PendingInteractions = [request] };
        App.DshTasks.Publish(attention);
        await DshTaskFeedback.PublishAsync(attention, "task_question", CancellationToken.None);
        var displayed = HaloPixelDisplayService.Sent.Single().Text;
        var spoken = App.VoiceAgent.Spoken.Single();
        Assert(Encoding.UTF8.GetByteCount(displayed) <= 55 && displayed != fullPrompt,
            "first device send is bounded summary independent of the long spoken prompt");
        Assert(spoken.Text == fullPrompt && spoken.Text.Contains("3：保留现有配色"),
            "voice retains complete original question and all options");
        Assert(spoken.ListenAfter && ReferenceEquals(spoken.Snapshot, attention),
            "full spoken question retains its exact task context and answer listening");
        var previousInvalidations = App.VoiceAgent.Invalidations;
        App.DshTasks.Publish(attention with { PendingInteractions = [], Detail = "答案已提交", State = "running" });
        Assert(App.VoiceAgent.Invalidations == previousInvalidations + 1, "removing question invalidates old voice prompt and cancels later pages");
        Assert(HaloPixelDisplayService.Sent.All(item => Encoding.UTF8.GetByteCount(item.Text) <= 55), "every emitted test packet respects the device byte limit");
    }
}
