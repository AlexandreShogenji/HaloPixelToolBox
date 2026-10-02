using HaloPixelToolBox;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.ViewModels;
using Microsoft.UI.Xaml;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++; Console.WriteLine("PASS " + name);
}
async Task WaitUntil(Func<bool> condition)
{
    for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
    if (!condition()) throw new InvalidOperationException("Timed out waiting for probe state");
}
if (args.Contains("--voice"))
{
    await VoiceRuntimeProbe.RunAsync(Check);
    Console.WriteLine($"All {passed} voice shortcut and page lifecycle probes passed.");
    return;
}
if (args.Contains("--page"))
{
    PageRuntimeProbe.Run(Check);
    await ComposerRuntimeProbe.RunAsync(Check);
    var pageXaml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","DshSessionsPage.xaml"));
    System.Xml.Linq.XNamespace pageNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    Check(pageXaml.Descendants(pageNs + "ProgressRing").All(ring => ring.Parent!.Name == pageNs + "Grid"
        && (string?)ring.Parent.Attribute("Width") == "16" && (string?)ring.Parent.Attribute("Height") == "16"),
        "both busy rings retain a fixed layout slot while hidden");
    Check(pageXaml.Descendants(pageNs + "Grid").Any(grid => (string?)grid.Attribute("Grid.Row") == "1" && double.TryParse((string?)grid.Attribute("MinHeight"), out var height) && height >= 16),
        "history status row keeps stable height throughout refresh");
    Check(pageXaml.Descendants(pageNs + "TextBox").Any(box => (string?)box.Attribute("Text") == "{x:Bind ViewModel.MessageDraft, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
        && (string?)box.Attribute("AcceptsReturn") == "True" && box.Attribute("IsEnabled") is null && box.Attribute("KeyDown") is null),
        "XAML composer keeps live two-way multiline draft and remains editable while sending");
    Check(pageXaml.Descendants(pageNs + "MenuFlyoutItem").Any(item => (string?)item.Attribute("Visibility") == "{x:Bind ViewModel.ArchiveSupportVisibility, Mode=OneWay}"),
        "More menu exposes actual archive support explanation");
        App.DshTasks.Reset();
    var selectionService=App.DshSessions;
    var selectionFirst=new DshSessionSummary("SELECTION-A","保留详情",@"C:\Tasks\a",DateTimeOffset.Now,"idle");
    var selectionSecond=selectionFirst with{Id="SELECTION-B",Title="另一个会话"};
    selectionService.Publish(new(true,"host","connected",[selectionFirst,selectionSecond],null){Home=@"C:\SelectionHome"});
    selectionService.HistoryReader=(id,_,_)=>Task.FromResult(new DshHistoryPage([new(1,"user",id,null){SessionId=id}],null,false));
    var selectionPage=new HaloPixelToolBox.Views.DshSessionsPage();selectionPage.RaiseLoaded();
    selectionPage.ViewModel.OpenSession(selectionPage.ViewModel.Sessions[0]);
    selectionPage.ViewModel.MessageDraft="继续输入";
    var selectedBeforeFilter=selectionPage.ViewModel.SelectedSession!;
    selectionPage.ViewModel.SearchText="另一个会话";
    var selectionHandler=typeof(HaloPixelToolBox.Views.DshSessionsPage).GetMethod("SessionsList_SelectionChanged",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
    var removedArgs=new Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs();removedArgs.RemovedItems.Add(selectedBeforeFilter);
    selectionHandler.Invoke(selectionPage,[selectionPage,removedArgs]);
    Check(selectionPage.ViewModel.SelectedSession?.Id==selectionFirst.Id&&selectionPage.ViewModel.MessageDraft=="继续输入","filter-only removal event cannot clear current chat");
    var keyboardArgs=new Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs();keyboardArgs.AddedItems.Add(selectionPage.ViewModel.Sessions[0]);
    selectionHandler.Invoke(selectionPage,[selectionPage,keyboardArgs]);
    Check(selectionPage.ViewModel.SelectedSession?.Id==selectionSecond.Id,"added keyboard selection opens selected chat");
    selectionPage.RaiseUnloaded();
    keyboardArgs=new();keyboardArgs.AddedItems.Add(selectedBeforeFilter);
    selectionHandler.Invoke(selectionPage,[selectionPage,keyboardArgs]);
    Check(selectionPage.ViewModel.SelectedSession?.Id==selectionSecond.Id,"detached page ignores late selection change");
    Check(pageXaml.Descendants(pageNs+"ListView").Any(list=>(string?)list.Attribute("SelectedItem")=="{x:Bind ViewModel.SelectedSession, Mode=OneWay}"&&(string?)list.Attribute("SelectionChanged")=="SessionsList_SelectionChanged"),"XAML selection cannot push filtered removal back into conversation VM");
Console.WriteLine($"All {passed} page runtime and XAML probes passed.");
    return;
}
if (args.Contains("--tasks"))
{
    TaskAnswerDraftProbe.Run(Check);
    await TaskRuntimeProbe.RunAsync(Check);
    await TaskStatusProbe.RunAsync(Check);
    await TaskQuestionUiProbe.RunAsync(Check);
    Console.WriteLine($"All {passed} task UI and view-model probes passed.");
    return;
}
var first = new DshSessionSummary("A", "PixelBar project", @"D:\Halo", DateTimeOffset.Now, "running");
var second = new DshSessionSummary("B", "Other conversation", @"C:\Music", DateTimeOffset.Now, "unknown");
var service = App.DshSessions;
service.Publish(new(true, "http://127.0.0.1:8765", "connected", [first, second], null));
bool latestChanged = false;
service.HistoryReader = (id, before, ct) => Task.FromResult(id == "A"
    ? before is not null
        ? new DshHistoryPage([new(1, "user", "oldest", null), new(2, "assistant", "older", null), new(3, "user", "recent", null)], 1, false)
        : new DshHistoryPage(latestChanged
            ? [new(3, "user", "recent", null), new(4, "assistant", "updated", null), new(5, "tool", "new event", null)]
            : [new(3, "user", "recent", null), new(4, "assistant", "latest", null)], 3, true)
    : new DshHistoryPage([new(10, "user", "second session", null)], null, false));
var vm = new DshSessionsPageViewModel();
vm.Attach();
Check(vm.Sessions.Count == 2 && vm.IsConnected, "attach observes current connected snapshot");
Check(vm.CanRefresh && !vm.CanConnect, "connection commands reflect actual connected state");
vm.SearchText = "pixel";
Check(vm.Sessions.Count == 1 && vm.Sessions[0].Id == "A", "case-insensitive title search");
vm.SearchText = "music";
Check(vm.Sessions.Count == 1 && vm.Sessions[0].Id == "B", "working-directory search");
vm.SearchText = "no such title";
Check(vm.Sessions.Count == 0 && vm.EmptyListVisibility == Visibility.Visible, "no matches have a visible empty state");
vm.SearchText = string.Empty;
vm.IsCompactLayout = true;
Check(vm.ListPaneVisibility == Visibility.Visible && vm.DetailsPaneVisibility == Visibility.Collapsed, "compact layout starts on list");
vm.OpenSession(vm.Sessions.First(item => item.Id == "A"));
await WaitUntil(() => vm.History.Count == 2 && !vm.IsHistoryLoading);
Check(vm.DetailsPaneVisibility == Visibility.Visible && vm.ListPaneVisibility == Visibility.Collapsed, "compact selection switches to details");
Check(vm.BackButtonVisibility == Visibility.Visible, "compact details provide a back button");
Check(service.TargetSelections == 0 && service.Current.VoiceTarget is null, "reading a session does not set voice target");
Check(vm.SelectedRuntimeStatus == "运行中", "runtime status comes from live summary");
vm.BackToListCommand.Execute(null);
Check(vm.ListPaneVisibility == Visibility.Visible && vm.SelectedSession?.Id == "A", "back keeps current selection while showing list");
vm.OpenSession(vm.SelectedSession!);
Check(vm.DetailsPaneVisibility == Visibility.Visible, "clicking the already-selected row reopens details");
vm.IsCompactLayout = false;
Check(vm.ListPaneVisibility == Visibility.Visible && vm.DetailsPaneVisibility == Visibility.Visible && vm.BackButtonVisibility == Visibility.Collapsed,
    "wide layout shows both panes");
vm.SelectVoiceTargetCommand.Execute(null);
Check(service.Current.VoiceTarget?.Id == "A" && vm.Sessions.First(item => item.Id == "A").TargetLabel.Length > 0, "explicit target action saves stable id and marks row");
vm.ClearVoiceTargetCommand.Execute(null);
Check(service.Current.VoiceTarget is null && !vm.CanClearVoiceTarget, "clear target clears shared selection");
await vm.LoadEarlierCommand.ExecuteAsync(null);
Check(vm.History.Select(item => item.Sequence).SequenceEqual(new long[] { 1, 2, 3, 4 }), "older page prepends chronologically and deduplicates overlap");
Check(!vm.CanLoadEarlier && vm.LoadEarlierVisibility == Visibility.Collapsed, "last page hides load-earlier action");
var unchanged = vm.History.First(item => item.Sequence == 3);
latestChanged = true;
await vm.RefreshCommand.ExecuteAsync(null);
Check(vm.History.Select(item => item.Sequence).SequenceEqual(new long[] { 1, 2, 3, 4, 5 }), "latest refresh preserves previously loaded older pages");
Check(vm.History.First(item => item.Sequence == 4).Text == "updated", "latest refresh updates changed entry");
Check(ReferenceEquals(unchanged, vm.History.First(item => item.Sequence == 3)), "unchanged entries keep object identity for text selection");

var staleHistory = new TaskCompletionSource<DshHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
CancellationToken staleToken = default;
service.HistoryReader = (id, before, ct) =>
{
    if (id == "B") { staleToken = ct; return staleHistory.Task; }
    return Task.FromResult(new DshHistoryPage([new(30, "user", "current A", null)], null, false));
};
vm.OpenSession(vm.Sessions.First(item => item.Id == "B"));
Check(vm.IsHistoryLoading && !vm.CanLoadEarlier, "pending history disables pagination");
vm.OpenSession(vm.Sessions.First(item => item.Id == "A"));
await WaitUntil(() => vm.History.Count == 1 && vm.History[0].Sequence == 30);
Check(staleToken.IsCancellationRequested, "switching sessions cancels old history read");
staleHistory.SetResult(new([new(99, "user", "stale B", null)], null, false));
await Task.Delay(30);
Check(vm.History.Count == 1 && vm.History[0].Sequence == 30, "late old-session response cannot replace selected history");
service.Publish(service.Current with { IsConnected = false, Message = "host offline", Sessions = [first with { RuntimeStatus = "unknown" }, second] });
Check(!vm.CanRefresh && !vm.CanSelectVoiceTarget && vm.IsErrorOpen, "offline snapshot disables connected actions and exposes error");
Check(vm.SelectedRuntimeStatus == "状态未知", "offline status remains unknown rather than inferring from update time");
Check(new DshSessionListItem(first with { UpdatedAt = DateTimeOffset.MinValue }, null).UpdatedAtText == "更新时间未知", "unknown timestamp is not shown as a fabricated date");
Check(new DshHistoryListItem(new(1, "assistant", "long", null, true)).Text.Contains("已截断"), "truncated history entry is visibly marked");
service.Publish(service.Current with { IsConnected = true, Sessions = [first, second] });
var detachedRead = new TaskCompletionSource<DshHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
CancellationToken detachedToken = default;
service.HistoryReader = (_, _, ct) => { detachedToken = ct; return detachedRead.Task; };
vm.OpenSession(vm.Sessions.First(item => item.Id == "B"));
vm.Detach();
Check(detachedToken.IsCancellationRequested && service.Disconnects == 0, "unload cancels reads without stopping backend host");
detachedRead.SetResult(new([new(100, "user", "late detached", null)], null, false));
await Task.Delay(30);
Check(vm.History.Count == 0, "unloaded page ignores late history response");

// Reused ids in another DSH home must not inherit or merge old history.
service.Publish(new(true, "http://127.0.0.1:8765", "connected", [first], null) { Home = @"C:\DSH-A" });
service.HistoryReader = (_, _, _) => Task.FromResult(new DshHistoryPage([new(1, "user", "old home", null)], 1, false));
var crossHome = new DshSessionsPageViewModel();
crossHome.Attach();
crossHome.OpenSession(crossHome.Sessions[0]);
await WaitUntil(() => crossHome.History.Count == 1);
var oldHomeRead = new TaskCompletionSource<DshHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
CancellationToken oldHomeToken = default;
service.HistoryReader = (_, _, ct) => { oldHomeToken = ct; return oldHomeRead.Task; };
var oldRefresh = crossHome.RefreshCommand.ExecuteAsync(null);
await WaitUntil(() => crossHome.IsHistoryLoading);
service.Publish(service.Current with { Home = @"D:\DSH-B" });
Check(oldHomeToken.IsCancellationRequested, "changing DSH home cancels in-flight history request");
Check(crossHome.SelectedSession is null && crossHome.History.Count == 0 && !crossHome.IsDetailOpen,
    "changing DSH home clears same-id selection history and details");
service.HistoryReader = (_, _, _) => Task.FromResult(new DshHistoryPage([new(1, "user", "new home only", null)], 1, false));
crossHome.OpenSession(crossHome.Sessions[0]);
await WaitUntil(() => crossHome.History.Count == 1);
Check(crossHome.History[0].Text == "new home only", "same session id loads fresh history in new home");
oldHomeRead.SetResult(new([new(2, "assistant", "late old home", null)], 1, false));
await oldRefresh;
Check(crossHome.History.Count == 1 && crossHome.History[0].Text == "new home only", "late old-home response cannot merge into new-home history");
service.Publish(service.Current with { Home = @"d:\dsh-b\" });
Check(crossHome.SelectedSession?.Id == "A" && crossHome.History.Count == 1, "equivalent home casing and trailing slash preserve selection");
service.Publish(service.Current with { Home = string.Empty });
Check(crossHome.SelectedSession?.Id == "A" && crossHome.History.Count == 1, "empty compatibility home does not erase known scope");
crossHome.Detach();

// Read failures expose an explicit retry, including an older-page retry.
service.Publish(service.Current with { Home = @"D:\DSH-B" });
int attempts = 0;
service.HistoryReader = (_, _, _) => { attempts++; return Task.FromException<DshHistoryPage>(new IOException("read failed")); };
var retryVm = new DshSessionsPageViewModel();
retryVm.Attach();
retryVm.OpenSession(retryVm.Sessions[0]);
await WaitUntil(() => retryVm.HasHistoryError && !retryVm.IsHistoryLoading);
Check(attempts == 1 && retryVm.RetryHistoryVisibility == Visibility.Visible && retryVm.CanRetryHistory,
    "initial read failure exposes retry without duplicating first request");
service.HistoryReader = (_, _, _) => Task.FromResult(new DshHistoryPage([new(3, "user", "recent", null), new(4, "assistant", "latest", null)], 3, true));
await retryVm.RetryHistoryCommand.ExecuteAsync(null);
Check(retryVm.History.Count == 2 && !retryVm.HasHistoryError && retryVm.RetryHistoryVisibility == Visibility.Collapsed,
    "retry recovers empty failed history for already-selected session");
service.HistoryReader = (_, _, _) => Task.FromException<DshHistoryPage>(new IOException("older page failed"));
await retryVm.LoadEarlierCommand.ExecuteAsync(null);
Check(retryVm.History.Select(item => item.Sequence).SequenceEqual(new long[] { 3, 4 }) && retryVm.HasHistoryError,
    "failed pagination keeps existing history");
long? retriedCursor = null;
service.HistoryReader = (_, before, _) =>
{
    retriedCursor = before;
    return Task.FromResult(new DshHistoryPage([new(1, "user", "oldest", null), new(2, "assistant", "older", null)], 1, false));
};
await retryVm.RetryHistoryCommand.ExecuteAsync(null);
Check(retriedCursor == 3 && retryVm.History.Select(item => item.Sequence).SequenceEqual(new long[] { 1, 2, 3, 4 }) && !retryVm.HasHistoryError,
    "retry of failed older page preserves cursor and merges into existing history");
retryVm.Detach();

// All diagnostic records remain readable, but only compact summaries take space.
service.Publish(new(true, "http://127.0.0.1:8765", "backend descriptor", [first], null) { Home = @"D:\FilterHome" });
var mixedPage = new DshHistoryPage([
    new(1, "system", "hidden system prompt", null) { Kind = "context" },
    new(2, "developer", "hidden developer prompt", null) { Kind = "context" },
    new(3, "user", "visible question", null),
    new(4, "tool", "raw tool output", null) { Kind = "tool" },
    new(5, "assistant", "visible answer", null),
    new(6, "unknown", "internal event", null) { Kind = "context" },
    new(7, "ASSISTANT", "case-insensitive answer", null)], 1, true);
service.HistoryReader = (_, _, _) => Task.FromResult(mixedPage);
var foldedVm = new DshSessionsPageViewModel();
foldedVm.Attach(); foldedVm.OpenSession(foldedVm.Sessions[0]);
await WaitUntil(() => foldedVm.History.Count == 7 && !foldedVm.IsHistoryLoading);
Check(foldedVm.VisibleHistory.Count == 7 && foldedVm.VisibleHistory.Where(row => row.IsConversation).Select(row => row.Sequence).SequenceEqual(new long[] { 3, 5, 7 }),
    "context and tools remain as compact rows alongside conversation");
Check(foldedVm.VisibleHistory.Where(row => !row.IsConversation).All(row => row.IsCollapsedRecord && row.CollapsedRecordVisibility == Visibility.Visible),
    "system developer tool and internal records are collapsed by default");
Check(foldedVm.VisibleHistory.Single(row => row.Sequence == 1).CollapsedSummary.StartsWith("运行上下文")
    && foldedVm.VisibleHistory.Single(row => row.Sequence == 4).CollapsedSummary.StartsWith("执行工具"),
    "collapsed diagnostics use human-readable summaries");
var visibleQuestion = foldedVm.VisibleHistory.Single(row => row.Sequence == 3);
Check(visibleQuestion.BubbleAlignment == HorizontalAlignment.Right && visibleQuestion.UserBubbleVisibility == Visibility.Visible
    && foldedVm.VisibleHistory.Single(row => row.Sequence == 5).BubbleAlignment == HorizontalAlignment.Left,
    "user bubbles align right and assistant bubbles align left");
service.HistoryReader = (_, _, _) => Task.FromResult(new DshHistoryPage([
    new(5, "assistant", "edited answer", null), new(8, "tool", "new raw tool output", null) { Kind = "tool" },
    new(9, "system", "new prompt", null) { Kind = "context" }, new(10, "user", "new question", null)], 5, false));
await foldedVm.RefreshCommand.ExecuteAsync(null);
Check(foldedVm.VisibleHistory.Count == 10 && foldedVm.VisibleHistory.Single(row => row.Sequence == 5).Text == "edited answer",
    "live refresh preserves context and tool rows while updating visible answer");
Check(ReferenceEquals(visibleQuestion, foldedVm.VisibleHistory.Single(row => row.Sequence == 3)),
    "live refresh keeps unchanged conversation text row identity");
foldedVm.SelectVoiceTargetCommand.Execute(null);
Check(foldedVm.SelectedStatus.Contains("音箱目标") && !foldedVm.CanSelectVoiceTarget && foldedVm.ClearTargetVisibility == Visibility.Visible,
    "selected target header reflects saved real session id");
foldedVm.ClearVoiceTargetCommand.Execute(null);
Check(foldedVm.CanSelectVoiceTarget && foldedVm.ClearTargetVisibility == Visibility.Collapsed, "clear target updates compact actions");
Check(foldedVm.ConnectionStatus == "已连接", "connection state remains concise");
service.Publish(service.Current with { Home = @"D:\ToolsOnlyHome" });
Check(foldedVm.VisibleHistory.Count == 0 && foldedVm.SelectedSession is null, "home change clears visible and raw history");
service.HistoryReader = (_, before, _) => Task.FromResult(before is null
    ? new DshHistoryPage([new(10, "system", "prompt", null) { Kind = "context" }, new(11, "tool", "output", null) { Kind = "tool" }], 10, true)
    : new DshHistoryPage([new(1, "user", "older question", null), new(2, "assistant", "older answer", null)], 1, false));
foldedVm.OpenSession(foldedVm.Sessions[0]);
await WaitUntil(() => foldedVm.History.Count == 2 && !foldedVm.IsHistoryLoading);
Check(foldedVm.VisibleHistory.Count == 2 && foldedVm.VisibleHistory.All(row => row.IsCollapsedRecord) && foldedVm.CanLoadEarlier,
    "diagnostic-only latest page keeps compact records and older cursor accessible");
await foldedVm.LoadEarlierCommand.ExecuteAsync(null);
Check(foldedVm.VisibleHistory.Count == 4 && foldedVm.VisibleHistory.Count(row => row.IsConversation) == 2,
    "loading older conversation alongside diagnostic-only latest page does not discard records");
service.HistoryReader = (_, _, _) => Task.FromException<DshHistoryPage>(new IOException("detailed read error"));
await foldedVm.RefreshCommand.ExecuteAsync(null);
Check(foldedVm.HasHistoryError && !foldedVm.IsErrorOpen && foldedVm.HistoryErrorDetail == "detailed read error",
    "history failure stays in history area rather than duplicating top error");
service.HistoryReader = (_, _, _) => Task.FromResult(new DshHistoryPage([new(12, "assistant", "recovered", null)], null, false));
await foldedVm.RetryHistoryCommand.ExecuteAsync(null);
Check(!foldedVm.HasHistoryError && foldedVm.HistoryErrorDetail.Length == 0, "retry clears folded reader error");
foldedVm.Detach();

// Long and legacy messages are expandable without rewriting ordinary prose.
const string legacyPrefix = "你是 Halo PixelBar 的语音助手。请严格执行下面的用户口令；优先调用可用的 PixelBar 工具完成操作；完成后只用一句简短中文说明结果，不使用 Markdown。不要改变、扩展或猜测用户原意。用户口令：";
var legacy = new DshHistoryListItem(new(1, "user", legacyPrefix + "关灯。用户口令：这部分也是口令。", null) { Kind = "context", SessionId = "old" });
Check(legacy.IsConversation && legacy.IsUser && legacy.Text == "关灯。用户口令：这部分也是口令。" && legacy.OriginalText == legacyPrefix + legacy.Text,
    "exact legacy wrapper displays command and retains original text");
Check(legacy.OriginalTextVisibility == Visibility.Visible && !legacy.IsOriginalExpanded,
    "legacy wrapper is available in a closed context expander");
var ordinary = new DshHistoryListItem(new(1, "user", "请解释‘用户口令：关灯’这句话。", null));
Check(!ordinary.IsLegacyVoiceWrapper && ordinary.Text == ordinary.Entry.Text, "ordinary user prose containing marker is not rewritten");
var nearPrefix = new DshHistoryListItem(new(1, "user", "引用：" + legacyPrefix + "关灯", null));
Check(!nearPrefix.IsLegacyVoiceWrapper && nearPrefix.Text == nearPrefix.Entry.Text, "non-exact prefix is not treated as historical wrapper");
var longMessage = new DshHistoryListItem(new(1, "assistant", new string('长', 351), null));
Check(longMessage.IsCollapsedRecord && !longMessage.IsExpanded && longMessage.Text.Length == 351,
    "message above 350 characters is closed and keeps complete body");
Check(new DshHistoryListItem(new(1, "assistant", new string('长', 350), null)).MessageTextVisibility == Visibility.Visible,
    "350-character message remains inline");
Check(new DshHistoryListItem(new(1, "assistant", string.Join("\n", Enumerable.Repeat("行", 10)), null)).IsCollapsedRecord,
    "message above eight newlines is collapsed");
longMessage.IsExpanded = true;
Check(longMessage.IsExpanded && longMessage.Text.Length == 351, "expanding long message does not lose text");

// Device controls occupy one virtual list entry while ordinary projects stay independent.
var deviceSession = new DshSessionSummary("DEVICE-1", "backend generated title", @"D:\Halo", DateTimeOffset.Now, "idle") { IsDeviceControl = true };
var legacyDevice = deviceSession with { Id = "DEVICE-OLD", Title = "" };
var namedOrdinary = first with { Id = "NAMED-NORMAL", Title = "音箱控制" };
var unnamedOrdinary = second with { Id = "UNNAMED-NORMAL", Title = "" };
service.Publish(new(true, "http://127.0.0.1:8765", "connected", [first, deviceSession, legacyDevice, namedOrdinary, unnamedOrdinary], first)
    { Home = @"D:\DeviceHome", DeviceSessionId = deviceSession.Id });
int deviceReads = 0;
IReadOnlyList<string>? requestedSources = null;
service.DeviceHistoryReader = (ids, cursor, ct) =>
{
    deviceReads++; requestedSources = ids;
    return Task.FromResult(new DshDeviceHistoryPage([
        new(1, "user", "legacy control", new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero)) { SessionId = "DEVICE-OLD" },
        new(1, "assistant", "new control", new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero)) { SessionId = "DEVICE-1" }], "older-device-cursor", true));
};
var deviceVm = new DshSessionsPageViewModel(); deviceVm.Attach();
await WaitUntil(() => deviceVm.History.Count == 2 && !deviceVm.IsHistoryLoading);
Check(deviceVm.Sessions.Count == 4 && deviceVm.Sessions.Count(row => row.IsDeviceControlGroup) == 1
    && deviceVm.SelectedSession?.Id == DshSessionsPageViewModel.DeviceHistoryId && deviceVm.SelectedSession.Session.Id == deviceSession.Id,
    "all confirmed device sessions share one entry with canonical stable session");
Check(deviceVm.Sessions.Any(row => row.Id == namedOrdinary.Id) && deviceVm.Sessions.Any(row => row.Id == unnamedOrdinary.Id),
    "title or lack of title alone never groups ordinary DSH sessions");
Check(requestedSources!.Order().SequenceEqual(new[] { "DEVICE-1", "DEVICE-OLD" }) && deviceReads == 1,
    "virtual history reads exact source set once during attach");
Check(deviceVm.History.Count == 2 && deviceVm.History.Select(row => row.Identity).Distinct().Count() == 2,
    "equal source sequence numbers from different device sessions are not deduplicated together");
Check(deviceVm.History[0].Entry.SessionId == "DEVICE-OLD" && deviceVm.History[1].Entry.SessionId == "DEVICE-1",
    "aggregate display sorts by created time rather than source sequence alone");
Check(service.Current.VoiceTarget?.Id == "A", "default control history selection does not overwrite coding task target");
deviceVm.SelectVoiceTargetCommand.Execute(null);
Check(service.Current.VoiceTarget?.Id == "DEVICE-1" && service.Current.VoiceTarget.Id != DshSessionsPageViewModel.DeviceHistoryId,
    "virtual target action saves canonical real session id");
string? olderCursor = null;
service.DeviceHistoryReader = (ids, cursor, ct) =>
{
    olderCursor = cursor;
    return Task.FromResult(new DshDeviceHistoryPage([
        new(0, "user", "older control", new DateTimeOffset(2026, 9, 30, 1, 0, 0, TimeSpan.Zero)) { SessionId = "DEVICE-OLD" }], null, false));
};
await deviceVm.LoadEarlierCommand.ExecuteAsync(null);
Check(olderCursor == "older-device-cursor" && deviceVm.History.Count == 3 && deviceVm.History[0].Sequence == 0,
    "aggregate pagination forwards opaque cursor and prepends older source entry");
var unchangedAggregate = deviceVm.History.Single(row => row.Entry.SessionId == "DEVICE-1");
unchangedAggregate.IsExpanded = true;
service.DeviceHistoryReader = (_, _, _) => Task.FromResult(new DshDeviceHistoryPage([
    new(1, "assistant", "updated answer", new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero)) { SessionId = "DEVICE-1" }], "latest-cursor", true));
