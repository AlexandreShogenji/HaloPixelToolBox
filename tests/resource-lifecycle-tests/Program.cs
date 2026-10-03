using HaloPixelToolBox.Core.Services.Device;
using HaloPixelToolBox.Services;

// Run in separate processes for both startup states; production singleton lifetime
// is preserved, with only the UI clock and physical-device boundary substituted.
var hiddenAtStart = args.Contains("--hidden-start", StringComparer.Ordinal);
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    passed++;
}

Check(WindowActivityService.IsVisible, "A host without a window should initially permit UI work.");
var visibilityEvents = 0;
bool? lastVisibility = null;
WindowActivityService.VisibilityChanged += (_, visible) =>
{
    visibilityEvents++;
    lastVisibility = visible;
    Check(WindowActivityService.IsVisible == visible, "Visibility must be committed before notification.");
};
WindowActivityService.SetVisibility(!hiddenAtStart);

var service = DeviceConnectionStatusService.Shared;
Check(ReferenceEquals(service, DeviceConnectionStatusService.Shared), "Consumers must share the same status service.");
Check(DispatcherTimer.Instances.Count == 1, "The shared UI status service must own exactly one timer.");
var timer = DispatcherTimer.Instances.Single();
Check(timer.Interval == TimeSpan.FromSeconds(2), "UI status interval changed unexpectedly.");
Check(timer.IsEnabled == !hiddenAtStart, "Initial polling must match actual window visibility.");
Check(HaloPixelDeviceConnectionMonitor.EnumerationCount == (hiddenAtStart ? 0 : 1), "Hidden startup must avoid device enumeration; visible startup must refresh immediately.");

var shellStates = new List<bool>();
var dashboardStates = new List<bool>();
EventHandler<bool> shellSubscriber = (_, connected) => shellStates.Add(connected);
EventHandler<bool> dashboardSubscriber = (_, connected) => dashboardStates.Add(connected);
service.StatusRefreshed += shellSubscriber;
service.StatusRefreshed += dashboardSubscriber;

if (hiddenAtStart)
{
    timer.AdvanceOneInterval();
    Check(HaloPixelDeviceConnectionMonitor.EnumerationCount == 0, "Hidden startup must remain idle across clock intervals.");
    WindowActivityService.SetVisibility(true);
    Check(HaloPixelDeviceConnectionMonitor.EnumerationCount == 1, "Showing the initial hidden window must refresh without waiting for a tick.");
}

var beforeTick = HaloPixelDeviceConnectionMonitor.EnumerationCount;
var shellBeforeTick = shellStates.Count;
var dashboardBeforeTick = dashboardStates.Count;
HaloPixelDeviceConnectionMonitor.Connected = true;
timer.AdvanceOneInterval();
Check(HaloPixelDeviceConnectionMonitor.EnumerationCount == beforeTick + 1, "Two UI consumers caused duplicate physical enumeration.");
Check(shellStates.Count == shellBeforeTick + 1 && dashboardStates.Count == dashboardBeforeTick + 1, "Each consumer must receive the shared refresh once.");
Check(service.IsConnected && shellStates[^1] && dashboardStates[^1], "Shared status must match the current device result.");

var beforeHide = HaloPixelDeviceConnectionMonitor.EnumerationCount;
var beforeHideEvents = visibilityEvents;
WindowActivityService.SetVisibility(false);
Check(!timer.IsEnabled && !WindowActivityService.IsVisible, "Hiding or minimizing must suspend the shared timer.");
Check(visibilityEvents == beforeHideEvents + 1 && lastVisibility == false, "Consumers must receive one hidden notification.");
HaloPixelDeviceConnectionMonitor.Connected = false;
for (var index = 0; index < 20; index++)
    timer.AdvanceOneInterval();
Check(HaloPixelDeviceConnectionMonitor.EnumerationCount == beforeHide, "A hidden window continued automatic enumeration.");
Check(service.IsConnected, "A paused display cache must retain its last result until refresh.");
WindowActivityService.SetVisibility(false);
Check(visibilityEvents == beforeHideEvents + 1, "Repeated hidden notifications must be coalesced.");

WindowActivityService.SetVisibility(true);
Check(timer.IsEnabled && WindowActivityService.IsVisible, "Restoring the window must resume polling.");
Check(HaloPixelDeviceConnectionMonitor.EnumerationCount == beforeHide + 1, "Restoring must immediately perform exactly one physical check.");
Check(!service.IsConnected && !shellStates[^1] && !dashboardStates[^1], "Restoring must replace stale device status for both consumers.");
var beforeRepeatedVisible = HaloPixelDeviceConnectionMonitor.EnumerationCount;
var beforeRepeatedVisibleEvents = visibilityEvents;
for (var index = 0; index < 10; index++)
    WindowActivityService.SetVisibility(true);
Check(visibilityEvents == beforeRepeatedVisibleEvents, "Repeated visible notifications must be coalesced.");
Check(HaloPixelDeviceConnectionMonitor.EnumerationCount == beforeRepeatedVisible, "Repeated visible state must not trigger extra checks.");
Check(DispatcherTimer.Instances.Count == 1, "Restoring must reuse the existing timer.");

// A cached dashboard unsubscribes when hidden/unloaded; the shell still receives
// the single common check, and a later dashboard attachment shares that clock.
service.StatusRefreshed -= dashboardSubscriber;
var dashboardBeforeDetach = dashboardStates.Count;
var shellBeforeDetach = shellStates.Count;
timer.AdvanceOneInterval();
Check(dashboardStates.Count == dashboardBeforeDetach, "Detached dashboard kept receiving UI updates.");
Check(shellStates.Count == shellBeforeDetach + 1, "Detaching the dashboard must not suspend the shell.");
service.StatusRefreshed += dashboardSubscriber;
var beforeReattach = HaloPixelDeviceConnectionMonitor.EnumerationCount;
timer.AdvanceOneInterval();
Check(HaloPixelDeviceConnectionMonitor.EnumerationCount == beforeReattach + 1 && dashboardStates.Count == dashboardBeforeDetach + 1, "Dashboard reattachment must reuse one physical check.");

var beforeManual = HaloPixelDeviceConnectionMonitor.EnumerationCount;
HaloPixelDeviceConnectionMonitor.Connected = true;
service.Refresh();
Check(HaloPixelDeviceConnectionMonitor.EnumerationCount == beforeManual + 1 && service.IsConnected, "An explicit connection test must bypass the cached result.");
service.StatusRefreshed -= shellSubscriber;
service.StatusRefreshed -= dashboardSubscriber;
WindowActivityService.SetVisibility(false);
Check(!timer.IsEnabled, "The final hidden state must leave no UI polling active.");

Console.WriteLine($"{passed} resource lifecycle checks passed ({(hiddenAtStart ? "hidden" : "visible")} startup); no native device or window access.");
