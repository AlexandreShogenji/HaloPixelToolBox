using System.Collections.Concurrent;
using System.Reflection;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services;

internal static class DisplayRevisionProbe
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("create preserves display revision captured before remote creation", async () =>
        {
            using var f = new Fixture();
            var blocked = new Pause();
            f.Client.BeforeCreate = blocked.WaitAsync;
            f.Revision = 100;
            var create = f.Service.StartAsync(new("C:/tasks", "test", ""));
            await blocked.Entered.Task;
            f.Revision = 101;
            blocked.Release.TrySetResult();
            await create;
            Check(f.Claims.Single().DisplayRequestRevision == 100, "Create claimed the scene selected while its server request was pending.");
            Check(f.Service.Current.DisplayRequestRevision is null, "A one-shot display request leaked into observable task state.");
        });

        await test("monitor preserves display revision captured before initial state read", async () =>
        {
            using var f = new Fixture();
            var blocked = new Pause();
            f.Client.BeforeRead = blocked.WaitAsync;
            f.Revision = 200;
            var monitor = f.Service.MonitorAsync(f.Existing);
            await blocked.Entered.Task;
            f.Revision = 201;
            blocked.Release.TrySetResult();
            await monitor;
            Check(f.Claims.Single().DisplayRequestRevision == 200, "Monitor claimed the scene chosen while reading its remote state.");
            Check(f.Service.Current.DisplayRequestRevision is null, "Monitor stored a temporary display lease in Current.");
        });

        await test("voice route keeps its original display revision through context adoption", async () =>
        {
            using var f = new Fixture();
            f.Client.Inner.SelectVoiceTarget(f.Existing);
            var blocked = new Pause();
            f.Client.BeforeRead = blocked.WaitAsync;
            f.Revision = 300;
            var route = f.Service.RouteVoiceAsync("任务内容：读取 README 并总结");
            await blocked.Entered.Task;
            f.Revision = 301;
            blocked.Release.TrySetResult();
            var result = await route;
            Check(result.Handled && f.Client.Inner.Prompts.Count == 1, "Voice continuation failed before requesting display.");
            Check(f.Claims.Single().DisplayRequestRevision == 300, "Adopting voice context replaced the original display intent with a newer scene.");
            await f.Service.SendMessageAsync("下一轮直接输入");
            Check(f.Claims.Last().DisplayRequestRevision == 301, "A completed voice turn leaked its old revision into a later direct action.");
        });

        await test("manual continuation captures display before waiting for the action gate", async () =>
        {
            using var f = new Fixture();
            await f.Service.StartAsync(new("C:/tasks", "test", ""));
            var gate = ActionGate(f.Service);
            await gate.WaitAsync();
            f.Revision = 400;
            var send = f.Service.SendMessageAsync("继续任务");
            f.Revision = 401;
            gate.Release();
            await send;
            Check(f.Claims.Last().DisplayRequestRevision == 400, "A queued continuation acquired a foreground selected while waiting for the action lock.");
        });

        await test("approval response captures display before waiting for the action gate", async () =>
        {
            using var f = new Fixture();
            f.Client.Inner.SetRemote("waitingApproval", "approval", [new("a1", "approval", "exec", "write file", [])]);
            await f.Service.MonitorAsync(f.Existing);
            var gate = ActionGate(f.Service);
            await gate.WaitAsync();
            f.Revision = 500;
            var respond = f.Service.RespondApprovalAsync("a1", false);
            f.Revision = 501;
            gate.Release();
            await respond;
            Check(f.Claims.Last().DisplayRequestRevision == 500, "A queued approval response acquired the scene selected while waiting.");
            Check(f.Client.Inner.Responses.Count == 1, "Approval response did not complete exactly once.");
        });
    }

    private static void Check(bool condition, string message) => ProbeAssert.Check(condition, message);
    private static SemaphoreSlim ActionGate(DshTaskService service)
        => (SemaphoreSlim)typeof(DshTaskService).GetField("actions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;

    private sealed class Pause
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public long Revision;
        public DelayedClient Client { get; } = new();
        public DshTaskService Service { get; }
        public ConcurrentQueue<DshTaskSnapshot> Claims { get; } = new();
        public DshSessionSummary Existing { get; } = new("task-1", "test", "C:/tasks/test", DateTimeOffset.Now, "idle");
        public Fixture()
        {
            Service = new(Client, () => "C:/tasks", () => "display-lease-test",
                feedback: (snapshot, cue, _) =>
                {
                    if (cue == "task_display_requested") Claims.Enqueue(snapshot);
                    return Task.CompletedTask;
                },
                stateRoot: Path.Combine(Path.GetTempPath(), "halo-display-lease-test-" + Guid.NewGuid().ToString("N")),
                pollInterval: TimeSpan.FromMinutes(1), displayRevisionProvider: () => Revision);
        }
        public void Dispose() => Service.Dispose();
    }

    private sealed class DelayedClient : IDshTaskSessionClient
    {
        public FakeTaskClient Inner { get; } = new();
        public Func<CancellationToken, Task>? BeforeCreate;
        public Func<CancellationToken, Task>? BeforeRead;
        public DshSessionsSnapshot Current => Inner.Current;
        public event EventHandler<DshSessionsSnapshot>? Changed { add => Inner.Changed += value; remove => Inner.Changed -= value; }
        public Task RefreshAsync(CancellationToken token = default) => Inner.RefreshAsync(token);
        public Task ConnectAsync(CancellationToken token = default) => Inner.ConnectAsync(token);
        public async Task<DshSessionSummary> CreateTaskSessionAsync(DshTaskStartRequest request, CancellationToken token = default)
        {
            if (Interlocked.Exchange(ref BeforeCreate, null) is { } before) await before(token);
            return await Inner.CreateTaskSessionAsync(request, token);
        }
        public Task AdoptTaskSessionAsync(string id, CancellationToken token = default) => Inner.AdoptTaskSessionAsync(id, token);
        public Task<DshTaskSubmission> SubmitTaskPromptAsync(string id, string prompt, CancellationToken token = default)
            => Inner.SubmitTaskPromptAsync(id, prompt, token);
        public async Task<DshTaskRemoteState> ReadTaskStateAsync(string id, CancellationToken token = default)
        {
            if (Interlocked.Exchange(ref BeforeRead, null) is { } before) await before(token);
            return await Inner.ReadTaskStateAsync(id, token);
        }
        public Task RespondTaskInteractionAsync(string id, string interaction, string type, string? outcome,
            IReadOnlyDictionary<string, string>? answers, CancellationToken token = default)
            => Inner.RespondTaskInteractionAsync(id, interaction, type, outcome, answers, token);
        public Task CancelTaskSessionAsync(string id, CancellationToken token = default) => Inner.CancelTaskSessionAsync(id, token);
        public Task ReleaseTaskSessionAsync(string id, CancellationToken token = default) => Inner.ReleaseTaskSessionAsync(id, token);
        public void SelectVoiceTarget(DshSessionSummary session) => Inner.SelectVoiceTarget(session);
        public void ClearVoiceTarget() => Inner.ClearVoiceTarget();
    }
}