await deviceVm.RefreshCommand.ExecuteAsync(null);
Check(deviceVm.History.Count == 3 && deviceVm.History.Single(row => row.Entry.SessionId == "DEVICE-1").IsExpanded,
    "aggregate refresh retains older records and expanded state when body changes");

var staleRead = new TaskCompletionSource<DshDeviceHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
CancellationToken oldMembershipToken = default;
service.DeviceHistoryReader = (_, _, ct) => { oldMembershipToken = ct; return staleRead.Task; };
var pendingRefresh = deviceVm.RefreshCommand.ExecuteAsync(null);
await WaitUntil(() => deviceVm.IsHistoryLoading);
var nextDevice = deviceSession with { Id = "DEVICE-2" };
service.DeviceHistoryReader = (ids, cursor, ct) =>
{
    olderCursor = cursor;
    return Task.FromResult(new DshDeviceHistoryPage([new(1, "user", "new membership", null) { SessionId = "DEVICE-2" }], null, false));
};
service.Publish(service.Current with { DeviceSessionId = "DEVICE-2", Sessions = [first, deviceSession, legacyDevice, nextDevice, namedOrdinary, unnamedOrdinary] });
await WaitUntil(() => !deviceVm.IsHistoryLoading && deviceVm.History.Count == 4);
Check(oldMembershipToken.IsCancellationRequested && olderCursor is null && deviceVm.History.Any(row => row.Text == "new membership") && deviceVm.History.Single(row => row.Entry.SessionId == "DEVICE-1").IsExpanded,
    "membership addition cancels old read and cursor but preserves older messages and expansion");
