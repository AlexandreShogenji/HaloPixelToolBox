using System.Threading.Channels;
using HaloPixelToolBox.Models;

internal static class PollingProbe
{
    private static void Check(bool value, string message) => ProbeAssert.Check(value, message);

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("stable terminal and empty tasks back off to 15 seconds without ending monitoring", async () =>
        {
            foreach (var state in new[] { "idle", "completed", "failed", "cancelled", "awaitingPrompt" })
            {
                var clock = new MonitorClock();
                using var f = new TaskCase(clock, TimeSpan.FromSeconds(1));
                var wait = await ReachIdleCapAsync(f, clock, state);
                var reads = f.Client.ReadCount;
                for (var i = 0; i < 3; i++)
                {
                    wait.Fire();
                    wait = await clock.NextAsync(15);
                }
                Check(f.Client.ReadCount == reads + 3 && f.Service.Current.IsMonitoring,
                    state + " stopped monitoring or polled beyond the scheduled interval");
            }
        });

        await test("running questions and approvals keep the fast polling cadence", async () =>
        {
            foreach (var state in new[] { "running", "waitingInput", "waitingApproval" })
            {
                var clock = new MonitorClock();
                using var f = new TaskCase(clock, TimeSpan.FromSeconds(1));
                await f.Start();
                var wait = await clock.NextAsync(1);
                DshTaskInteraction[] pending = state == "waitingInput"
                    ? [new("question", "question", "ask", "", [new("answer", "名称", "文件名？", [])])]
                    : state == "waitingApproval" ? [new("approval", "approval", "exec", "write file", [])] : [];
                f.Client.SetRemote(state, "active", pending);
                for (var i = 0; i < 7; i++)
                {
                    wait.Fire();
                    wait = await clock.NextAsync(1);
                }
                Check(f.Service.Current.State == state, "fast poll lost the active task state");
            }
        });

        await test("continuation wakes an idle monitor immediately without duplicate submission", async () =>
        {
            var clock = new MonitorClock();
            using var f = new TaskCase(clock, TimeSpan.FromSeconds(1));
            var wait = await ReachIdleCapAsync(f, clock);
            var reads = f.Client.ReadCount;
            await f.Service.SendMessageAsync("继续当前任务");
            await clock.NextAsync(1);
            Check(wait.IsDisposed && f.Client.ReadCount == reads + 1 && f.Service.Current.State == "running",
                "new command waited for the idle timeout or retained the slow cadence");
            Check(f.Client.Prompts.Count == 2 && f.Client.Creates.Count == 1, "continuation replayed or created another task");
        });

        await test("failed continuation wakes status verification but never replays an uncertain command", async () =>
        {
            var clock = new MonitorClock();
            using var f = new TaskCase(clock, TimeSpan.FromSeconds(1));
            var wait = await ReachIdleCapAsync(f, clock);
            var reads = f.Client.ReadCount;
            f.Client.SubmitFailure = new IOException("reply unavailable");
            try { await f.Service.SendMessageAsync("继续任务"); }
            catch (IOException) { }
            await clock.NextAsync(1);
            Check(wait.IsDisposed && f.Client.ReadCount == reads + 1 && f.Client.Prompts.Count == 2,
                "uncertain submission was not checked promptly or was replayed");
        });

        await test("answer and cancel wake the monitor without waiting for the timer", async () =>
        {
            var clock = new MonitorClock();
            using var f = new TaskCase(clock, TimeSpan.FromSeconds(1));
            await f.Start();
            var wait = await clock.NextAsync(1);
            f.Client.SetRemote("waitingApproval", "approval", [new("a1", "approval", "exec", "write file", [])]);
            wait.Fire();
            wait = await clock.NextAsync(1);
            var reads = f.Client.ReadCount;
            await f.Service.RespondApprovalAsync("a1", false);
            var next = await clock.NextAsync(1);
            Check(wait.IsDisposed && f.Client.ReadCount == reads + 1 && f.Client.Responses.Count == 1,
                "answer did not immediately refresh or was replayed");
            reads = f.Client.ReadCount;
            await f.Service.CancelAsync();
            await clock.NextAsync(1);
            Check(next.IsDisposed && f.Client.ReadCount == reads + 1 && f.Client.CancelCount == 1,
                "cancel did not immediately refresh or was replayed");
        });

        await test("only meaningful changes to the monitored session wake idle polling", async () =>
        {
            var clock = new MonitorClock();
            using var f = new TaskCase(clock, TimeSpan.FromSeconds(1));
            var wait = await ReachIdleCapAsync(f, clock);
            var reads = f.Client.ReadCount;
            f.Client.Signal();
            var tracked = f.Client.Current.Sessions.Single();
            var unrelated = tracked with { Id = "other-task", UpdatedAt = tracked.UpdatedAt.AddMinutes(1) };
            f.Client.SetSessions([tracked with { Title = "renamed" }, unrelated]);
            Check(!wait.IsDisposed && f.Client.ReadCount == reads, "identical or unrelated list update defeated the backoff");
            f.Client.SetRemote("running", "external-resume", []);
            f.Client.SetSessions([tracked with { RuntimeStatus = "running", UpdatedAt = tracked.UpdatedAt.AddSeconds(1) }, unrelated]);
            await clock.NextAsync(1);
            Check(wait.IsDisposed && f.Client.ReadCount == reads + 1 && f.Service.Current.State == "running",
                "external session activity did not wake the monitor");
        });

