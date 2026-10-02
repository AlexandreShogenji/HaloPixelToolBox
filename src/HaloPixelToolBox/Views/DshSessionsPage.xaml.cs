using System.ComponentModel;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Input;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;

namespace HaloPixelToolBox.Views;

public sealed partial class DshSessionsPage : Page
{
    private const double CompactWorkspaceWidth = 620;
    private const double CompactConnectionWidth = 580;
    private const double FollowLatestDistance = 64;
    private ScrollViewer? historyScrollViewer;
    private bool pageIsLoaded;
    private bool followLatest = true;
    private bool openingHistory = true;
    private bool latestScrollPending;
    private bool latestScrollQueued;
    private bool historyLayoutChanging;
    private bool historyUserScrollPending;
    private bool historyUserWasFollowing;
    private double historyUserInitialOffset;
    private bool historyDirectManipulationActive;
    private uint? historyScrollPointerId;
    private DispatcherQueueTimer? historyResizeTimer;
    private DispatcherQueueTimer? historyUserScrollTimer;
    private readonly PointerEventHandler historyWheelHandler;
    private readonly PointerEventHandler historyPointerPressedHandler;
    private readonly PointerEventHandler historyPointerReleasedHandler;
    private readonly KeyEventHandler historyKeyHandler;
    private readonly KeyEventHandler composerKeyHandler;
    private int scrollGeneration;
    private string? historySelectionId;
    private (string SessionId, long Sequence)? latestHistoryIdentity;
    private bool composerIsComposing;
    private bool composerCommitKeyPending;
    private int composerCompositionGeneration;
    private ContentDialog? sessionTitleDialog;

    public DshSessionsPageViewModel ViewModel { get; } = new();

