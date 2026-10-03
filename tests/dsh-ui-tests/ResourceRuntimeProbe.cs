using HaloPixelToolBox;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services;
using HaloPixelToolBox.ViewModels;
using Microsoft.UI.Xaml;

public static class ResourceRuntimeProbe
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        WindowActivityService.SetVisibility(true);
        App.DshTasks.Reset();
        var service = App.DshSessions;
        var session = new DshSessionSummary("RESOURCE", "resource probe", "", DateTimeOffset.Now, "idle");
        service.Publish(new(true, "local", "connected", [session], null) { Home = @"C:\ResourceProbe" });
        var latest = 1000;
        var reads = 0;
        var cursors = new List<long?>();
        service.HistoryReader = (id, before, _) =>
        {
            reads++;
            cursors.Add(before);
            var end = (int)(before - 1 ?? latest);
            var start = Math.Max(1, end - 99);
            return Task.FromResult(new DshHistoryPage(
                Enumerable.Range(start, end - start + 1).Select(i => new DshHistoryEntry(i, "assistant", "message " + i, null) { SessionId = id }).ToArray(),
                start, start > 1));
        };
        var vm = new DshSessionsPageViewModel();
        vm.Attach();
        vm.OpenSession(vm.Sessions[0]);
        vm.MessageDraft = "保留草稿";
        for (var i = 0; i < 6; i++) await vm.LoadEarlierCommand.ExecuteAsync(null);
        check(vm.History.Count == 200 && vm.History.First().Sequence == 301 && vm.History.Last().Sequence == 500,
            "paging older history keeps a bounded contiguous window");
        check(cursors.SequenceEqual(new long?[] { null, 901, 801, 701, 601, 501, 401 }),
            "older history never skips or invents server cursors");
        check(vm.LoadLatestVisibility == Visibility.Visible && vm.CanLoadLatest,
            "trimmed older history exposes return to latest");
        latest = 1100;
        var beforeUpdate = reads;
        service.Publish(service.Current with { Sessions = [session with { UpdatedAt = session.UpdatedAt.AddSeconds(1) }] });
        check(reads == beforeUpdate && vm.History.First().Sequence == 301,
            "background events do not mix a live tail into older window");
        await vm.LoadLatestCommand.ExecuteAsync(null);
        check(vm.History.Count == 100 && vm.History.Last().Sequence == latest && vm.LoadLatestVisibility == Visibility.Collapsed,
            "return to latest reloads live history and clears historical mode");

        // A hidden window cancels an in-flight UI read, but keeps the host and
        // draft alive. A backend ignoring cancellation must not apply stale text.
        var pending = new TaskCompletionSource<DshHistoryPage>();
        CancellationToken pendingToken = default;
        service.HistoryReader = (_, _, ct) => { reads++; pendingToken = ct; return pending.Task; };
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        WindowActivityService.SetVisibility(false);
        check(pendingToken.IsCancellationRequested && !vm.IsHistoryLoading && service.Disconnects == 0,
            "hiding cancels page reads without disconnecting DSH");
        beforeUpdate = reads;
        service.Publish(service.Current with { Sessions = [session with { UpdatedAt = session.UpdatedAt.AddSeconds(2) }] });
        pending.SetResult(new([new(99999, "assistant", "stale hidden read", null)], null, false));
        await refresh;
        check(reads == beforeUpdate && vm.History.All(row => row.Sequence != 99999),
            "hidden page makes no history reads and rejects late cancelled results");
        service.HistoryReader = (id, _, _) =>
        {
            reads++;
            return Task.FromResult(new DshHistoryPage([new(1101, "assistant", "resumed", null) { SessionId = id }], null, false));
        };
        WindowActivityService.SetVisibility(true);
        check(reads > beforeUpdate && vm.History.Last().Sequence == 1101 && vm.MessageDraft == "保留草稿",
            "showing refreshes promptly and preserves unsent draft");
        vm.Detach();
        check(vm.History.Count == 0 && vm.VisibleHistory.Count == 0 && vm.MessageDraft == "保留草稿",
            "cached page releases message bodies on navigation but retains draft");
        beforeUpdate = reads;
        WindowActivityService.SetVisibility(false);
        WindowActivityService.SetVisibility(true);
        check(reads == beforeUpdate, "detached page has no visibility subscription");

        // Continuous latest updates also stay bounded. Grouped cursors are
        // intentionally opaque and must survive overflow unchanged.
        session = session with { Id = "DEVICE", IsDeviceControl = true };
        service.Publish(new(true, "local", "connected", [session], null)
            { Home = @"C:\ResourceGroupProbe", DeviceSessionId = session.Id });
        var pageNumber = 0;
        string? receivedCursor = null;
        service.DeviceHistoryReader = (ids, cursor, _) =>
        {
            receivedCursor = cursor;
            var start = pageNumber * 100 + 1;
            return Task.FromResult(new DshDeviceHistoryPage(
                Enumerable.Range(start, 100).Select(i => new DshHistoryEntry(i, "user", "body", null) { SessionId = ids[0] }).ToArray(),
                "opaque-cursor-" + pageNumber, true));
        };
        vm = new DshSessionsPageViewModel();
        vm.Attach();
        for (pageNumber = 1; pageNumber <= 5; pageNumber++) await vm.RefreshCommand.ExecuteAsync(null);
        check(vm.History.Count == 100 && vm.History.First().Sequence == 501 && vm.History.Last().Sequence == 600,
            "live history overflow retains complete latest page with its cursor");
        await vm.LoadEarlierCommand.ExecuteAsync(null);
        check(receivedCursor == "opaque-cursor-5", "live overflow forwards exact replacement page cursor");
        service.DeviceHistoryReader = (ids, cursor, _) =>
        {
            receivedCursor = cursor;
            return Task.FromResult(new DshDeviceHistoryPage(Enumerable.Range(1, 650)
                .Select(i => new DshHistoryEntry(i, "user", "expanded source suffix", null) { SessionId = ids[0] }).ToArray(),
                "expanded-page-cursor", true));
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        check(vm.History.Count == 650 && vm.History.Last().Sequence == 650,
            "one expanded grouped page is kept whole rather than losing records behind its cursor");
        await vm.LoadEarlierCommand.ExecuteAsync(null);
        check(receivedCursor == "expanded-page-cursor" && vm.History.Count == 650,
            "expanded grouped page keeps exact paging cursor without accumulating another copy");
        vm.Detach();

        session = session with { Id = "BYTES", IsDeviceControl = false };
        service.Publish(new(true, "local", "connected", [session], null) { Home = @"C:\ResourceByteProbe" });
        service.HistoryReader = (id, before, _) =>
        {
            var end = (int)(before - 1 ?? 1000);
            var start = end - 79;
            return Task.FromResult(new DshHistoryPage(Enumerable.Range(start, 80)
                .Select(i => new DshHistoryEntry(i, "assistant", new string('长', 15000), null) { SessionId = id }).ToArray(), start, true));
        };
        vm = new DshSessionsPageViewModel(); vm.Attach(); vm.OpenSession(vm.Sessions[0]);
        await vm.LoadEarlierCommand.ExecuteAsync(null);
        check(vm.History.Count < 160 && vm.History.Sum(row => 2L * (row.Entry.Text.Length + row.Entry.SessionId.Length
                + row.Entry.Role.Length + row.Entry.Kind.Length)) <= DshSessionsPageViewModel.MaximumHistoryTextBytes,
            "large messages obey retained text-byte budget before row limit");
        check(vm.CanLoadLatest && vm.CanLoadEarlier, "byte-limited window remains navigable in both directions");
        vm.Detach();

        service.Publish(service.Current with { Capabilities = new(CanSendMessages: true) });
        vm = new DshSessionsPageViewModel(); vm.Attach(); vm.OpenSession(vm.Sessions[0]);
        await vm.LoadEarlierCommand.ExecuteAsync(null);
        var accepted = new TaskCompletionSource<DshDeviceCommandResult>();
        service.MessageSender = (_, _, _) => accepted.Task;
        vm.MessageDraft = "从历史页发送";
        var sending = vm.SendMessageCommand.ExecuteAsync(null);
        WindowActivityService.SetVisibility(false);
        accepted.SetResult(new(true, "done", "done", session.Id, [], []) { Accepted = true, Completed = true });
        await sending;
        service.HistoryReader = (id, _, _) => Task.FromResult(new DshHistoryPage(
            [new(1001, "assistant", "new reply after hidden send", null) { SessionId = id }], null, false));
        WindowActivityService.SetVisibility(true);
        check(vm.History.Single().Sequence == 1001 && vm.MessageDraft == string.Empty && !vm.CanLoadLatest,
            "accepted send while hidden leaves historical mode and shows new reply on restore");
        vm.Detach();
    }
}
