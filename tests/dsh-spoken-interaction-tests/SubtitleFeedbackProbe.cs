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
        async Task Claim(DshTaskSnapshot snapshot)
        {
            App.DshTasks.Publish(snapshot);
            await DshTaskFeedback.PublishAsync(snapshot with { DisplayRequestRevision = HaloPixelDisplayService.ForegroundRevision },
                DshTaskFeedback.RequestDisplayCue, CancellationToken.None);
        }

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
        await Claim(waiting);
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
        await Claim(running);
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
        await Claim(finalFailure);
        await DshTaskFeedback.PublishAsync(finalFailure, "", CancellationToken.None);
        Assert(HaloPixelDisplayService.Sent.Count == 3,
            "explicitly resuming monitoring can acquire the display again");

        // Suppressed output must remain retryable when the device is available again.
        ClearLogs();
        var screenOff = Snapshot("screen-off-session", "running", "检查设备设置");
        await Claim(screenOff);
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
        await Claim(attention);
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

        ClearLogs();
        var protectedTask = Snapshot("manual-clock", "running", "执行任务");
        await Claim(protectedTask);
        await DshTaskFeedback.PublishAsync(protectedTask, "", CancellationToken.None);
        HaloPixelDisplayService.Foreground();
        ClearLogs();
        var finished = protectedTask with { State = "completed", FinalText = "已完成", StatusText = "任务完成" };
        App.DshTasks.Publish(finished);
        await DshTaskFeedback.PublishAsync(finished, "task_completed", CancellationToken.None);
        Assert(HaloPixelDisplayService.Attempted.IsEmpty, "completion does not steal a manually selected clock");
        Assert(App.VoiceAgent.Spoken.Count == 1, "suppressed screen completion still plays its voice notification");
        var needsApproval = finished with { State = "waitingApproval", PendingInteractions = [new("approve", "approval", "shell", "执行命令", [])] };
        App.DshTasks.Publish(needsApproval);
        await DshTaskFeedback.PublishAsync(needsApproval, "approval_required", CancellationToken.None);
        Assert(HaloPixelDisplayService.Attempted.IsEmpty && App.VoiceAgent.Spoken.Last().ListenAfter,
            "approval keeps the clock and still provides spoken interaction");
        await DshTaskFeedback.PublishVoiceReplyAsync("已切换时钟", CancellationToken.None);
        Assert(HaloPixelDisplayService.Attempted.IsEmpty, "successful device reply cannot replace its own scene");
        var beforeCommand = HaloPixelDisplayService.ForegroundRevision;
        HaloPixelDisplayService.Foreground();
        await DshTaskFeedback.PublishVoiceReplyAsync("任务已提交", CancellationToken.None, true, beforeCommand);
        Assert(HaloPixelDisplayService.Attempted.IsEmpty, "late voice reply cannot overwrite a newer manual screen choice");

        // A persisted monitor is background observation, not renewed screen consent.
        var restored = Snapshot("restored-task", "completed", "恢复旧任务");
        App.DshTasks.Publish(restored);
        await DshTaskFeedback.PublishAsync(restored, "task_completed", CancellationToken.None);
        Assert(HaloPixelDisplayService.Attempted.IsEmpty, "automatic restore does not claim the display");
        await Claim(restored);
        await DshTaskFeedback.PublishAsync(restored, "", CancellationToken.None);
        Assert(HaloPixelDisplayService.Sent.Count == 1, "explicit task action can acquire display after manual override");

        ClearLogs();
        var queuedTask = Snapshot("scene-during-queue", "running", "等待显示");
        await Claim(queuedTask);
        var blockedByScene = new FeedbackBlockedSend();
        HaloPixelDisplayService.BlockNext = blockedByScene;
        var delayed = DshTaskFeedback.PublishAsync(queuedTask, "", CancellationToken.None);
        await blockedByScene.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        HaloPixelDisplayService.Foreground();
        await blockedByScene.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await delayed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(HaloPixelDisplayService.Sent.IsEmpty, "manual display immediately cancels already queued task pages");

        // The request carries the foreground from the beginning of an explicit
        // task action, before creating/adopting its remote session could await.
        ClearLogs();
        var lateClaim = Snapshot("delayed-display-claim", "running", "创建会话已返回");
        App.DshTasks.Publish(lateClaim);
        var requestedRevision = HaloPixelDisplayService.ForegroundRevision;
        HaloPixelDisplayService.Foreground();
        App.LyricsSubtitleControl.CurrentStatus = (true, false);
        var stopsBeforeClaim = App.LyricsSubtitleControl.StopCalls;
        await DshTaskFeedback.PublishAsync(lateClaim with { DisplayRequestRevision = requestedRevision },
            DshTaskFeedback.RequestDisplayCue, CancellationToken.None);
        await DshTaskFeedback.PublishAsync(lateClaim, "task_started", CancellationToken.None);
        Assert(HaloPixelDisplayService.Attempted.IsEmpty, "a delayed explicit task action cannot claim a foreground chosen while it awaited");
        Assert(App.LyricsSubtitleControl.StopCalls == stopsBeforeClaim && App.LyricsSubtitleControl.CurrentStatus.IsRunning,
            "rejecting an old display request does not stop current lyrics");
        await DshTaskFeedback.PublishAsync(lateClaim, DshTaskFeedback.RequestDisplayCue, CancellationToken.None);
        Assert(App.LyricsSubtitleControl.StopCalls == stopsBeforeClaim, "a display request without a captured revision cannot stop lyrics or claim a screen");

        // Stopping the old lyrics source is itself asynchronous. A fresh scene
        // chosen during that await must still invalidate the original request.
        ClearLogs();
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        App.LyricsSubtitleControl.BeforeStop = async token =>
        {
            stopEntered.TrySetResult();
            await releaseStop.Task.WaitAsync(token);
        };
        var delayedClaim = DshTaskFeedback.PublishAsync(lateClaim with { DisplayRequestRevision = HaloPixelDisplayService.ForegroundRevision },
            DshTaskFeedback.RequestDisplayCue, CancellationToken.None);
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        HaloPixelDisplayService.Foreground();
        releaseStop.TrySetResult();
        await delayedClaim.WaitAsync(TimeSpan.FromSeconds(5));
        App.LyricsSubtitleControl.BeforeStop = null;
        await DshTaskFeedback.PublishAsync(lateClaim, "", CancellationToken.None);
        Assert(HaloPixelDisplayService.Attempted.IsEmpty, "a scene chosen while stopping lyrics prevents reacquiring its new revision");
        Assert(App.LyricsSubtitleControl.StopCalls == stopsBeforeClaim + 1, "a valid display request stops the prior lyrics exactly once");
        await Claim(lateClaim);
        await DshTaskFeedback.PublishAsync(lateClaim, "", CancellationToken.None);
        Assert(HaloPixelDisplayService.Sent.Count == 1, "a later explicit task action with a new revision can still claim the display");
    }
}
