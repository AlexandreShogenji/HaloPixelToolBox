using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Scenes;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.DeviceControl;
using HaloPixelToolBox.Core.Utilities;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    passed++;
}

DisplayTextOptions TaskPage(string text, long revision) => new()
{
    Text = text,
    Source = DisplayContentKind.TaskStatus,
    ExpectedForegroundRevision = revision
};

await DisplayStateProbe.RunAsync(Check);

var device = new HaloPixelDevice();
var display = new HaloPixelDisplayService(device);
var notifications = new List<(DisplayContentKind Kind, long Revision)>();
HaloPixelDisplayService.ContentSent += (_, args) => notifications.Add((args.ContentKind, HaloPixelDisplayService.ForegroundRevision));

// Every foreground source invalidates an older task lease before its observers run.
foreach (var source in Enum.GetValues<DisplayContentKind>().Where(kind => kind != DisplayContentKind.TaskStatus))
{
    var before = HaloPixelDisplayService.ForegroundRevision;
    Check(await display.SendTextAsync(new() { Text = source.ToString(), Source = source }), $"Foreground {source} failed.");
    Check(HaloPixelDisplayService.ForegroundRevision == before + 1, $"Foreground {source} did not advance revision once.");
    Check(notifications[^1] == (source, before + 1), $"Observers of {source} saw an outdated revision.");
}

var obsoleteLease = HaloPixelDisplayService.ForegroundRevision;
Check(await display.ShowScreenSceneAsync(1, 2, 3, 4), "Scene switch failed.");
Check(HaloPixelDisplayService.ForegroundRevision == obsoleteLease + 1, "Scene switch must advance foreground revision.");
var writesAfterScene = device.Writes.Count;
var notificationsAfterScene = notifications.Count;
Check(!await display.SendTextAsync(TaskPage("old task", obsoleteLease)), "A scene must invalidate the old task lease.");
Check(device.Writes.Count == writesAfterScene, "Rejected task touched the physical text layout or content.");
Check(notifications.Count == notificationsAfterScene && HaloPixelDisplayService.LastContentSent?.ContentKind == DisplayContentKind.Scene,
    "Rejected task changed the published display owner.");

var validLease = HaloPixelDisplayService.ForegroundRevision;
Check(await display.SendTextAsync(TaskPage("page one", validLease)), "A valid task lease failed.");
Check(await display.SendTextAsync(TaskPage("page two", validLease)), "A task page must not invalidate its next page.");
Check(HaloPixelDisplayService.ForegroundRevision == validLease, "Task status advanced foreground revision.");
var longCue = new SubtitleCue { Text = new string('甲', 30) };
var beforePages = device.Writes.Count;
Check(await display.SendSubtitleCueAsync(longCue, TaskPage(string.Empty, validLease)), "A valid lease failed across cloned subtitle options.");
Check(device.Writes.Count == beforePages + 4 && HaloPixelDisplayService.ForegroundRevision == validLease,
    "A two-page task subtitle must share its lease.");

// Change foreground after page one, while the real segment sender is delaying.
// The event schedules the new foreground into the real queue before page two.
Task<bool>? foregroundDuringPages = null;
EventHandler<DisplayContentChangedEventArgs> interruptPagination = (_, args) =>
{
    if (args.ContentKind == DisplayContentKind.TaskStatus && foregroundDuringPages is null)
        foregroundDuringPages = display.ShowBuiltInUiAsync(HaloPixelUIModel.Clock, DisplayContentKind.Clock);
};
HaloPixelDisplayService.ContentSent += interruptPagination;
beforePages = device.Writes.Count;
Check(!await display.SendSubtitleCueAsync(longCue, TaskPage(string.Empty, HaloPixelDisplayService.ForegroundRevision)),
    "A new foreground must reject later cloned task pages.");
HaloPixelDisplayService.ContentSent -= interruptPagination;
Check(foregroundDuringPages is not null && await foregroundDuringPages, "Foreground did not execute between pages.");
Check(device.Writes.Count == beforePages + 3, "A stale cloned page reached the device after a foreground switch.");

