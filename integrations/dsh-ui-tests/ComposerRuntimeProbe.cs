using System.Reflection;
using HaloPixelToolBox;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

public static class ComposerRuntimeProbe
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(DshSessionsPage page, string name) => typeof(DshSessionsPage).GetField(name, Private)!.GetValue(page);
    private static bool Flag(DshSessionsPage page, string name) => (bool)Get(page, name)!;
    private static object? Invoke(DshSessionsPage page, string name, params object[] args) => typeof(DshSessionsPage).GetMethod(name, Private)!.Invoke(page, args);
    private static bool ValidTitle(string text) => (bool)typeof(DshSessionsPage).GetMethod("IsValidSessionTitle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [text])!;
    private static TextBox DialogInput(ContentDialog dialog) => ((StackPanel)dialog.Content!).Children.OfType<TextBox>().Single();
    private static KeyRoutedEventArgs Enter(DshSessionsPage page, bool handled = false)
    {
        var key = new KeyRoutedEventArgs { Key = Windows.System.VirtualKey.Enter, Handled = handled };
        page.TestComposer.Send(UIElement.KeyDownEvent, key);
        return key;
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        if (!condition()) throw new InvalidOperationException("Composer probe timed out");
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var service = App.DshSessions;
        var first = new DshSessionSummary("CHAT-A", "会话 A", "", DateTimeOffset.Now, "idle");
        var second = first with { Id = "CHAT-B", Title = "会话 B" };
        service.HistoryReader = (id, _, _) => Task.FromResult(new DshHistoryPage([new(1, "assistant", "hello", null) { SessionId = id }], null, false));
        service.Publish(new(true, "http://localhost", "connected", [first, second], null)
            { Home = @"D:\ComposerProbeHome", Capabilities = new(true, true, true, true, true) });
        var page = new DshSessionsPage(); page.RaiseLoaded(); page.ViewModel.OpenSession(page.ViewModel.Sessions[0]); DispatcherQueue.Drain();
        check(page.TestComposer.HandlerCount == 1, "composer registers one handled-events-too routed key listener");
        int sends = 0;
        string? sentId = null, sentText = null;
        service.MessageSender = (id, text, _) => { sends++; sentId = id; sentText = text; return Task.FromResult(new DshDeviceCommandResult(true, "done", "done", id, [], []) { Accepted = true, Completed = true }); };
        page.ViewModel.MessageDraft = "中文候选";
        Invoke(page, "Composer_TextCompositionStarted", page.TestComposer, new TextCompositionStartedEventArgs());
        check(!Enter(page).Handled && sends == 0, "Enter while IME composing stays with input method and cannot send");
        check(Enter(page, handled: true).Handled && sends == 0, "handled IME composition Return cannot send");
        Invoke(page, "Composer_TextCompositionEnded", page.TestComposer, new TextCompositionEndedEventArgs());
        check(Enter(page).Handled && sends == 0, "candidate commit Enter cannot leak into send after composition ended");
        check(Enter(page, handled: true).Handled && sends == 0, "handled candidate commit Return is also kept out of send path");
        Invoke(page, "Composer_KeyUp", page.TestComposer, new KeyRoutedEventArgs { Key = Windows.System.VirtualKey.Enter });
        Enter(page); await Until(() => !page.ViewModel.IsSending); DispatcherQueue.Drain();
        check(sends == 1 && sentId == first.Id && sentText == "中文候选", "fresh Enter after candidate key release sends raw draft to selected stable session");
        page.ViewModel.MessageDraft = "TextBox 已处理 Return";
        Enter(page, handled: true); await Until(() => !page.ViewModel.IsSending); DispatcherQueue.Drain();
        check(sends == 2 && sentText == "TextBox 已处理 Return", "handled Return from AcceptsReturn TextBox still sends once");
        page.ViewModel.MessageDraft = "多行草稿";
        InputKeyboardSource.ShiftState = Windows.UI.Core.CoreVirtualKeyStates.Down;
        check(!Enter(page).Handled && sends == 2 && page.ViewModel.MessageDraft == "多行草稿", "Shift Enter remains a normal multiline edit");
        Enter(page, handled: true);
        check(sends == 2 && page.ViewModel.MessageDraft == "多行草稿", "already-handled Shift Return stays multiline and does not send");
        InputKeyboardSource.ShiftState = Windows.UI.Core.CoreVirtualKeyStates.None;
        var arrow = new KeyRoutedEventArgs { Key = Windows.System.VirtualKey.Up };
        Invoke(page, "Composer_KeyDown", page.TestComposer, arrow);
        check(!Flag(page, "historyUserScrollPending"), "composer navigation keys do not mark history as manually scrolled");

        Invoke(page, "Composer_TextCompositionStarted", page.TestComposer, new TextCompositionStartedEventArgs());
        Invoke(page, "Composer_TextCompositionEnded", page.TestComposer, new TextCompositionEndedEventArgs());
        DispatcherQueue.Drain();
        check(!Flag(page, "composerCommitKeyPending"), "mouse-accepted IME candidate does not swallow next independent Enter");
        var pendingSend = new TaskCompletionSource<DshDeviceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.MessageSender = (id, text, _) => { sends++; return pendingSend.Task; };
        Enter(page);
        check(page.ViewModel.IsSending && Enter(page).Handled && sends == 3, "second Enter while sending cannot submit duplicate command");
        page.ViewModel.MessageDraft = "发送期间的新草稿";
        pendingSend.SetResult(new(true, "done", "done", first.Id, [], []) { Accepted = true, Completed = true });
        await Until(() => !page.ViewModel.IsSending);
        check(page.ViewModel.MessageDraft == "发送期间的新草稿", "new edits survive completion of already submitted draft");

        Invoke(page, "Composer_TextCompositionStarted", page.TestComposer, new TextCompositionStartedEventArgs());
        page.ViewModel.OpenSession(page.ViewModel.Sessions.Single(row => row.Id == second.Id)); DispatcherQueue.Drain();
        check(!Flag(page, "composerIsComposing") && !Flag(page, "composerCommitKeyPending"), "session switch clears previous composer composition state");
        Invoke(page, "UpdateWorkspaceLayout", 530d);
        check(page.TestComposer.MinHeight == 52 && page.TestComposer.MaxHeight == 120, "narrow composer limits multiline height to preserve chat viewport");
        Invoke(page, "UpdateWorkspaceLayout", 800d);
        check(page.TestComposer.MinHeight == 64 && page.TestComposer.MaxHeight == 160, "wide composer provides larger edit area");
        var connectionActions = (Grid)Get(page, "ConnectionActions")!;
        Invoke(page, "UpdateConnectionLayout", 420d);
        check(Grid.TestRow(connectionActions) == 1 && Grid.TestSpan(connectionActions) == 2 && connectionActions.HorizontalAlignment == HorizontalAlignment.Left,
            "narrow header places all actions on a separate full-width row");
        Invoke(page, "UpdateConnectionLayout", 800d);
        check(Grid.TestRow(connectionActions) == 0 && Grid.TestSpan(connectionActions) == 1, "wide header aligns actions beside title");
        check(ValidTitle("  会话标题  ") && ValidTitle(new string('字', 200)) && !ValidTitle(new string('字', 201))
            && !ValidTitle(" ") && !ValidTitle("标题\n") && !ValidTitle("标题\t"), "dialog title validation enforces 1 to 200 characters and rejects controls");

        int creations = 0, renames = 0;
        string? createdTitle = null, renamedId = null;
        service.SessionCreator = (title, _) =>
        {
            creations++; createdTitle = title;
            var created = first with { Id = "NEW-CHAT", Title = title };
            service.Publish(service.Current with { Sessions = service.Current.Sessions.Append(created).ToArray() });
            return Task.FromResult(created);
        };
        service.SessionRenamer = (id, title, _) =>
        {
            renames++; renamedId = id;
            service.Publish(service.Current with { Sessions = service.Current.Sessions.Select(row => row.Id == id ? row with { Title = title } : row).ToArray() });
            return Task.CompletedTask;
        };
        ContentDialog.NextShow = dialog => { DialogInput(dialog).Text = "  新标题  "; check(dialog.ValidatePrimary(), "dialog primary becomes enabled for valid edited title"); return Task.FromResult(ContentDialogResult.Primary); };
        await (Task)Invoke(page, "ShowSessionTitleDialogAsync", false)!;
        check(creations == 1 && createdTitle == "新标题" && page.ViewModel.SelectedSession?.Session.Id == "NEW-CHAT", "new dialog creates trimmed title and opens confirmed session");
        ContentDialog.NextShow = dialog =>
        {
            check(DialogInput(dialog).Text == "新标题", "rename dialog starts with full real selected title");
            DialogInput(dialog).Text = "重命名后"; return Task.FromResult(ContentDialogResult.Primary);
        };
        await (Task)Invoke(page, "ShowSessionTitleDialogAsync", true)!;
        check(renames == 1 && renamedId == "NEW-CHAT" && page.ViewModel.SelectedTitle == "重命名后", "rename dialog targets stable selected session and refreshes title");
        ContentDialog.NextShow = dialog => { DialogInput(dialog).Text = "无效\n标题"; return Task.FromResult(ContentDialogResult.Primary); };
        await (Task)Invoke(page, "ShowSessionTitleDialogAsync", false)!;
        check(creations == 1, "invalid title is rejected even if dialog reports primary result");
        ContentDialog.NextShow = dialog =>
        {
            DialogInput(dialog).Text = "不应写入";
            service.Publish(service.Current with { Home = @"D:\ChangedDuringDialog" });
            return Task.FromResult(ContentDialogResult.Primary);
        };
        await (Task)Invoke(page, "ShowSessionTitleDialogAsync", true)!;
        check(renames == 1 && page.ViewModel.SessionOperationStatus.Contains("已改变"), "directory change while dialog open rejects rename instead of targeting new scope");
        page.ViewModel.OpenSession(page.ViewModel.Sessions[0]);
        ContentDialog.NextShow = dialog =>
        {
            DialogInput(dialog).Text = "不应创建"; DisplayFeatureProfile.DshProfileName = "other-profile";
            return Task.FromResult(ContentDialogResult.Primary);
        };
        await (Task)Invoke(page, "ShowSessionTitleDialogAsync", false)!;
        check(creations == 1, "profile change while dialog open rejects create");
        DisplayFeatureProfile.DshProfileName = "default";

        ContentDialog.NextShow = null;
        var dialogPending = (Task)Invoke(page, "ShowSessionTitleDialogAsync", false)!;
        var shownDialog = ContentDialog.Last;
        await (Task)Invoke(page, "ShowSessionTitleDialogAsync", false)!;
        check(ReferenceEquals(ContentDialog.Last, shownDialog) && !dialogPending.IsCompleted, "repeated create click cannot open a second dialog");
        page.RaiseUnloaded(); await dialogPending;
        check(creations == 1 && Get(page, "sessionTitleDialog") is null && page.TestComposer.HandlerCount == 0,
            "page unload closes pending title dialog and unregisters composer key without executing mutation");
        DispatcherQueue.Drain();
        ContentDialog.NextShow = null; InputKeyboardSource.ShiftState = Windows.UI.Core.CoreVirtualKeyStates.None;
    }
}