        await test("unannounced remote activity is detected by the bounded idle poll", async () =>
        {
            var clock = new MonitorClock();
            using var f = new TaskCase(clock, TimeSpan.FromSeconds(1));
            var wait = await ReachIdleCapAsync(f, clock);
            f.Client.SetRemote("waitingInput", "external-question",
                [new("q", "question", "ask", "", [new("answer", "内容", "请回答", [])])]);
            wait.Fire();
            await clock.NextAsync(1);
            Check(f.Service.Current.NeedsAttention && f.Cues.Count(c => c == "input_required") == 1,
                "idle task missed an external question or failed to restore prompt feedback");
        });

        await test("activity signalled during a pending state read cannot lose its wake-up", async () =>
        {
            var clock = new MonitorClock();
            using var f = new TaskCase(clock, TimeSpan.FromSeconds(1));
            var wait = await ReachIdleCapAsync(f, clock);
            var reads = f.Client.ReadCount;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<DshTaskRemoteState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var before = f.Client.Remote;
            f.Client.ReadOverride = (_, ct) => { entered.TrySetResult(); return finish.Task.WaitAsync(ct); };
            wait.Fire();
            await entered.Task;
            f.Client.SetRemote("running", "external-during-read", []);
            var tracked = f.Client.Current.Sessions.Single();
            f.Client.SetSessions([tracked with { RuntimeStatus = "running", UpdatedAt = tracked.UpdatedAt.AddSeconds(1) }]);
            f.Client.ReadOverride = null;
            finish.SetResult(before);
            await clock.NextAsync(1);
            Check(f.Client.ReadCount == reads + 2 && f.Service.Current.State == "running",
                "activity during the previous read was deferred until the idle timeout");
        });

        await test("disconnect backoff stays bounded and reconnect wakes without replaying work", async () =>
        {
            var clock = new MonitorClock();
            using var f = new TaskCase(clock, TimeSpan.FromSeconds(1));
            var wait = await ReachIdleCapAsync(f, clock);
            f.Client.Disconnect();
            var next = await clock.NextAsync(2);
            Check(wait.IsDisposed, "connection loss did not wake idle polling");
            foreach (var seconds in new[] { 4, 8, 16, 30, 30 })
            {
                next.Fire();
                next = await clock.NextAsync(seconds);
            }
            f.Client.ServerAvailable = true;
            f.Client.SetRemote("running", "reconnected", []);
            await f.Client.RefreshAsync();
            await clock.NextAsync(1);
            Check(next.IsDisposed && f.Service.Current.State == "running" && f.Client.Prompts.Count == 1
                && f.Client.Creates.Count == 1 && f.Client.ConnectCount == 0,
                "reconnect kept the slow timer, restarted the host or replayed work");
        });

        await test("switching or stopping an idle monitor cancels its timer and cannot revive the old task", async () =>
        {
            var clock = new MonitorClock();
            using var f = new TaskCase(clock, TimeSpan.FromSeconds(1));
            var wait = await ReachIdleCapAsync(f, clock);
            var replacement = await f.Start();
            var next = await clock.NextAsync(1);
            Check(wait.IsDisposed && replacement.SessionId == "task-2" && f.Client.ReadCount > 0,
                "switch kept the old timer or failed to monitor the replacement immediately");
            await f.Service.StopMonitoringAsync();
            var reads = f.Client.ReadCount;
            next.Fire();
            wait.Fire();
            Check(next.IsDisposed && f.Client.ReadCount == reads && !f.Service.Current.IsMonitoring,
                "a cancelled monitor timer revived a task");
        });
    }

    private static async Task<MonitorClock.Wait> ReachIdleCapAsync(TaskCase f, MonitorClock clock, string state = "completed")
    {
        await f.Start();
        var wait = await clock.NextAsync(1);
        if (state == "awaitingPrompt")
        {
            f.Client.SetRemote("completed", "replace-empty", []);
            wait.Fire();
            wait = await clock.NextAsync(1);
            await f.Service.StartAsync(new("C:/test", "empty", ""));
            wait = await clock.NextAsync(1);
        }
        else
        {
            f.Client.SetRemote(state, "idle-state", []);
            wait.Fire();
            wait = await clock.NextAsync(1);
        }
        foreach (var seconds in new[] { 2, 4, 8, 15 })
        {
            wait.Fire();
            wait = await clock.NextAsync(seconds);
        }
        return wait;
    }
}

// Task.WaitAsync uses TimeProvider timers. These tests advance one scheduled tick
// explicitly, so backoff and immediate wakes are verified without wall-clock sleeps.
internal sealed class MonitorClock : TimeProvider
{
    private readonly Channel<Wait> waits = Channel.CreateUnbounded<Wait>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("Expected one-shot monitor wait.");
        var wait = new Wait(callback, state, dueTime);
        waits.Writer.TryWrite(wait);
        return wait;
    }

    public async Task<Wait> NextAsync(int seconds)
    {
        var wait = await waits.Reader.ReadAsync();
        ProbeAssert.Check(wait.Delay == TimeSpan.FromSeconds(seconds), $"Expected {seconds}s poll, got {wait.Delay.TotalSeconds}s.");
        return wait;
    }

    internal sealed class Wait(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        private int disposed;
        public TimeSpan Delay { get; } = delay;
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public void Fire() { if (Interlocked.Exchange(ref disposed, 1) == 0) callback(state); }
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
    }
}
