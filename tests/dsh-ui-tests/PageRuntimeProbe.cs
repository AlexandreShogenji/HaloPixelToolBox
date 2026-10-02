using System.Reflection;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

public static class PageRuntimeProbe
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(DshSessionsPage page, string name) => typeof(DshSessionsPage).GetField(name, Private)!.GetValue(page);
    private static bool Flag(DshSessionsPage page, string name) => (bool)Get(page, name)!;
    private static void Invoke(DshSessionsPage page, string name, params object[] args) => typeof(DshSessionsPage).GetMethod(name, Private)!.Invoke(page, args);
    private static void Wheel(DshSessionsPage page) => page.TestHistory.Send(UIElement.PointerWheelChangedEvent, new PointerRoutedEventArgs());
    public static void Run(Action<bool, string> check)
    {
        var service = HaloPixelToolBox.App.DshSessions;
        var first = new DshSessionSummary("SCROLL-A", "scroll A", "", DateTimeOffset.Now, "idle");
        var second = first with { Id = "SCROLL-B", Title = "scroll B" };
        service.HistoryReader = (id, _, _) => Task.FromResult(new DshHistoryPage(
            [new(1, "user", "command", null) { SessionId = id }, new(2, "assistant", "response", null) { SessionId = id }], null, false));
        service.Publish(new(true, "http://localhost", "connected", [first, second], null) { Home = @"D:\ScrollProbeHome" });
        var page = new DshSessionsPage();
        page.RaiseLoaded(); page.RaiseLoaded();
        check(page.TestHistory.HandlerCount == 5 && page.TestViewer.ViewSubscriptions == 1, "page repeated load does not duplicate input or viewport subscriptions");
        page.ViewModel.OpenSession(page.ViewModel.Sessions[0]);
        DispatcherQueue.Drain();
        check(page.TestHistory.ScrollCalls == 1 && Flag(page, "followLatest") && !Flag(page, "historyUserScrollPending"), "opening history scrolls latest and clears user state");

        var text = new FrameworkElement(); page.TestHistory.AddChild(text);
        page.TestHistory.Send(UIElement.PointerPressedEvent, new PointerRoutedEventArgs { OriginalSource = text });
        check(Flag(page, "followLatest") && !Flag(page, "historyUserScrollPending"), "clicking history text does not pause following");
        page.TestViewer.VerticalOffset = 200; page.TestViewer.RaiseViewChanged(false);
        check(Flag(page, "followLatest"), "automatic viewport movement without user input cannot disable following");

        page.TestViewer.VerticalOffset = 1000;
        Wheel(page);
        check(!Flag(page, "followLatest") && Flag(page, "historyUserScrollPending"), "wheel intent temporarily protects history from automatic scroll");
        ((DispatcherQueueTimer)Get(page, "historyUserScrollTimer")!).Fire();
        DispatcherQueue.Drain();
        check(Flag(page, "followLatest") && !Flag(page, "historyUserScrollPending"), "wheel at bottom with no viewport event restores following after settle");
        page.TestHistory.Send(UIElement.KeyDownEvent, new KeyRoutedEventArgs { Key = Windows.System.VirtualKey.End });
        ((DispatcherQueueTimer)Get(page, "historyUserScrollTimer")!).Fire(); DispatcherQueue.Drain();
        check(Flag(page, "followLatest"), "End key already at bottom does not permanently pause following");
        Wheel(page); page.TestViewer.VerticalOffset = 200; page.TestViewer.RaiseViewChanged(false);
        DispatcherQueue.Drain();
        check(!Flag(page, "followLatest") && !Flag(page, "historyUserScrollPending"), "actual user wheel upward disables following");
        var previousScrollCalls = page.TestHistory.ScrollCalls;
        page.ViewModel.VisibleHistory.Add(new(new(3, "assistant", "fresh", null) { SessionId = first.Id }));
        DispatcherQueue.Drain();
        check(page.TestHistory.ScrollCalls == previousScrollCalls && page.TestViewer.VerticalOffset == 200,
            "new message preserves viewport while user reads earlier history");
        Wheel(page); page.TestViewer.VerticalOffset = 1000; page.TestViewer.RaiseViewChanged(false);
        DispatcherQueue.Drain();
        check(Flag(page, "followLatest"), "scrolling back to bottom resumes following");
        previousScrollCalls = page.TestHistory.ScrollCalls;
        page.ViewModel.VisibleHistory.Insert(0, new(new(0, "user", "older", null) { SessionId = first.Id }));
        DispatcherQueue.Drain();
        check(page.TestHistory.ScrollCalls == previousScrollCalls, "prepending an older page never asks to scroll to latest");

        var scrollbar = new ScrollBar(); var thumb = new FrameworkElement(); scrollbar.AddChild(thumb); page.TestViewer.AddChild(scrollbar);
        page.TestHistory.Send(UIElement.PointerPressedEvent, new PointerRoutedEventArgs { OriginalSource = thumb, Pointer = new(77) });
        page.TestViewer.VerticalOffset = 250; page.TestViewer.RaiseViewChanged(false);
        check(Flag(page, "historyUserScrollPending"), "scrollbar drag stays user-owned until pointer release");
        page.TestHistory.Send(UIElement.PointerReleasedEvent, new PointerRoutedEventArgs { Pointer = new(77) });
        check(!Flag(page, "historyUserScrollPending") && !Flag(page, "followLatest"), "scrollbar release settles at earlier history without automatic follow");
        page.TestHistory.Send(UIElement.PointerPressedEvent, new PointerRoutedEventArgs { OriginalSource = thumb, Pointer = new(78) });
        page.TestHistory.Send(UIElement.PointerCanceledEvent, new PointerRoutedEventArgs { Pointer = new(78) });
        check(!Flag(page, "historyUserScrollPending"), "canceled scrollbar pointer clears user input state");

        page.TestViewer.BeginManipulation(); page.TestViewer.VerticalOffset = 1000; page.TestViewer.RaiseViewChanged(false);
        check(Flag(page, "historyUserScrollPending"), "touch panning owns viewport through manipulation completion");
        page.TestViewer.EndManipulation(); DispatcherQueue.Drain();
        check(Flag(page, "followLatest") && !Flag(page, "historyUserScrollPending"), "touch panning to bottom resumes following");

        Wheel(page);
        page.ViewModel.OpenSession(page.ViewModel.Sessions[1]); DispatcherQueue.Drain();
        check(!Flag(page, "historyUserScrollPending") && Flag(page, "followLatest")
            && ((DshHistoryListItem)page.TestHistory.LastScrolledItem!).Entry.SessionId == second.Id,
            "switching session discards old input and scrolls only new session latest");
        Wheel(page); page.ViewModel.VisibleHistory.Clear();
        check(!Flag(page, "historyUserScrollPending"), "history clear resets pending user input");
        page.ViewModel.VisibleHistory.Add(new(new(1, "assistant", "new scope", null) { SessionId = second.Id }));
        DispatcherQueue.Drain();
        check(Flag(page, "followLatest"), "fresh history after clear starts following latest");

        previousScrollCalls = page.TestHistory.ScrollCalls;
        page.ViewModel.VisibleHistory.Add(new(new(2, "assistant", "pending update", null) { SessionId = second.Id }));
        Invoke(page, "SessionsWorkspace_SizeChanged", page, new SizeChangedEventArgs(new(530, 450)));
        DispatcherQueue.Drain();
        check(page.TestHistory.ScrollCalls == previousScrollCalls, "queued scroll defers if resize begins before dispatcher callback");
        ((DispatcherQueueTimer)Get(page, "historyResizeTimer")!).Fire(); DispatcherQueue.Drain();
        check(page.TestHistory.ScrollCalls == previousScrollCalls + 1, "resize settle reveals latest after virtualization anchor work");

        Wheel(page); page.TestViewer.VerticalOffset = 250; page.TestViewer.RaiseViewChanged(false);
        previousScrollCalls = page.TestHistory.ScrollCalls;
        Invoke(page, "SessionsWorkspace_SizeChanged", page, new SizeChangedEventArgs(new(700, 500)));
        ((DispatcherQueueTimer)Get(page, "historyResizeTimer")!).Fire(); DispatcherQueue.Drain();
        check(page.TestHistory.ScrollCalls == previousScrollCalls && page.TestViewer.VerticalOffset == 250,
            "resize does not pull user away from earlier history");

        Wheel(page);
        var inputTimer = (DispatcherQueueTimer)Get(page, "historyUserScrollTimer")!;
        var resizeTimer = (DispatcherQueueTimer)Get(page, "historyResizeTimer")!;
        var viewer = page.TestViewer;
        page.RaiseUnloaded();
        check(page.TestHistory.HandlerCount == 0 && viewer.ViewSubscriptions == 0 && !inputTimer.IsRunning && !resizeTimer.IsRunning,
            "unload removes exact input delegates and stops both viewport timers");
        previousScrollCalls = page.TestHistory.ScrollCalls;
        inputTimer.Fire(true); resizeTimer.Fire(true); viewer.EndManipulation(); DispatcherQueue.Drain();
        check(page.TestHistory.ScrollCalls == previousScrollCalls, "late timer or viewport events cannot scroll an unloaded page");
        page.RaiseLoaded(); DispatcherQueue.Drain();
        check(page.TestHistory.HandlerCount == 5 && viewer.ViewSubscriptions == 1,
            "cached page reattach has one set of handlers");
        Wheel(page);
        inputTimer.Fire(true);
        check(Flag(page, "historyUserScrollPending"), "old input timer cannot consume new page attachment input");
        Invoke(page, "SessionsWorkspace_SizeChanged", page, new SizeChangedEventArgs(new(530, 450)));
        resizeTimer.Fire(true);
        check(Flag(page, "historyLayoutChanging"), "old resize timer cannot release new page layout guard");
        page.RaiseUnloaded();
    }
}