staleRead.SetResult(new([new(88, "assistant", "late old membership", null) { SessionId = "DEVICE-OLD" }], null, false));
await pendingRefresh;
Check(deviceVm.History.Count == 4 && deviceVm.History.All(row => row.Sequence != 88), "late old membership response cannot merge into new aggregate");
service.HistoryReader = (_, _, _) => Task.FromResult(new DshHistoryPage([new(1, "user", "ordinary project", null)], null, false));
deviceVm.OpenSession(deviceVm.Sessions.Single(row => row.Id == "A"));
await WaitUntil(() => deviceVm.History[0].Text == "ordinary project");
service.Publish(service.Current with { DeviceSessionId = "DEVICE-3", Sessions = [first, deviceSession, legacyDevice, nextDevice, nextDevice with { Id = "DEVICE-3" }] });
Check(deviceVm.SelectedSession?.Id == "A" && deviceVm.History[0].Text == "ordinary project",
    "new device membership does not steal ordinary project selection");
deviceVm.SearchText = "音箱控制";
Check(deviceVm.Sessions.Count == 1 && deviceVm.Sessions[0].IsDeviceControlGroup, "one device control entry is searchable");
deviceVm.Detach();

service.Publish(new(true, "http://127.0.0.1:8765", "connected", [first], null) { Home = @"D:\ArrivingDeviceHome", DeviceSessionId = "DEVICE-ARRIVING" });
var arrivingVm = new DshSessionsPageViewModel(); arrivingVm.Attach();
Check(arrivingVm.SelectedSession is null, "announced id absent from source list does not invent a history group");
service.DeviceHistoryReader = (ids, _, _) => Task.FromResult(new DshDeviceHistoryPage([new(1, "user", "arrived", null) { SessionId = ids[0] }], null, false));
service.Publish(service.Current with { Sessions = [first, deviceSession with { Id = "DEVICE-ARRIVING" }] });
await WaitUntil(() => arrivingVm.SelectedSession?.IsDeviceControlGroup == true && arrivingVm.History.Count == 1);
Check(arrivingVm.IsDetailOpen, "device group arriving after id publication opens only if nothing is selected");
service.Publish(service.Current with { Home = @"E:\AnotherDeviceHome" });
await WaitUntil(() => arrivingVm.History.Count == 1 && !arrivingVm.IsHistoryLoading);
Check(arrivingVm.History.Count == 1, "home change freshly reads default group without merging prior scope");
arrivingVm.Detach();