    public DshSessionsPage()
    {
        historyWheelHandler = History_UserScroll;
        historyPointerPressedHandler = History_PointerPressed;
        historyPointerReleasedHandler = History_PointerReleased;
        historyKeyHandler = History_KeyDown;
        composerKeyHandler = Composer_KeyDown;
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (pageIsLoaded)
            return;
        pageIsLoaded = true;
        scrollGeneration++;
        historySelectionId = ViewModel.SelectedSession?.Id;
        latestHistoryIdentity = ViewModel.VisibleHistory.LastOrDefault()?.Identity;
        openingHistory = true;
        followLatest = true;
        ResetHistoryUserScroll();
        ResetComposerInput();
        latestScrollPending = ViewModel.VisibleHistory.Count > 0;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.VisibleHistory.CollectionChanged += History_CollectionChanged;
        HistoryListView.AddHandler(UIElement.PointerWheelChangedEvent, historyWheelHandler, true);
        HistoryListView.AddHandler(UIElement.PointerPressedEvent, historyPointerPressedHandler, true);
        HistoryListView.AddHandler(UIElement.PointerReleasedEvent, historyPointerReleasedHandler, true);
        HistoryListView.AddHandler(UIElement.PointerCanceledEvent, historyPointerReleasedHandler, true);
        HistoryListView.AddHandler(UIElement.KeyDownEvent, historyKeyHandler, true);
        ComposerTextBox.AddHandler(UIElement.KeyDownEvent, composerKeyHandler, true);
        ViewModel.Attach();
        UpdateWorkspaceLayout(SessionsWorkspace.ActualWidth);
        UpdateConnectionLayout(ConnectionLayout.ActualWidth);
        AttachHistoryScrollViewer();
        UpdateComposerHint();
        QueueLatestScroll();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!pageIsLoaded)
            return;
        pageIsLoaded = false;
        scrollGeneration++;
        latestScrollQueued = false;
        latestScrollPending = false;
        historyLayoutChanging = false;
        historyResizeTimer?.Stop();
        historyResizeTimer = null;
        ResetHistoryUserScroll();
        historyUserScrollTimer = null;
        ResetComposerInput();
        sessionTitleDialog?.Hide();
        taskDialog?.Hide();
        taskAnswerDrafts.Clear();
        HistoryListView.RemoveHandler(UIElement.PointerWheelChangedEvent, historyWheelHandler);
        HistoryListView.RemoveHandler(UIElement.PointerPressedEvent, historyPointerPressedHandler);
        HistoryListView.RemoveHandler(UIElement.PointerReleasedEvent, historyPointerReleasedHandler);
        HistoryListView.RemoveHandler(UIElement.PointerCanceledEvent, historyPointerReleasedHandler);
        HistoryListView.RemoveHandler(UIElement.KeyDownEvent, historyKeyHandler);
        ComposerTextBox.RemoveHandler(UIElement.KeyDownEvent, composerKeyHandler);
        if (historyScrollViewer is not null)
        {
            historyScrollViewer.ViewChanged -= HistoryScrollViewer_ViewChanged;
            historyScrollViewer.DirectManipulationStarted -= HistoryScrollViewer_DirectManipulationStarted;
            historyScrollViewer.DirectManipulationCompleted -= HistoryScrollViewer_DirectManipulationCompleted;
        }
        historyScrollViewer = null;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.VisibleHistory.CollectionChanged -= History_CollectionChanged;
        ViewModel.Detach();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.TaskSnapshot))
            taskAnswerDrafts.Synchronize(TaskDialogScope, ViewModel.TaskSnapshot);
        if (e.PropertyName is nameof(ViewModel.TaskSnapshot) or nameof(ViewModel.CanRespondToTask)
            or nameof(ViewModel.CanStartTask) or nameof(ViewModel.IsConnected))
            taskDialogContextChanged?.Invoke();
        if (e.PropertyName is nameof(ViewModel.IsDetailOpen) or nameof(ViewModel.IsCompactLayout))
            UpdateWorkspaceLayout(SessionsWorkspace.ActualWidth);
        if (e.PropertyName == nameof(ViewModel.SelectedSession) && historySelectionId != ViewModel.SelectedSession?.Id)
        {
            ResetHistoryUserScroll();
            ResetComposerInput();
            historySelectionId = ViewModel.SelectedSession?.Id;
            openingHistory = true;
            followLatest = true;
            latestScrollPending = ViewModel.VisibleHistory.Count > 0;
        }
        if (e.PropertyName is nameof(ViewModel.SelectedSession) or nameof(ViewModel.IsHistoryLoading)
            or nameof(ViewModel.IsDetailOpen) or nameof(ViewModel.IsCompactLayout))
            QueueLatestScroll();
        if (e.PropertyName == nameof(ViewModel.SendStatus))
            UpdateComposerHint();
        if (e.PropertyName == nameof(ViewModel.IsConnected))
            UpdateConnectionLayout(ConnectionLayout.ActualWidth);
    }

    private void UpdateComposerHint()
        => ComposerHintText.Visibility = ViewModel.SendStatusVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void ResetComposerInput()
    {
        composerIsComposing = false;
        composerCommitKeyPending = false;
        composerCompositionGeneration++;
    }

    private void Composer_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        composerCompositionGeneration++;
        composerIsComposing = true;
        composerCommitKeyPending = false;
    }

    private void Composer_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        composerIsComposing = false;
        composerCommitKeyPending = true;
        var composition = ++composerCompositionGeneration;
        var generation = scrollGeneration;
        // Some IMEs commit the candidate before the same Enter reaches KeyDown.
        // Keep that key out of the send path until the input event has finished.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (pageIsLoaded && generation == scrollGeneration && composition == composerCompositionGeneration && !composerIsComposing)
                composerCommitKeyPending = false;
        });
    }

    private async void Composer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // AcceptsReturn can mark Enter handled before the application receives
        // it; the routed handler intentionally observes those events as well.
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        if (composerIsComposing)
            return;
        if (composerCommitKeyPending)
        {
            e.Handled = true;
            return;
        }
        if ((InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
            return;
        e.Handled = true;
        if (ViewModel.SendMessageCommand.CanExecute(null))
            await ViewModel.SendMessageCommand.ExecuteAsync(null);
    }

    private void Composer_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !composerIsComposing)
            composerCommitKeyPending = false;
    }

    private async void NewSession_Click(object sender, RoutedEventArgs e) => await ShowSessionTitleDialogAsync(rename: false);
    private async void RenameSession_Click(object sender, RoutedEventArgs e) => await ShowSessionTitleDialogAsync(rename: true);

    private static bool IsValidSessionTitle(string title)
        => title.Trim().Length is >= 1 and <= 200 && !title.Any(char.IsControl);

    private async Task ShowSessionTitleDialogAsync(bool rename)
    {
        if (!pageIsLoaded || XamlRoot is null || sessionTitleDialog is not null
            || taskDialog is not null
            || (rename ? !ViewModel.CanRenameSession : !ViewModel.CanCreateSession))
            return;
        var generation = scrollGeneration;
        var selectedId = ViewModel.SelectedSession?.Id;
        var home = App.DshSessions.Current.Home;
        var profile = DisplayFeatureProfile.DshProfileName;
        var titleInput = new TextBox
        {
            Text = rename ? ViewModel.SelectedSession?.Session.Title ?? string.Empty : string.Empty,
            MaxLength = 200,
            PlaceholderText = "会话名称",
            AcceptsReturn = false
        };
        var validation = new TextBlock { Text = "1–200 个字符，不能包含控制字符。", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(titleInput);
        content.Children.Add(validation);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = rename ? "重命名会话" : "新建会话",
            PrimaryButtonText = rename ? "保存" : "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
            IsPrimaryButtonEnabled = IsValidSessionTitle(titleInput.Text)
        };
        titleInput.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = IsValidSessionTitle(titleInput.Text);
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !IsValidSessionTitle(titleInput.Text);
        dialog.Opened += (_, _) => { titleInput.Focus(FocusState.Programmatic); titleInput.SelectAll(); };
        sessionTitleDialog = dialog;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !IsValidSessionTitle(titleInput.Text)
                || !pageIsLoaded || generation != scrollGeneration)
                return;
            if (!string.Equals(home, App.DshSessions.Current.Home, StringComparison.OrdinalIgnoreCase)
                || profile != DisplayFeatureProfile.DshProfileName || (rename && selectedId != ViewModel.SelectedSession?.Id))
            {
                ViewModel.SessionOperationStatus = "会话或数据目录已改变，请重新操作。";
                return;
            }
            var title = titleInput.Text.Trim();
            if (rename)
            {
                if (ViewModel.RenameSessionCommand.CanExecute(title))
                    await ViewModel.RenameSessionCommand.ExecuteAsync(title);
            }
            else if (ViewModel.NewSessionCommand.CanExecute(title))
                await ViewModel.NewSessionCommand.ExecuteAsync(title);
        }
        catch (Exception exception)
        {
            if (pageIsLoaded && generation == scrollGeneration)
                ViewModel.SessionOperationStatus = $"无法打开或完成会话操作：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(sessionTitleDialog, dialog))
                sessionTitleDialog = null;
        }
    }

    private void HistoryListView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!pageIsLoaded)
            return;
        AttachHistoryScrollViewer();
        QueueLatestScroll();
    }

    private void HistoryListView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!pageIsLoaded || !followLatest)
            return;
        latestScrollPending = ViewModel.VisibleHistory.Count > 0;
        QueueLatestScroll();
    }

    private void AttachHistoryScrollViewer()
    {
        var viewer = FindScrollViewer(HistoryListView);
        if (viewer is null || ReferenceEquals(viewer, historyScrollViewer))
            return;
        if (historyScrollViewer is not null)
        {
            historyScrollViewer.ViewChanged -= HistoryScrollViewer_ViewChanged;
            historyScrollViewer.DirectManipulationStarted -= HistoryScrollViewer_DirectManipulationStarted;
            historyScrollViewer.DirectManipulationCompleted -= HistoryScrollViewer_DirectManipulationCompleted;
        }
        historyScrollViewer = viewer;
        viewer.ViewChanged += HistoryScrollViewer_ViewChanged;
        viewer.DirectManipulationStarted += HistoryScrollViewer_DirectManipulationStarted;
        viewer.DirectManipulationCompleted += HistoryScrollViewer_DirectManipulationCompleted;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer)
                return viewer;
            if (FindScrollViewer(child) is { } nested)
                return nested;
        }
        return null;
    }

    private void HistoryScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!historyUserScrollPending || historyLayoutChanging
            || historyScrollViewer is null || historyScrollViewer.ViewportHeight <= 0)
            return;
        if (e.IsIntermediate)
            RestartHistoryUserScrollTimer();
        else if (!historyDirectManipulationActive && historyScrollPointerId is null)
            CompleteHistoryUserScroll();
    }

    private bool IsHistoryNearLatest()
    {
        if (historyScrollViewer is null || historyScrollViewer.ViewportHeight <= 0)
            return followLatest;
        // Virtualized lists estimate their extent. Prefer the realized last row
        // when available so an extent correction does not disable following.
        if (ViewModel.VisibleHistory.LastOrDefault() is { } latest
            && HistoryListView.ContainerFromItem(latest) is FrameworkElement { IsLoaded: true, ActualHeight: > 0 } container)
        {
            var bottom = container.TransformToVisual(historyScrollViewer)
                .TransformPoint(new Windows.Foundation.Point(0, container.ActualHeight)).Y;
            return bottom >= 0 && bottom <= historyScrollViewer.ViewportHeight + FollowLatestDistance;
        }
        return historyScrollViewer.ScrollableHeight - historyScrollViewer.VerticalOffset <= FollowLatestDistance;
    }

    private void History_UserScroll(object sender, PointerRoutedEventArgs e)
        => BeginHistoryUserScroll();

    private void History_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // A mouse click on text/expanders is not a scroll. Thumb and track
        // presses are; touch panning is reported by direct manipulation below.
        for (var source = e.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, HistoryListView);
             source = VisualTreeHelper.GetParent(source))
            if (source is ScrollBar)
            {
                historyScrollPointerId = e.Pointer.PointerId;
                BeginHistoryUserScroll();
                return;
            }
    }

    private void History_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (historyScrollPointerId != e.Pointer.PointerId)
            return;
        historyScrollPointerId = null;
        CompleteHistoryUserScroll();
    }

    private void HistoryScrollViewer_DirectManipulationStarted(object? sender, object e)
    {
        historyDirectManipulationActive = true;
        BeginHistoryUserScroll();
    }

    private void HistoryScrollViewer_DirectManipulationCompleted(object? sender, object e)
    {
        historyDirectManipulationActive = false;
        CompleteHistoryUserScroll();
    }

    private void History_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down
            or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown
            or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End)
            BeginHistoryUserScroll();
    }

    private void BeginHistoryUserScroll()
    {
        if (!pageIsLoaded || historyScrollViewer is null || ViewModel.VisibleHistory.Count == 0)
            return;
        if (!historyUserScrollPending)
        {
            historyUserWasFollowing = followLatest;
            historyUserInitialOffset = historyScrollViewer.VerticalOffset;
        }
        historyUserScrollPending = true;
        openingHistory = false;
        followLatest = false;
        latestScrollPending = false;
        historyResizeTimer?.Stop();
        historyLayoutChanging = false;
        RestartHistoryUserScrollTimer();
    }

    private void RestartHistoryUserScrollTimer()
    {
        if (historyUserScrollTimer is null)
        {
            var generation = scrollGeneration;
            historyUserScrollTimer = DispatcherQueue.CreateTimer();
            historyUserScrollTimer.Interval = TimeSpan.FromMilliseconds(200);
            historyUserScrollTimer.IsRepeating = false;
            historyUserScrollTimer.Tick += (_, _) =>
            {
                if (pageIsLoaded && generation == scrollGeneration && !historyDirectManipulationActive && historyScrollPointerId is null)
                    CompleteHistoryUserScroll();
            };
        }
        historyUserScrollTimer.Stop();
        historyUserScrollTimer.Start();
    }

    private void CompleteHistoryUserScroll()
    {
        if (!pageIsLoaded || !historyUserScrollPending || historyScrollViewer is null || historyLayoutChanging)
            return;
        var moved = Math.Abs(historyScrollViewer.VerticalOffset - historyUserInitialOffset) > 0.5;
        var wasFollowing = historyUserWasFollowing;
        ResetHistoryUserScroll();
        // Wheel/key input at an edge can produce no ViewChanged event. It must
        // not leave following permanently disabled when nothing actually moved.
        followLatest = moved ? IsHistoryNearLatest() : wasFollowing;
        latestScrollPending = followLatest && ViewModel.VisibleHistory.Count > 0;
        QueueLatestScroll();
    }

    private void ResetHistoryUserScroll()
    {
        historyUserScrollTimer?.Stop();
        historyUserScrollPending = false;
        historyDirectManipulationActive = false;
        historyScrollPointerId = null;
    }

    private void History_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var latest = ViewModel.VisibleHistory.LastOrDefault();
        if (latest is null)
        {
            ResetHistoryUserScroll();
            // Session changes and aggregate membership resets clear the rows first.
            latestHistoryIdentity = null;
            openingHistory = true;
            latestScrollPending = false;
            return;
        }
        var latestChanged = latestHistoryIdentity != latest.Identity;
        var latestReplaced = e.Action == NotifyCollectionChangedAction.Replace
            && e.NewStartingIndex == ViewModel.VisibleHistory.Count - 1;
        // Prepending older pages keeps the latest identity unchanged. The items
        // panel anchors visible items, so paging never asks to scroll to the end.
        if (openingHistory || (followLatest && (latestChanged || latestReplaced)))
            latestScrollPending = true;
        latestHistoryIdentity = latest.Identity;
        QueueLatestScroll();
    }

    private void QueueLatestScroll()
    {
        if (!pageIsLoaded || historyLayoutChanging || historyUserScrollPending || !latestScrollPending || latestScrollQueued || ViewModel.IsHistoryLoading
            || ViewModel.DetailsPaneVisibility != Visibility.Visible || ViewModel.VisibleHistory.Count == 0)
            return;
        latestScrollQueued = true;
        var generation = scrollGeneration;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!pageIsLoaded || generation != scrollGeneration)
                return;
            latestScrollQueued = false;
            if (historyLayoutChanging || historyUserScrollPending || !latestScrollPending || ViewModel.IsHistoryLoading || ViewModel.VisibleHistory.Count == 0
                || ViewModel.DetailsPaneVisibility != Visibility.Visible)
                return;
            ResetHistoryUserScroll();
            HistoryListView.ScrollIntoView(ViewModel.VisibleHistory[^1], ScrollIntoViewAlignment.Leading);
            openingHistory = false;
            latestScrollPending = false;
            followLatest = true;
        });
    }

    private void SessionsWorkspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResetHistoryUserScroll();
        var wasFollowing = followLatest;
        historyLayoutChanging = pageIsLoaded;
        UpdateWorkspaceLayout(e.NewSize.Width);
        if (!pageIsLoaded)
            return;
        if (wasFollowing)
            latestScrollPending = ViewModel.VisibleHistory.Count > 0;
        if (historyResizeTimer is null)
        {
            var generation = scrollGeneration;
            historyResizeTimer = DispatcherQueue.CreateTimer();
            historyResizeTimer.Interval = TimeSpan.FromMilliseconds(100);
            historyResizeTimer.IsRepeating = false;
            historyResizeTimer.Tick += (_, _) =>
            {
                if (!pageIsLoaded || generation != scrollGeneration)
                    return;
                historyLayoutChanging = false;
                QueueLatestScroll();
            };
        }
        // The virtualizing panel restores its anchor after resize. Wait for the
        // resize to settle before asking it to reveal the last message.
        historyResizeTimer.Stop();
        historyResizeTimer.Start();
    }

    private void SessionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DshSessionListItem session)
            ViewModel.OpenSession(session);
    }

    private void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Removing a row during search must not clear the open conversation.
        // Added selections still support keyboard navigation of the list.
        if (pageIsLoaded && e.AddedItems.OfType<DshSessionListItem>().LastOrDefault() is { } session
            && session.Id != ViewModel.SelectedSession?.Id)
            ViewModel.OpenSession(session);
    }

    private void UpdateWorkspaceLayout(double width)
    {
        if (SessionListColumn is null || SessionDetailsColumn is null)
            return;
        var compact = width < CompactWorkspaceWidth;
        ViewModel.IsCompactLayout = compact;
        if (ComposerTextBox is not null)
        {
            ComposerTextBox.MinHeight = compact ? 52 : 64;
            ComposerTextBox.MaxHeight = compact ? 120 : 160;
        }
        SessionListColumn.Width = compact
            ? new GridLength(ViewModel.IsDetailOpen ? 0 : 1, ViewModel.IsDetailOpen ? GridUnitType.Pixel : GridUnitType.Star)
            : new GridLength(Math.Clamp(width * 0.28, 240, 300));
        SessionDetailsColumn.Width = compact && !ViewModel.IsDetailOpen
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        SessionsWorkspace.ColumnSpacing = compact ? 0 : 12;
    }

    private void ConnectionLayout_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateConnectionLayout(e.NewSize.Width);

    private void UpdateConnectionLayout(double width)
    {
        if (ConnectionActions is null)
            return;
        var compact = width < CompactConnectionWidth;
        Grid.SetRow(ConnectionActions, compact ? 1 : 0);
        Grid.SetColumn(ConnectionActions, compact ? 0 : 1);
        Grid.SetColumnSpan(ConnectionActions, compact ? 2 : 1);
        var twoRows = compact && (!ViewModel.IsConnected || width < 320);
        ConnectionActions.HorizontalAlignment = twoRows ? HorizontalAlignment.Stretch : compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        ConnectionActionColumn0.Width = ConnectionActionColumn1.Width = twoRows
            ? new GridLength(1, GridUnitType.Star) : new GridLength(1, GridUnitType.Auto);
        ConnectionActionColumn2.Width = ConnectionActionColumn3.Width = twoRows
            ? new GridLength(0) : new GridLength(1, GridUnitType.Auto);
        SetActionPosition(NewTaskButton, 0, 0, 0);
        SetActionPosition(VoiceActionButton, 1, 0, 1);
        SetActionPosition(ConnectDshButton, 2, 1, 0);
        SetActionPosition(RefreshSessionsButton, 3, 1, 1);

        void SetActionPosition(FrameworkElement action, int wideColumn, int compactRow, int compactColumn)
        {
            Grid.SetRow(action, twoRows ? compactRow : 0);
            Grid.SetColumn(action, twoRows ? compactColumn : wideColumn);
        }
    }
}
