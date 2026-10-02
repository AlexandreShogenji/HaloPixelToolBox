using HaloPixelToolBox.Models;

namespace HaloPixelToolBox.Profiles.CrossVersionProfiles
{
    public static class DisplayFeatureProfile { public static string DshProfileName { get; set; } = "default"; public static string DshTaskRootDirectory { get; set; } = @"C:\TestTasks"; }
}

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum HorizontalAlignment { Left, Center, Right, Stretch }
    public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
    {
        public Thickness(double all) : this(all, all, all, all) { }
    }
}
namespace Microsoft.UI.Dispatching
{
    public enum DispatcherQueuePriority { Low, Normal, High }
    public sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRepeating { get; set; }
        public bool IsRunning { get; private set; }
        public event EventHandler<object>? Tick;
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Fire(bool evenIfStopped = false)
        {
            if (!IsRunning && !evenIfStopped) return;
            IsRunning = false;
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }
    public sealed class DispatcherQueue
    {
        private static readonly Queue<Action> callbacks = new();
        public static bool ForceQueue { get; set; }
        public static DispatcherQueue GetForCurrentThread() => new();
        public bool HasThreadAccess => !ForceQueue;
        public bool TryEnqueue(Action action) { if(ForceQueue) callbacks.Enqueue(action);else action(); return true; }
        public bool TryEnqueue(DispatcherQueuePriority priority, Action action) { callbacks.Enqueue(action); return true; }
        public DispatcherQueueTimer CreateTimer() => new();
        public static void Drain() { while (callbacks.TryDequeue(out var callback)) callback(); }
    }
}
namespace HaloPixelToolBox
{
    public static class App { public static SessionServiceStub DshSessions { get; } = new(); public static TaskServiceStub DshTasks { get; } = new(); public static VoiceServiceStub VoiceAgent { get; } = new(); }
    public sealed class SessionServiceStub
    {
        public DshSessionsSnapshot Current { get; private set; } = DshSessionsSnapshot.Initial;
        public event EventHandler<DshSessionsSnapshot>? Changed;
        public Func<string, long?, CancellationToken, Task<DshHistoryPage>> HistoryReader = (_, _, _) => Task.FromResult(new DshHistoryPage([], null, false));
        public Func<IReadOnlyList<string>, string?, CancellationToken, Task<DshDeviceHistoryPage>> DeviceHistoryReader = async (ids, cursor, ct) =>
        {
            var page = await App.DshSessions.HistoryReader(ids[0], cursor is null ? null : long.Parse(cursor), ct);
            return new(page.Entries.Select(entry => entry with { SessionId = ids[0] }).ToArray(), page.BeforeSeq?.ToString(), page.HasMore, page.Truncated);
        };
        public int TargetSelections;
        public int Disconnects;
        public Func<string, CancellationToken, Task<DshDeviceCommandResult>> DeviceSender = (_, _) => Task.FromResult(new DshDeviceCommandResult(true, "done", "done", "DEVICE", [], []) { Accepted = true, Completed = true });
        public Func<string, string, CancellationToken, Task<DshDeviceCommandResult>> MessageSender = (id, _, _) => Task.FromResult(new DshDeviceCommandResult(true, "done", "done", id, [], []) { Accepted = true, Completed = true });
        public Func<string, CancellationToken, Task<DshSessionSummary>> SessionCreator = (_, _) => throw new NotImplementedException();
        public Func<string, string, CancellationToken, Task> SessionRenamer = (_, _, _) => throw new NotImplementedException();
        public Func<string, bool, CancellationToken, Task> SessionArchiver = (_, _, _) => throw new NotImplementedException();
        public void Publish(DshSessionsSnapshot snapshot) { Current = snapshot; Changed?.Invoke(this, snapshot); }
        public Task ConnectAsync(CancellationToken ct) { Publish(Current with { IsConnected = true, HostUrl = "http://127.0.0.1:8765", Message = "connected" }); return Task.CompletedTask; }
        public Task RefreshAsync(CancellationToken ct) { Publish(Current); return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken ct) { Disconnects++; return Task.CompletedTask; }
        public Task<DshHistoryPage> ReadHistoryAsync(string id, long? before, CancellationToken ct) => HistoryReader(id, before, ct);
        public Task<DshDeviceHistoryPage> ReadDeviceHistoryAsync(IReadOnlyList<string> ids, string? cursor, CancellationToken ct) => DeviceHistoryReader(ids, cursor, ct);
        public Task<DshDeviceCommandResult> ExecuteDeviceCommandAsync(string text, int timeoutSeconds = 120, CancellationToken cancellationToken = default) => DeviceSender(text, cancellationToken);
        public Task<DshDeviceCommandResult> SendMessageAsync(string id, string text, CancellationToken ct) => MessageSender(id, text, ct);
        public Task<DshSessionSummary> CreateSessionAsync(string title, CancellationToken ct) => SessionCreator(title, ct);
        public Task RenameSessionAsync(string id, string title, CancellationToken ct) => SessionRenamer(id, title, ct);
        public Task SetSessionArchivedAsync(string id, bool archived, CancellationToken ct) => SessionArchiver(id, archived, ct);
        public void SelectVoiceTarget(DshSessionSummary? summary) { TargetSelections++; Publish(Current with { VoiceTarget = summary }); }
        public void ClearVoiceTarget() => Publish(Current with { VoiceTarget = null });
    }
}