service.Publish(new(true, "http://127.0.0.1:8765", "connected", [deviceSession], null) { Home = @"D:\EmptyDeviceHome", DeviceSessionId = deviceSession.Id });
int emptyDeviceReads = 0;
service.DeviceHistoryReader = (_, _, _) => { emptyDeviceReads++; return Task.FromResult(new DshDeviceHistoryPage([], null, false)); };
var emptyDeviceVm = new DshSessionsPageViewModel(); emptyDeviceVm.Attach();
Check(emptyDeviceReads == 1 && emptyDeviceVm.SelectedSession?.IsDeviceControlGroup == true, "empty aggregate is not requested twice during attach");
emptyDeviceVm.Detach(); emptyDeviceVm.Attach();
Check(emptyDeviceReads == 2, "reattaching empty aggregate makes one fresh read");
emptyDeviceVm.Detach();

// Canonical metadata and defensive source validation are independent of titles.
var canonicalWithoutSetting = deviceSession with { Id = "CANONICAL", DeviceControlKind = "canonical", UpdatedAt = DateTimeOffset.Now.AddHours(-1) };
var newerLegacy = legacyDevice with { Id = "NEWER-LEGACY", UpdatedAt = DateTimeOffset.Now };
service.Publish(new(true, "http://127.0.0.1:8765", "connected", [canonicalWithoutSetting, newerLegacy, first], null)
    { Home = @"D:\CanonicalFallbackHome" });