// Queue a stale task behind an in-flight foreground write. A check performed
// before enqueueing would incorrectly accept this task while the lease is valid.
using (var sceneEntered = new ManualResetEventSlim())
using (var releaseScene = new ManualResetEventSlim())
{
    obsoleteLease = HaloPixelDisplayService.ForegroundRevision;
    device.BeforeScreenSceneWrite = () =>
    {
        sceneEntered.Set();
        if (!releaseScene.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Test did not release the scene write.");
    };
    var scene = display.ShowScreenSceneAsync(4, 3, 2, 1);
    Check(sceneEntered.Wait(TimeSpan.FromSeconds(5)), "Foreground did not acquire the physical queue.");
    var queuedTask = display.SendTextAsync(TaskPage("queued old task", obsoleteLease));
    Check(!queuedTask.IsCompleted, "The task bypassed the device queue.");
    releaseScene.Set();
    Check(await scene, "Blocked foreground write failed.");
    Check(!await queuedTask, "An old task queued before foreground completion overwrote the new scene.");
    Check(device.Writes.Last() == "scene:4-3-2-1", "Physical write order ended in a stale task.");
    device.BeforeScreenSceneWrite = null;
}

// Queue cancellation must release the gate and never perform a pending write.
using (var queueEntered = new ManualResetEventSlim())
using (var releaseQueue = new ManualResetEventSlim())
using (var cancellation = new CancellationTokenSource())
{
    var blocker = HaloPixelDeviceOperationQueue.RunAsync(() =>
    {
        queueEntered.Set();
        if (!releaseQueue.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Test did not release queue blocker.");
        return true;
    });
    Check(queueEntered.Wait(TimeSpan.FromSeconds(5)), "Queue blocker did not start.");
    var beforeCancelled = device.Writes.Count;
    var cancelled = display.SendTextAsync(TaskPage("cancelled task", HaloPixelDisplayService.ForegroundRevision), cancellation.Token);
    cancellation.Cancel();
    try
    {
        await cancelled;
        throw new InvalidOperationException("Queued cancellation was ignored.");
    }
    catch (OperationCanceledException) { passed++; }
    finally { releaseQueue.Set(); }
    await blocker;
    Check(device.Writes.Count == beforeCancelled, "A cancelled queued subtitle touched the device.");
    Check(await display.SendTextAsync(TaskPage("after cancellation", HaloPixelDisplayService.ForegroundRevision)), "Cancellation stranded the queue gate.");
}

// Missing/failed device writes and preview-only snapshots must not claim display.
var failedDevice = new HaloPixelDevice { CurrentDevice = null, InitializeResult = false };
var failedDisplay = new HaloPixelDisplayService(failedDevice);
var beforeFailure = HaloPixelDisplayService.ForegroundRevision;
Check(!await failedDisplay.SendTextAsync(new() { Text = "offline" }), "An absent device reported a successful write.");
Check(HaloPixelDisplayService.ForegroundRevision == beforeFailure, "A failed foreground advanced revision.");
Check(!await failedDisplay.SendTextAsync(TaskPage("stale", beforeFailure - 1)), "An offline stale lease was accepted.");
Check(failedDevice.InitializeCalls == 1, "A stale lease should be rejected before even opening the device.");
device.ThrowOnText = true;
try
{
    await display.SendTextAsync(new() { Text = "write failure" });
    throw new InvalidOperationException("Simulated write failure was not surfaced.");
}
catch (IOException) { passed++; }
finally { device.ThrowOnText = false; }
Check(HaloPixelDisplayService.ForegroundRevision == beforeFailure, "An exception before content completion advanced revision.");
_ = HaloPixelDisplayService.CreateScenePreviewSnapshot(new PersonalSceneDefinition { Name = "preview only" });
Check(HaloPixelDisplayService.ForegroundRevision == beforeFailure, "A UI snapshot claimed the physical foreground.");

// Ordinary callers that do not opt into a lease preserve the previous behavior.
Check(await display.SendTextAsync(new() { Text = "ordinary subtitle", Source = DisplayContentKind.Custom }), "An ordinary subtitle without a lease was rejected.");
Check(device.Writes.Last() == "text:ordinary subtitle" && HaloPixelDisplayService.ForegroundRevision == beforeFailure + 1,
    "An ordinary subtitle did not replace the foreground normally.");
var beforeLegacyTask = HaloPixelDisplayService.ForegroundRevision;
Check(await display.SendTextAsync(new() { Text = "legacy task", Source = DisplayContentKind.TaskStatus }), "An unleased task broke backward compatibility.");
Check(HaloPixelDisplayService.ForegroundRevision == beforeLegacyTask, "An unleased task changed foreground revision.");

Console.WriteLine($"{passed} display ownership checks passed using the real display service and device queue; no hardware or user data accessed.");