service.DeviceHistoryReader = (ids, _, _) => Task.FromResult(new DshDeviceHistoryPage(
    [new(1, "assistant", "valid control", null) { SessionId = "CANONICAL" }], null, false));
var sourceGuardVm = new DshSessionsPageViewModel(); sourceGuardVm.Attach();
await WaitUntil(() => sourceGuardVm.History.Count == 1 && !sourceGuardVm.IsHistoryLoading);
Check(sourceGuardVm.SelectedSession?.Session.Id == "CANONICAL", "canonical device metadata wins fallback over newer legacy source");
service.DeviceHistoryReader = (_, _, _) => Task.FromResult(new DshDeviceHistoryPage(
    [new(99, "user", "ordinary source leaked", null) { SessionId = "A" }], null, false));
await sourceGuardVm.RefreshCommand.ExecuteAsync(null);
Check(sourceGuardVm.HasHistoryError && sourceGuardVm.History.Count == 1 && sourceGuardVm.History[0].Text == "valid control",
    "aggregate rejects unrequested source without committing or discarding prior history");
service.HistoryReader = (_, _, _) => Task.FromResult(new DshHistoryPage(
    [new(1, "user", "wrong regular source", null) { SessionId = "OTHER" }], null, false));
sourceGuardVm.OpenSession(sourceGuardVm.Sessions.Single(row => row.Id == "A"));
await WaitUntil(() => sourceGuardVm.HasHistoryError && !sourceGuardVm.IsHistoryLoading);
Check(sourceGuardVm.History.Count == 0, "ordinary session rejects explicit history identity from another source");
sourceGuardVm.Detach();

var xamlPath = Path.Combine(AppContext.BaseDirectory,"Fixtures","DshSessionsPage.xaml");
var xamlSource = File.ReadAllText(xamlPath);
var xaml = System.Xml.Linq.XDocument.Parse(xamlSource);
System.Xml.Linq.XNamespace xamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
System.Xml.Linq.XNamespace namesNs = "http://schemas.microsoft.com/winfx/2006/xaml";
var workspace = xaml.Descendants(xamlNs + "Grid").Single(node => (string?)node.Attribute(namesNs + "Name") == "SessionsWorkspace");
var columns = workspace.Element(xamlNs + "Grid.ColumnDefinitions")!.Elements().ToArray();
Check((string?)columns[0].Attribute(namesNs + "Name") == "SessionDetailsColumn" && (string?)columns[0].Attribute("Width") == "*"
    && (string?)columns[1].Attribute(namesNs + "Name") == "SessionListColumn" && (string?)columns[1].Attribute("Width") == "240",
    "wide XAML gives left history remaining width and right list 240 pixels");
var listPane = workspace.Elements(xamlNs + "Border").Single(node => (string?)node.Attribute(namesNs + "Name") == "SessionListPane");
Check((string?)listPane.Attribute("Grid.Column") == "1", "session list stays right");
Check(xamlSource.Contains("ViewModel.VisibleHistory") && xamlSource.Contains("x:Bind BubbleAlignment")
    && xamlSource.Contains("x:Bind IsExpanded, Mode=TwoWay") && xamlSource.Contains("x:Bind OriginalText")
    && !xamlSource.Contains("显示工具记录"), "XAML uses chat alignment and collapsible complete-text records");
// Exercise the actual VM cache and background paths against controlled service I/O.
var idleSummary = first with { Id = "IDLE", RuntimeStatus = "idle", UpdatedAt = DateTimeOffset.UtcNow };
service.Publish(new(true, "http://127.0.0.1:8765", "connected", [idleSummary], null) { Home = @"D:\RefreshHome" });
int cacheReads = 0;
string cachedBody = "answer 1";
service.HistoryReader = (_, _, _) => { cacheReads++; return Task.FromResult(new DshHistoryPage([new(1, "assistant", cachedBody, null)], 1, false)); };
var cacheVm = new DshSessionsPageViewModel();
cacheVm.Attach(); cacheVm.OpenSession(cacheVm.Sessions[0]);
Check(cacheReads == 1, "idle source makes one initial uncached history read");
var cachedSessionRow = cacheVm.Sessions[0];
var cachedHistoryRow = cacheVm.History[0];
int collectionResets = 0;
cacheVm.Sessions.CollectionChanged += (_, args) => { if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) collectionResets++; };
service.Publish(service.Current);
Check(cacheReads == 1 && ReferenceEquals(cachedSessionRow, cacheVm.Sessions[0]), "unchanged idle snapshot skips history I/O and keeps list row identity");
service.Publish(service.Current with { Sessions = [idleSummary with { Title = "renamed idle task" }] });
Check(cacheReads == 1 && collectionResets == 0, "title-only changes update list without rereading or resetting its collection");
await cacheVm.RefreshCommand.ExecuteAsync(null);
Check(cacheReads == 2, "explicit refresh still reads history even when metadata is unchanged");
cachedBody = "answer 2";
idleSummary = idleSummary with { UpdatedAt = idleSummary.UpdatedAt.AddSeconds(1) };
service.Publish(service.Current with { Sessions = [idleSummary] });
Check(cacheReads == 3 && cacheVm.History[0].Text == cachedBody, "source update timestamp immediately refreshes selected messages");
cachedBody = "stream fragment 1";
idleSummary = idleSummary with { RuntimeStatus = "running" };
service.Publish(service.Current with { Sessions = [idleSummary] });
cachedBody = "stream fragment 2";
service.Publish(service.Current);
Check(cacheReads == 5 && cacheVm.History[0].Text == cachedBody, "running source always rereads streaming message even with unchanged timestamp");
idleSummary = idleSummary with { RuntimeStatus = "idle", UpdatedAt = DateTimeOffset.MinValue };
service.Publish(service.Current with { Sessions = [idleSummary] });
service.Publish(service.Current);
Check(cacheReads == 7, "unknown update time never counts as a valid history cache");
idleSummary = idleSummary with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1) };
service.Publish(service.Current with { Sessions = [idleSummary] });
var historyReady = new TaskCompletionSource<DshHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
service.HistoryReader = (_, _, _) => { cacheReads++; return historyReady.Task; };
cachedHistoryRow = cacheVm.History[0]; cachedHistoryRow.IsExpanded = true;
idleSummary = idleSummary with { UpdatedAt = idleSummary.UpdatedAt.AddSeconds(1) };
service.Publish(service.Current with { Sessions = [idleSummary] });
Check(cacheVm.IsHistoryLoading && cacheVm.HistoryLoadingVisibility == Visibility.Collapsed && ReferenceEquals(cacheVm.History[0], cachedHistoryRow),
    "automatic read with existing history stays silent and preserves current message until ready");
idleSummary = idleSummary with { UpdatedAt = idleSummary.UpdatedAt.AddSeconds(1) };
service.Publish(service.Current with { Sessions = [idleSummary] });
service.HistoryReader = (_, _, _) => { cacheReads++; return Task.FromResult(new DshHistoryPage([new(1, "assistant", "newest after pending", null)], null, false)); };
int readsBeforeCompletion = cacheReads;
historyReady.SetResult(new([new(1, "assistant", "first pending reply", null)], null, false));
await WaitUntil(() => !cacheVm.IsHistoryLoading && cacheVm.History[0].Text == "newest after pending");
Check(cacheReads == readsBeforeCompletion + 1 && cacheVm.History[0].IsExpanded, "new source revision arriving during history read gets one follow-up read with expansion preserved");
service.HistoryReader = (_, _, _) => { cacheReads++; return Task.FromException<DshHistoryPage>(new IOException("automatic read failed")); };
idleSummary = idleSummary with { UpdatedAt = idleSummary.UpdatedAt.AddSeconds(1) };
service.Publish(service.Current with { Sessions = [idleSummary] });
Check(cacheVm.HasHistoryError && cacheVm.History.Count == 1, "automatic read failure stays visible and retains existing messages");
service.HistoryReader = (_, _, _) => { cacheReads++; return Task.FromResult(new DshHistoryPage([new(1, "assistant", "recovered automatic read", null)], null, false)); };
service.Publish(service.Current);
Check(!cacheVm.HasHistoryError && cacheVm.History[0].Text == "recovered automatic read", "failed revision remains uncached and retries on an unchanged snapshot");

var delayedConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Func<CancellationToken, Task> backgroundOperation = async _ => { await delayedConnection.Task; service.Publish(service.Current); };
var operationMethod = typeof(DshSessionsPageViewModel).GetMethod("RunConnectionOperationAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
var silentOperation = (Task<bool>)operationMethod.Invoke(cacheVm, [backgroundOperation, true])!;
Check(!cacheVm.IsConnectionBusy && cacheVm.ConnectionStatus == "已连接" && !cacheVm.CanRefresh, "background list operation has no connection spinner but still prevents overlapping refreshes");
service.Publish(service.Current with { IsConnected = false, Message = "host dropped" });
delayedConnection.SetResult();
Check(!await silentOperation && !cacheVm.IsConnected && cacheVm.IsErrorOpen, "background refresh cannot report success when host disconnected");
service.Publish(service.Current with { IsConnected = true });
await WaitUntil(() => !cacheVm.IsHistoryLoading);

var profileHistory = new TaskCompletionSource<DshHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
CancellationToken previousProfileToken = default;
service.HistoryReader = (_, _, ct) => { previousProfileToken = ct; return profileHistory.Task; };
var previousProfileRead = cacheVm.RefreshCommand.ExecuteAsync(null);
await WaitUntil(() => cacheVm.IsHistoryLoading);
HaloPixelToolBox.Profiles.CrossVersionProfiles.DisplayFeatureProfile.DshProfileName = "changed-profile";
service.Publish(service.Current);
Check(previousProfileToken.IsCancellationRequested && cacheVm.SelectedSession is null && cacheVm.History.Count == 0,
    "profile change cancels in-flight read and clears same-home same-id cached history");
profileHistory.SetResult(new([new(88, "assistant", "late previous profile", null)], null, false));
await previousProfileRead;
Check(cacheVm.History.Count == 0, "late previous-profile response cannot populate new profile");
cacheVm.Detach();
HaloPixelToolBox.Profiles.CrossVersionProfiles.DisplayFeatureProfile.DshProfileName = "default";

// Manual conversation composer and management commands use only real service paths.
var chatCapabilities = new DshSessionCapabilities(true, true, true, true, true);
var chatA = first with { Id = "CHAT-A", Title = "chat A", RuntimeStatus = "idle", UpdatedAt = DateTimeOffset.UtcNow };
var chatB = chatA with { Id = "CHAT-B", Title = "chat B" };
service.Publish(new(true, "http://127.0.0.1:8765", "connected", [chatA, chatB], null)
    { Home = @"D:\ChatHome", Capabilities = chatCapabilities });
int chatReads = 0, regularSends = 0, deviceSends = 0, creates = 0, renames = 0, archives = 0;
string chatReply = "previous reply";
service.HistoryReader = (id, _, _) => { chatReads++; return Task.FromResult(new DshHistoryPage([new(1, "assistant", chatReply, null) { SessionId = id }], null, false)); };
var chatVm = new DshSessionsPageViewModel(); chatVm.Attach(); chatVm.OpenSession(chatVm.Sessions.Single(row => row.Id == chatA.Id));
Check(!chatVm.CanSendMessage && chatVm.ComposerVisibility == Visibility.Visible, "composer appears for selection and rejects blank message");
chatVm.MessageDraft = "  first manual message  ";
var chatPending = new TaskCompletionSource<DshDeviceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
string? sentId = null, sentBody = null;
service.MessageSender = (id, text, _) => { regularSends++; sentId = id; sentBody = text; return chatPending.Task; };
var sendPending = chatVm.SendMessageCommand.ExecuteAsync(null);
Check(chatVm.IsSending && !chatVm.CanSendMessage && regularSends == 1 && sentId == chatA.Id && sentBody == "first manual message",
    "ordinary manual send preserves existing real id and sends only trimmed message");
service.Publish(service.Current with { Sessions = [chatA with { UpdatedAt = chatA.UpdatedAt.AddSeconds(1) }, chatB] });
Check(chatVm.MessageDraft == "  first manual message  " && regularSends == 1, "background update during send keeps draft and does not resend");
chatVm.MessageDraft = "next edited draft";
chatReply = "manual reply now visible";
int readsBeforeAccepted = chatReads;
chatPending.SetResult(new(true, "done", chatReply, chatA.Id, [], []) { Accepted = true, Completed = true });
await sendPending;
Check(!chatVm.IsSending && chatVm.MessageDraft == "next edited draft" && chatVm.SendStatus == "已发送", "accepted result preserves text edited while sending");
Check(chatReads > readsBeforeAccepted && chatVm.History[0].Text == chatReply, "manual result forces latest history read even without updated timestamp");
chatVm.OpenSession(chatVm.Sessions.Single(row => row.Id == chatB.Id));
Check(chatVm.MessageDraft == string.Empty && chatVm.SendStatus == string.Empty, "switching conversations does not leak another session draft or send status");
chatVm.MessageDraft = "B private draft";
chatVm.OpenSession(chatVm.Sessions.Single(row => row.Id == chatA.Id));
Check(chatVm.MessageDraft == "next edited draft", "returning to conversation restores its draft");
var selectionPending = new TaskCompletionSource<DshDeviceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
service.MessageSender = (id, text, _) => { regularSends++; return selectionPending.Task; };
sendPending = chatVm.SendMessageCommand.ExecuteAsync(null);
chatVm.OpenSession(chatVm.Sessions.Single(row => row.Id == chatB.Id));
selectionPending.SetResult(new(true, "done", "done", chatA.Id, [], []) { Accepted = true, Completed = true });
await sendPending;
Check(chatVm.SelectedSession?.Id == chatB.Id && chatVm.MessageDraft == "B private draft" && chatVm.SendStatus == string.Empty,
    "late successful send clears only submitted session draft without stealing current selection");
chatVm.OpenSession(chatVm.Sessions.Single(row => row.Id == chatA.Id));
Check(chatVm.MessageDraft == string.Empty && chatVm.SendStatus == "已发送", "submitted unchanged draft is cleared in its own conversation");
chatVm.MessageDraft = "retry me only manually";
service.MessageSender = (id, _, _) => { regularSends++; return Task.FromResult(new DshDeviceCommandResult(false, "busy, not submitted", string.Empty, id, [], []) { ErrorCode = "busy" }); };
await chatVm.SendMessageCommand.ExecuteAsync(null);
Check(chatVm.MessageDraft == "retry me only manually" && chatVm.SendStatus == "busy, not submitted", "known rejected submission retains original draft and reason");
service.MessageSender = (id, _, _) => { regularSends++; return Task.FromResult(new DshDeviceCommandResult(false, "unknown", string.Empty, id, [], []) { ErrorCode = "unconfirmed" }); };
await chatVm.SendMessageCommand.ExecuteAsync(null);
int sendsAtUnknown = regularSends;
service.Publish(service.Current); await Task.Delay(30);
Check(chatVm.MessageDraft == "retry me only manually" && chatVm.SendStatus.Contains("尚未确认") && regularSends == sendsAtUnknown,
    "unknown acceptance preserves draft and never automatically resends on background change");
service.MessageSender = (_, _, _) => { regularSends++; return Task.FromException<DshDeviceCommandResult>(new IOException("transport broke")); };
await chatVm.SendMessageCommand.ExecuteAsync(null);
Check(chatVm.MessageDraft == "retry me only manually" && chatVm.SendStatus.Contains("未确认") && !chatVm.IsSending,
    "transport exception conservatively retains draft and releases sending state");
service.MessageSender = (id, _, _) => { regularSends++; return Task.FromResult(new DshDeviceCommandResult(true, "queued", string.Empty, id, [], []) { Accepted = true }); };
await chatVm.SendMessageCommand.ExecuteAsync(null);
Check(chatVm.MessageDraft == string.Empty && chatVm.SendStatus == "已接收，正在执行…", "known accepted pending request clears submitted draft with pending status");

var manualDevice = chatA with { Id = "MANUAL-DEVICE", IsDeviceControl = true, DeviceControlKind = "canonical" };
service.Publish(service.Current with { Sessions = [chatA, chatB, manualDevice], DeviceSessionId = manualDevice.Id });
chatVm.OpenSession(chatVm.Sessions.Single(row => row.IsDeviceControlGroup));
service.DeviceHistoryReader = (ids, _, _) => Task.FromResult(new DshDeviceHistoryPage([new(1, "assistant", "device reply", null) { SessionId = ids[0] }], null, false));
service.DeviceSender = (text, _) => { deviceSends++; sentBody = text; return Task.FromResult(new DshDeviceCommandResult(true, "done", "device reply", manualDevice.Id, [], []) { Accepted = true, Completed = true }); };
chatVm.MessageDraft = "  查询状态  ";
int ordinarySendsBeforeDevice = regularSends;
await chatVm.SendMessageCommand.ExecuteAsync(null);
Check(deviceSends == 1 && regularSends == ordinarySendsBeforeDevice && sentBody == "查询状态" && chatVm.MessageDraft == string.Empty,
    "manual speaker command uses fixed device send path rather than ordinary session send");
Check(!chatVm.CanRenameSession && !chatVm.CanArchiveSession && !chatVm.CanRestoreSession, "fixed speaker history cannot be renamed archived or restored as an ordinary task");

service.SessionCreator = (title, _) =>
{
    creates++;
    var created = chatA with { Id = "CREATED-" + creates, Title = title };
    service.Publish(service.Current with { Sessions = service.Current.Sessions.Append(created).ToArray() });
    return Task.FromResult(created);
};
service.SessionRenamer = (id, title, _) => { renames++; service.Publish(service.Current with { Sessions = service.Current.Sessions.Select(summary => summary.Id == id ? summary with { Title = title } : summary).ToArray() }); return Task.CompletedTask; };
service.SessionArchiver = (id, archived, _) => { archives++; service.Publish(service.Current with { Sessions = service.Current.Sessions.Select(summary => summary.Id == id ? summary with { IsArchived = archived } : summary).ToArray() }); return Task.CompletedTask; };
await chatVm.NewSessionCommand.ExecuteAsync("  new explicit task  ");
Check(creates == 1 && chatVm.SelectedSession?.Id == "CREATED-1" && chatVm.SelectedTitle == "new explicit task" && !chatVm.IsSessionOperationBusy,
    "explicit new-session action creates once and selects actual returned session");
await chatVm.RenameSessionCommand.ExecuteAsync(" renamed task ");
Check(renames == 1 && chatVm.SelectedSession?.Id == "CREATED-1" && chatVm.SelectedTitle == "renamed task", "rename updates real task without changing its id");
chatVm.MessageDraft = "keep through archive";
await chatVm.ArchiveSessionCommand.ExecuteAsync(null);
Check(archives == 1 && chatVm.Sessions.All(row => row.Id != "CREATED-1") && chatVm.SelectedSession is null,
    "archive hides task from normal list without deleting original summary");
chatVm.ShowArchived = true;
chatVm.OpenSession(chatVm.Sessions.Single(row => row.Id == "CREATED-1"));
Check(chatVm.MessageDraft == "keep through archive" && !chatVm.CanSendMessage && chatVm.CanRestoreSession
    && chatVm.SelectedRuntimeStatus == "已归档" && chatVm.RestoreSessionVisibility == Visibility.Visible,
    "archived task keeps history and draft but blocks send until restored");
await chatVm.RestoreSessionCommand.ExecuteAsync(null);
Check(archives == 2 && chatVm.SelectedSession?.Id == "CREATED-1" && !chatVm.SelectedSession.Session.IsArchived && chatVm.CanSendMessage,
    "restore revives same task and allows its retained draft to be sent");
await chatVm.NewSessionCommand.ExecuteAsync("bad\nname");
Check(creates == 1 && chatVm.SessionOperationStatus.Contains("1–200"), "invalid title is rejected before any create request");
service.Publish(service.Current with { Capabilities = new() });
Check(!chatVm.CanCreateSession && !chatVm.CanRenameSession && !chatVm.CanArchiveSession && !chatVm.CanRestoreSession && !chatVm.CanSendMessage,
    "missing official management and send capabilities disable unsupported ordinary actions");
service.Publish(service.Current with { Capabilities = chatCapabilities });
service.SessionRenamer = (_, _, _) => Task.FromException(new IOException("rename failed"));
await chatVm.RenameSessionCommand.ExecuteAsync("rename fails");
Check(chatVm.SelectedTitle == "renamed task" && !chatVm.IsSessionOperationBusy && chatVm.SessionOperationStatus.Contains("rename failed"),
    "management failure releases busy state and preserves current task metadata");

// Forced reads must not be lost when a successful send finishes during an older read.
var forcedOlderRead = new TaskCompletionSource<DshHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
service.HistoryReader = (_, _, _) => { chatReads++; return forcedOlderRead.Task; };
var concurrentHistory = chatVm.RefreshCommand.ExecuteAsync(null);
await WaitUntil(() => chatVm.IsHistoryLoading);
chatVm.MessageDraft = "history arrives while reading";
service.MessageSender = (id, _, _) => { regularSends++; return Task.FromResult(new DshDeviceCommandResult(true, "done", "done", id, [], []) { Accepted = true, Completed = true }); };
service.HistoryReader = (id, _, _) => { chatReads++; return Task.FromResult(new DshHistoryPage([new(1, "assistant", "reply after old in-flight read", null) { SessionId = id }], null, false)); };
await chatVm.SendMessageCommand.ExecuteAsync(null);
Check(chatVm.IsHistoryLoading && chatVm.MessageDraft == string.Empty, "accepted send during history load queues its forced latest read");
forcedOlderRead.SetResult(new([new(1, "assistant", "earlier captured body", null) { SessionId = "CREATED-1" }], null, false));
await concurrentHistory;
await WaitUntil(() => !chatVm.IsHistoryLoading && chatVm.History[0].Text == "reply after old in-flight read");
Check(chatVm.History[0].Text == "reply after old in-flight read", "queued forced read obtains new reply even when source timestamp was unchanged");
service.Publish(service.Current with { Capabilities = chatCapabilities with { CanRestoreSessions = false } });
Check(!chatVm.CanArchiveSession && chatVm.ArchiveSessionVisibility == Visibility.Collapsed && chatVm.ArchiveSupportVisibility == Visibility.Visible,
    "archive is never exposed without official restore capability");
service.Publish(service.Current with { Capabilities = chatCapabilities });
int createsBeforeLength = creates;
await chatVm.NewSessionCommand.ExecuteAsync(new string('a', 200));
Check(creates == createsBeforeLength + 1, "200-character title matches official client validation limit");
await chatVm.NewSessionCommand.ExecuteAsync(new string('a', 201));
Check(creates == createsBeforeLength + 1, "title beyond official limit never makes a creation request");
chatVm.OpenSession(chatVm.Sessions.Single(row => row.Id == "CREATED-1"));
service.HistoryReader = (id, _, _) => Task.FromResult(new DshHistoryPage([new(1, "assistant", "current message", null) { SessionId = id }], null, false));
chatVm.MessageDraft = "old scope only";
var scopePending = new TaskCompletionSource<DshDeviceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
service.MessageSender = (id, _, _) => { regularSends++; return scopePending.Task; };
sendPending = chatVm.SendMessageCommand.ExecuteAsync(null);
var scopeSnapshot = service.Current;
service.Publish(scopeSnapshot with { Home = @"E:\OtherChatHome" });
chatVm.OpenSession(chatVm.Sessions.Single(row => row.Id == "CREATED-1"));
chatVm.MessageDraft = "new scope draft";
scopePending.SetResult(new(true, "done", "done", "CREATED-1", [], []) { Accepted = true, Completed = true });
await sendPending;
Check(chatVm.MessageDraft == "new scope draft" && chatVm.SendStatus == string.Empty, "same id in new DSH home does not receive old send result or draft cleanup");
var detachedSend = new TaskCompletionSource<DshDeviceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
CancellationToken detachedSendToken = default;
service.MessageSender = (_, _, ct) => { regularSends++; detachedSendToken = ct; return detachedSend.Task; };
sendPending = chatVm.SendMessageCommand.ExecuteAsync(null);
int sendsBeforeDetach = regularSends;
chatVm.Detach();
detachedSend.SetCanceled(detachedSendToken);
await sendPending;
chatVm.Attach();
Check(detachedSendToken.IsCancellationRequested && chatVm.MessageDraft == "new scope draft" && chatVm.SendStatus.Contains("中断") && !chatVm.IsSending && regularSends == sendsBeforeDetach,
    "page detach cancels request conservatively and reattach retains interrupted draft without resending");
chatVm.Detach();

service.Publish(new(true, "http://127.0.0.1:8765", "connected", [chatA], null)
    { Home = @"D:\OfflineAfterCreateHome", Capabilities = chatCapabilities });
var offlineAfterCreateVm = new DshSessionsPageViewModel(); offlineAfterCreateVm.Attach(); offlineAfterCreateVm.OpenSession(offlineAfterCreateVm.Sessions[0]);
service.SessionCreator = (title, _) =>
{
    creates++;
    var created = chatA with { Id = "CONFIRMED-CREATE-OFFLINE", Title = title };
    service.Publish(service.Current with { IsConnected = false, Message = "host lost after accepted create", Sessions = [chatA, created] });
    return Task.FromResult(created);
};
await offlineAfterCreateVm.NewSessionCommand.ExecuteAsync("accepted but refresh offline");
Check(!offlineAfterCreateVm.IsConnected && offlineAfterCreateVm.SessionOperationStatus.Contains("会话已创建")
    && offlineAfterCreateVm.SessionOperationStatus.Contains("列表刷新失败") && offlineAfterCreateVm.SelectedSession?.Id == chatA.Id,
    "confirmed creation followed by nonthrowing offline refresh reports confirmed action and failed list rather than pretending full success");
offlineAfterCreateVm.Detach();

{
    App.DshTasks.Reset();
    var filterA = new DshSessionSummary("FILTER-A", "保留当前对话", @"C:\Tasks\a", DateTimeOffset.Now, "idle");
    var filterB = new DshSessionSummary("FILTER-B", "其他会话", @"C:\Tasks\b", DateTimeOffset.Now, "idle");
    service.HistoryReader = (id, _, _) => Task.FromResult(new DshHistoryPage([new(1,"user","history:"+id,null){SessionId=id}],null,false));
    service.Publish(new(true,"host","connected",[filterA,filterB],null){Home=@"C:\FilterHome",Capabilities=chatCapabilities});
    var filterVm = new DshSessionsPageViewModel();filterVm.Attach();filterVm.OpenSession(filterVm.Sessions[0]);
    filterVm.MessageDraft="未发出的草稿";
    var originalHistory=filterVm.History.Single();
    filterVm.SearchText="其他会话";
    Check(filterVm.Sessions.Count==1&&filterVm.Sessions[0].Id==filterB.Id,"search still filters list accurately");
    Check(filterVm.SelectedSession?.Id==filterA.Id&&filterVm.IsDetailOpen,"search retains open conversation outside results");
    Check(filterVm.MessageDraft=="未发出的草稿"&&ReferenceEquals(filterVm.History.Single(),originalHistory),"search preserves draft and loaded history identity");
    filterVm.SearchText="完全无结果";
    Check(filterVm.Sessions.Count==0&&filterVm.SelectedSession?.Id==filterA.Id&&filterVm.ComposerVisibility==Visibility.Visible,"empty search results retain active composer");
    service.Publish(service.Current with{Sessions=[filterA with{Title="已改名",RuntimeStatus="running"},filterB]});
    Check(filterVm.SelectedTitle=="已改名"&&filterVm.SelectedRuntimeStatus=="运行中","hidden current session receives latest metadata");
    var hiddenSelection=filterVm.SelectedSession;
    int restoredSelectionNotifications=0;
    filterVm.PropertyChanged+=(_,args)=>{if(args.PropertyName==nameof(filterVm.SelectedSession))restoredSelectionNotifications++;};
    filterVm.SearchText=string.Empty;
    Check(filterVm.SelectedSession?.Id==filterA.Id&&filterVm.Sessions.Count==2&&filterVm.MessageDraft=="未发出的草稿","clearing search restores row selection and draft");
    Check(ReferenceEquals(filterVm.SelectedSession,filterVm.Sessions.Single(row=>row.Id==filterA.Id)),"restored selection uses the actual visible list item for highlight");
    Check(restoredSelectionNotifications==1&&!ReferenceEquals(hiddenSelection,filterVm.SelectedSession),"reinserted row notifies one-way selected-item binding to restore highlight");
    filterVm.SearchText="其他会话";filterVm.OpenSession(filterVm.Sessions[0]);filterVm.MessageDraft="另一份草稿";
    Check(filterVm.SelectedSession?.Id==filterB.Id&&filterVm.History.Single().Text=="history:"+filterB.Id,"explicit filtered selection opens requested conversation");
    filterVm.SearchText=string.Empty;filterVm.OpenSession(filterVm.Sessions.First(row=>row.Id==filterA.Id));
    Check(filterVm.MessageDraft=="未发出的草稿","returning to first session retains its independent draft");
    filterVm.SearchText="其他会话";service.Publish(service.Current with{Sessions=[filterB]});
    Check(filterVm.SelectedSession is null&&filterVm.History.Count==0,"true removal still closes former conversation");
    filterVm.OpenSession(filterVm.Sessions[0]);service.Publish(service.Current with{Home=@"C:\OtherFilterHome"});
    Check(filterVm.SelectedSession is null&&filterVm.MessageDraft==string.Empty,"scope change still clears old selected conversation and draft");
    filterVm.Detach();
}
Console.WriteLine($"All {passed} session UI probes passed.");
