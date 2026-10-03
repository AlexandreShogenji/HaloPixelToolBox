using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Services;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;

namespace HaloPixelToolBox.ViewModels;

public partial class DshSessionsPageViewModel : ViewModelBase
{
    public const string DeviceHistoryId = "halo:device-control-history";
    internal const int MaximumHistoryRows = 500;
    internal const long MaximumHistoryTextBytes = 4 * 1024 * 1024;
    private readonly DispatcherQueue? dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private IReadOnlyList<DshSessionSummary> allSessions = [];
    private CancellationTokenSource? pageCancellation;
    private CancellationTokenSource? historyCancellation;
    private CancellationTokenSource? visibleRefreshCancellation;
    private bool historicalWindow;
    private bool attached;
    private bool rebuildingList;
    private bool connectionOperationInProgress;
    private bool isHistoryReadSilent;
    private bool historyRefreshPending;
    private bool historyForceRefreshPending;
    private bool historyPagingNeedsReset;
    private int viewGeneration;
    private int historyGeneration;
    private string? historySessionId;
    private string? selectedSessionId;
    private string? voiceTargetId;
    private string? deviceSessionId;
    private IReadOnlyList<string> deviceSessionIds = [];
    private string? currentHome;
    private string? currentProfile;
    private HistorySourceStamp[]? loadedHistoryStamp;
    private HistorySourceStamp[]? readingHistoryStamp;
    private long? beforeSequence;
    private string? beforeDeviceCursor;
    private bool hasEarlierHistory;
    private bool failedHistoryWasEarlier;
    private readonly Dictionary<string, string> messageDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> messageStatuses = new(StringComparer.Ordinal);
    private string? draftSessionKey;
    private bool restoringDraft;
    private DshSessionCapabilities sessionCapabilities = new();
    private int sendOperation;
    private int sessionOperation;

    public ObservableCollection<DshSessionListItem> Sessions { get; } = [];
    public ObservableCollection<DshHistoryListItem> History { get; } = [];
    public ObservableCollection<DshHistoryListItem> VisibleHistory { get; } = [];

    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private DshSessionListItem? selectedSession;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isConnectionBusy;
    [ObservableProperty] private bool isHistoryLoading;
    [ObservableProperty] private bool hasHistoryError;
    [ObservableProperty] private bool isCompactLayout;
    [ObservableProperty] private bool isDetailOpen;
    [ObservableProperty] private bool isErrorOpen;
    [ObservableProperty] private string errorMessage = string.Empty;
    [ObservableProperty] private string historyErrorDetail = string.Empty;
    [ObservableProperty] private string historyStatus = string.Empty;
    [ObservableProperty] private string messageDraft = string.Empty;
    [ObservableProperty] private bool isSending;
    [ObservableProperty] private string sendStatus = string.Empty;
    [ObservableProperty] private bool isSessionOperationBusy;
    [ObservableProperty] private string sessionOperationStatus = string.Empty;
    [ObservableProperty] private bool showArchived;

    public bool CanConnect => !connectionOperationInProgress && !IsConnected;
    public bool CanRefresh => IsConnected && !connectionOperationInProgress;
    public bool CanSelectVoiceTarget => IsConnected && SelectedSession is { Session.IsArchived: false } && SelectedSession.Session.Id != voiceTargetId;
    public bool CanClearVoiceTarget => voiceTargetId is not null;
    public bool CanLoadEarlier => SelectedSession is not null && hasEarlierHistory && !IsHistoryLoading && IsConnected;
    public bool CanRetryHistory => SelectedSession is not null && !IsHistoryLoading && IsConnected;
    public bool CanLoadLatest => CanRetryHistory && historicalWindow;
    public Visibility LoadLatestVisibility => historicalWindow ? Visibility.Visible : Visibility.Collapsed;
    public bool CanSendMessage => attached && IsConnected && !IsSending && !IsSessionOperationBusy
        && SelectedSession is { Session.IsArchived: false } && !string.IsNullOrWhiteSpace(MessageDraft)
        && CanSendToSelectedMonitoredTask
        && (SelectedSession.IsDeviceControlGroup || IsSelectedMonitoredTask && sessionCapabilities.CanPromptTasks || sessionCapabilities.CanSendMessages);
    public bool CanCreateSession => attached && IsConnected && !IsSending && !IsSessionOperationBusy && sessionCapabilities.CanCreateSessions;
    public bool CanRenameSession => attached && IsConnected && !IsSending && !IsSessionOperationBusy
        && SelectedSession is { IsDeviceControlGroup: false } && sessionCapabilities.CanRenameSessions;
    public bool CanArchiveSession => attached && IsConnected && !IsSending && !IsSessionOperationBusy
        && SelectedSession is { IsDeviceControlGroup: false, Session.IsArchived: false }
        && SelectedSession.Session.RuntimeStatus != "running" && sessionCapabilities.CanArchiveSessions && sessionCapabilities.CanRestoreSessions;
    public bool CanRestoreSession => attached && IsConnected && !IsSending && !IsSessionOperationBusy
        && SelectedSession is { IsDeviceControlGroup: false, Session.IsArchived: true } && sessionCapabilities.CanRestoreSessions;
    public string SessionCountText => $"{Sessions.Count} 个";
    public string SelectedTitle => SelectedSession?.Title ?? "会话历史";
    public string SelectedDirectory => SelectedSession?.WorkingDirectory ?? string.Empty;
    public string SelectedRuntimeStatus => IsSelectedMonitoredTask ? TaskSnapshot.StatusText : SelectedSession?.RuntimeStatusText ?? string.Empty;
    public string SelectedDetails => SelectedSession is null ? string.Empty : $"{SelectedTitle}\n{SelectedDirectory}";
    public string SelectedStatus => SelectedSession is null ? string.Empty
        : SelectedSession.Session.Id == voiceTargetId ? $"{SelectedRuntimeStatus} · 音箱目标" : SelectedRuntimeStatus;
    public string VoiceTargetActionText => SelectedSession?.Session.Id == voiceTargetId && voiceTargetId is not null
        ? "音箱目标" : "设为音箱目标";
    public string ConnectionStatus => IsConnectionBusy ? "连接中…" : IsConnected ? "已连接" : "未连接";
    public string ConnectionIssueText => IsConnected ? "操作未完成，请重试。" : "连接失败，请检查 DSH 设置后重试。";
    public string EmptyListMessage => !IsConnected
        ? "连接本机 DSH 后显示会话。"
        : string.IsNullOrWhiteSpace(SearchText) ? "暂无会话。" : "没有匹配的会话。";
    public string EmptyHistoryMessage => SelectedSession is null ? "选择右侧会话查看历史。"
        : IsHistoryLoading ? "正在读取历史…" : HasHistoryError ? "历史读取失败。"
        : IsSelectedMonitoredTask && TaskSnapshot.State == "awaitingPrompt" ? "任务会话已新建，请在下方输入任务内容，或通过音箱继续说明。"
        : "暂无对话记录。";
    public Visibility ListPaneVisibility => !IsCompactLayout || !IsDetailOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailsPaneVisibility => !IsCompactLayout || IsDetailOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BackButtonVisibility => IsCompactLayout && IsDetailOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyListVisibility => Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyHistoryVisibility => VisibleHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectedActionsVisibility => SelectedSession is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ClearTargetVisibility => voiceTargetId is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConnectionIssueVisibility => IsErrorOpen && !HasHistoryError ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HistoryLoadingVisibility => IsHistoryLoading && !isHistoryReadSilent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConnectionBusyVisibility => IsConnectionBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConnectActionVisibility => IsConnected ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LoadEarlierVisibility => hasEarlierHistory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RetryHistoryVisibility => HasHistoryError ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ComposerVisibility => SelectedSession is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SendStatusVisibility => string.IsNullOrWhiteSpace(SendStatus) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SessionOperationStatusVisibility => string.IsNullOrWhiteSpace(SessionOperationStatus) ? Visibility.Collapsed : Visibility.Visible;
    public string ComposerPlaceholder => IsSelectedMonitoredTask && TaskSnapshot.NeedsAttention ? "请先在任务详情中处理授权或问题…"
        : IsSelectedMonitoredTask && TaskSnapshot.State == "awaitingPrompt" ? "输入要执行的任务内容…"
        : SelectedSession?.IsDeviceControlGroup == true ? "输入音箱控制口令…" : "发送消息到当前会话…";
    public Visibility ArchiveSessionVisibility => sessionCapabilities.CanArchiveSessions && sessionCapabilities.CanRestoreSessions && SelectedSession is { IsDeviceControlGroup: false, Session.IsArchived: false }
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RestoreSessionVisibility => sessionCapabilities.CanRestoreSessions && SelectedSession is { IsDeviceControlGroup: false, Session.IsArchived: true }
        ? Visibility.Visible : Visibility.Collapsed;
    public string ArchiveSupportText => IsConnected && !(sessionCapabilities.CanArchiveSessions && sessionCapabilities.CanRestoreSessions)
        ? "当前 DSH 未提供可恢复的归档功能。" : string.Empty;
    public Visibility ArchiveSupportVisibility => string.IsNullOrEmpty(ArchiveSupportText) ? Visibility.Collapsed : Visibility.Visible;

    public void Attach()
    {
        if (attached)
            return;
        attached = true;
        viewGeneration++;
        pageCancellation = new CancellationTokenSource();
        IsConnectionBusy = false;
        connectionOperationInProgress = false;
        IsHistoryLoading = false;
        App.DshSessions.Changed += Service_Changed;
        WindowActivityService.VisibilityChanged += WindowVisibility_Changed;
        ApplySnapshot(App.DshSessions.Current);
        AttachTasks();
        AttachVoice();
        TryOpenDeviceSession();
        if (SelectedSession is not null && History.Count == 0 && historyCancellation is null
            && !IsHistoryLoading && !HasHistoryError)
            _ = LoadHistoryAsync(reset: true, earlier: false);
        UpdateVisibleRefresh();
    }

    public void Detach()
    {
        if (!attached)
            return;
        attached = false;
        viewGeneration++;
        App.DshSessions.Changed -= Service_Changed;
        WindowActivityService.VisibilityChanged -= WindowVisibility_Changed;
        StopVisibleRefresh();
        DetachTasks();
        DetachVoice();
        pageCancellation?.Cancel();
        pageCancellation?.Dispose();
        pageCancellation = null;
        // The page itself is navigation-cached; retain drafts/selection, not all
        // previously decoded conversation text while another tool is displayed.
        ClearHistory();
        if (IsSessionOperationBusy)
            SessionOperationStatus = "会话操作已中断，请刷新列表确认结果。";
    }

    private void Service_Changed(object? sender, DshSessionsSnapshot snapshot)
    {
        var generation = viewGeneration;
        void Apply()
        {
            if (attached && generation == viewGeneration && WindowActivityService.IsVisible)
                ApplySnapshot(snapshot);
        }
        if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess)
            Apply();
        else
            dispatcherQueue.TryEnqueue(Apply);
    }

    private void ApplySnapshot(DshSessionsSnapshot snapshot)
    {
        var home = snapshot.Home.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var homeChanged = home.Length > 0 && !string.Equals(home, currentHome, StringComparison.OrdinalIgnoreCase);
        var profile = DisplayFeatureProfile.DshProfileName;
        var profileChanged = currentProfile is not null && currentProfile != profile;
        currentProfile = profile;
        if (homeChanged || profileChanged)
        {
            if (home.Length > 0)
                currentHome = home;
            // A copied DSH home can contain the same ids. Never merge its history
            // with records read from the previous data directory.
            selectedSessionId = null;
            rebuildingList = true;
            try { SelectedSession = null; }
            finally { rebuildingList = false; }
            IsDetailOpen = false;
            ClearHistory();
            IsErrorOpen = false;
            SessionOperationStatus = string.Empty;
            NotifySelectionProperties();
            RestoreSelectedDraft();
            InvalidateTaskActions();
        }
        var wasConnected = IsConnected;
        IsConnected = snapshot.IsConnected;
        sessionCapabilities = snapshot.Capabilities;
        var newTargetId = snapshot.VoiceTarget?.Id;
        var newDeviceId = string.IsNullOrWhiteSpace(snapshot.DeviceSessionId) ? null : snapshot.DeviceSessionId;
        var deviceChanged = deviceSessionId != newDeviceId;
        var newDeviceIds = snapshot.Sessions.Where(session => session.IsDeviceControl || session.Id == newDeviceId)
            .Select(session => session.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var membershipChanged = !deviceSessionIds.SequenceEqual(newDeviceIds);
        var listChanged = homeChanged || profileChanged || deviceChanged || membershipChanged || !allSessions.SequenceEqual(snapshot.Sessions) || voiceTargetId != newTargetId;
        allSessions = snapshot.Sessions;
        voiceTargetId = newTargetId;
        deviceSessionId = newDeviceId;
        deviceSessionIds = newDeviceIds;
        if (listChanged)
            RebuildSessionList();
        if (membershipChanged && SelectedSession?.IsDeviceControlGroup == true)
            ResetDeviceMembershipHistory();
        var openedDeviceSession = (homeChanged || profileChanged || deviceChanged || membershipChanged)
            && TryOpenDeviceSession();
        NotifyConnectionProperties();
        if (wasConnected && !IsConnected)
        {
            CancelHistoryRead();
            IsHistoryLoading = false;
            loadedHistoryStamp = null;
            ShowError(snapshot.Message);
        }
        if (!connectionOperationInProgress && !openedDeviceSession)
            _ = RefreshSelectedHistoryAsync(background: true, force: false);
    }

    partial void OnSearchTextChanged(string value) => RebuildSessionList();

    private bool TryOpenDeviceSession()
    {
        if (ShouldWaitForMonitoredTask()) return false;
        if (SelectedSession is null && Sessions.FirstOrDefault(session => session.IsDeviceControlGroup) is { } device)
        {
            OpenSessionAutomatically(device);
            return true;
        }
        return false;
    }

    private void RebuildSessionList()
    {
        var id = selectedSessionId;
        var query = SearchText.Trim();
        rebuildingList = true;
        try
        {
            var available = new List<DshSessionListItem>();
            var deviceSessions = allSessions.Where(session => deviceSessionIds.Contains(session.Id) && !session.IsArchived).ToArray();
            if (deviceSessions.Length > 0)
            {
                var canonical = deviceSessions.FirstOrDefault(session => session.Id == deviceSessionId)
                    ?? deviceSessions.FirstOrDefault(session => session.DeviceControlKind == "canonical")
                    ?? deviceSessions.OrderByDescending(session => session.UpdatedAt).ThenBy(session => session.Id, StringComparer.Ordinal).First();
                available.Add(new DshSessionListItem(canonical, voiceTargetId, deviceSessionId,
                    deviceSessionIds, deviceSessions.Max(session => session.UpdatedAt)));
            }
            foreach (var session in allSessions.Where(session => !deviceSessionIds.Contains(session.Id) && (ShowArchived || !session.IsArchived)))
                available.Add(new DshSessionListItem(session, voiceTargetId));
            var desired = available.Where(row => (ShowArchived || !row.Session.IsArchived)
                && (query.Length == 0 || (row.IsDeviceControlGroup
                    ? "音箱控制".Contains(query, StringComparison.OrdinalIgnoreCase)
                        || deviceSessions.Any(session => session.WorkingDirectory.Contains(query, StringComparison.OrdinalIgnoreCase))
                    : row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || row.WorkingDirectory.Contains(query, StringComparison.OrdinalIgnoreCase)))).ToArray();
            UpdateSessionRows(desired);
            // Search filters the list, not the open conversation. Keep its latest
            // metadata, history and draft while it still exists in this DSH scope.
            SelectedSession = Sessions.FirstOrDefault(session => session.Id == id)
                ?? available.FirstOrDefault(session => session.Id == id);
        }
        finally
        {
            rebuildingList = false;
        }
        if (SelectedSession is null)
        {
            selectedSessionId = null;
            IsDetailOpen = false;
            ClearHistory();
        }
        NotifySelectionProperties();
        if (draftSessionKey != GetSelectedDraftKey())
            RestoreSelectedDraft();
        OnPropertyChanged(nameof(SessionCountText));
        OnPropertyChanged(nameof(EmptyListVisibility));
        OnPropertyChanged(nameof(EmptyListMessage));
        TryApplyPendingTaskFollow();
    }

    partial void OnSelectedSessionChanged(DshSessionListItem? value)
    {
        if (rebuildingList)
            return;
        RecordManualSessionSelection(value);
        selectedSessionId = value?.Id;
        RestoreSelectedDraft();
        IsDetailOpen = value is not null;
        NotifySelectionProperties();
        if (value is null)
            ClearHistory();
        else if (value.Id != historySessionId)
            _ = LoadHistoryAsync(reset: true, earlier: false);
    }

    public void OpenSession(DshSessionListItem session)
    {
        RecordManualSessionSelection(session);
        SelectedSession = session;
        IsDetailOpen = true;
    }

    partial void OnIsConnectedChanged(bool value) => NotifyConnectionProperties();
    partial void OnIsConnectionBusyChanged(bool value)
    {
        NotifyConnectionProperties();
        OnPropertyChanged(nameof(ConnectionBusyVisibility));
    }
    partial void OnIsHistoryLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(HistoryLoadingVisibility));
        OnPropertyChanged(nameof(EmptyHistoryMessage));
        LoadEarlierCommand.NotifyCanExecuteChanged();
        RetryHistoryCommand.NotifyCanExecuteChanged();
        LoadLatestCommand.NotifyCanExecuteChanged();
    }
    partial void OnHasHistoryErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(RetryHistoryVisibility));
        OnPropertyChanged(nameof(EmptyHistoryMessage));
        OnPropertyChanged(nameof(ConnectionIssueVisibility));
    }
    partial void OnIsErrorOpenChanged(bool value) => OnPropertyChanged(nameof(ConnectionIssueVisibility));
    partial void OnIsCompactLayoutChanged(bool value) => NotifyPaneProperties();
    partial void OnIsDetailOpenChanged(bool value) => NotifyPaneProperties();
    partial void OnMessageDraftChanged(string value)
    {
        if (!restoringDraft && draftSessionKey is not null)
            messageDrafts[draftSessionKey] = value;
        SendMessageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSendMessage));
    }
    partial void OnIsSendingChanged(bool value) => NotifyChatActions();
    partial void OnIsSessionOperationBusyChanged(bool value) => NotifyChatActions();
    partial void OnShowArchivedChanged(bool value) => RebuildSessionList();
    partial void OnSendStatusChanged(string value) => OnPropertyChanged(nameof(SendStatusVisibility));
    partial void OnSessionOperationStatusChanged(string value) => OnPropertyChanged(nameof(SessionOperationStatusVisibility));

    private string? GetSelectedDraftKey()
        => SelectedSession is null ? null : GetCurrentScopeKey() + "\n" + SelectedSession.Id;

    private string GetCurrentScopeKey()
        => (currentHome ?? string.Empty).ToUpperInvariant() + "\n" + (currentProfile ?? string.Empty);

    private void RestoreSelectedDraft()
    {
        draftSessionKey = GetSelectedDraftKey();
        restoringDraft = true;
        try
        {
            MessageDraft = draftSessionKey is not null && messageDrafts.TryGetValue(draftSessionKey, out var draft) ? draft : string.Empty;
            SendStatus = draftSessionKey is not null && messageStatuses.TryGetValue(draftSessionKey, out var status) ? status : string.Empty;
        }
        finally { restoringDraft = false; }
    }

    private void SetMessageStatus(string key, string status)
    {
        messageStatuses[key] = status;
        if (draftSessionKey == key)
            SendStatus = status;
    }

    private void NotifyChatActions()
    {
        NotifyTaskActions();
        SendMessageCommand.NotifyCanExecuteChanged();
        NewSessionCommand.NotifyCanExecuteChanged();
        RenameSessionCommand.NotifyCanExecuteChanged();
        ArchiveSessionCommand.NotifyCanExecuteChanged();
        RestoreSessionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSendMessage));
        OnPropertyChanged(nameof(CanCreateSession));
        OnPropertyChanged(nameof(CanRenameSession));
        OnPropertyChanged(nameof(CanArchiveSession));
        OnPropertyChanged(nameof(CanRestoreSession));
        OnPropertyChanged(nameof(ArchiveSessionVisibility));
        OnPropertyChanged(nameof(RestoreSessionVisibility));
        OnPropertyChanged(nameof(ArchiveSupportText));
        OnPropertyChanged(nameof(ArchiveSupportVisibility));
    }

    private void NotifyConnectionProperties()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        SelectVoiceTargetCommand.NotifyCanExecuteChanged();
        ClearVoiceTargetCommand.NotifyCanExecuteChanged();
        LoadEarlierCommand.NotifyCanExecuteChanged();
        RetryHistoryCommand.NotifyCanExecuteChanged();
        LoadLatestCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(EmptyListMessage));
        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(ConnectActionVisibility));
        OnPropertyChanged(nameof(ConnectionIssueText));
        OnPropertyChanged(nameof(ClearTargetVisibility));
        NotifyChatActions();
    }

    private void NotifySelectionProperties()
    {
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedDirectory));
        OnPropertyChanged(nameof(SelectedRuntimeStatus));
        OnPropertyChanged(nameof(SelectedStatus));
        OnPropertyChanged(nameof(SelectedDetails));
        OnPropertyChanged(nameof(SelectedActionsVisibility));
        OnPropertyChanged(nameof(VoiceTargetActionText));
        OnPropertyChanged(nameof(EmptyHistoryMessage));
        OnPropertyChanged(nameof(ComposerVisibility));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        SelectVoiceTargetCommand.NotifyCanExecuteChanged();
        LoadEarlierCommand.NotifyCanExecuteChanged();
        RetryHistoryCommand.NotifyCanExecuteChanged();
        LoadLatestCommand.NotifyCanExecuteChanged();
        NotifyChatActions();
    }

    private void NotifyPaneProperties()
    {
        OnPropertyChanged(nameof(ListPaneVisibility));
        OnPropertyChanged(nameof(DetailsPaneVisibility));
        OnPropertyChanged(nameof(BackButtonVisibility));
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (await RunConnectionOperationAsync(App.DshSessions.ConnectAsync))
            await RefreshSelectedHistoryAsync(background: false, force: false);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (await RunConnectionOperationAsync(App.DshSessions.RefreshAsync))
            await RefreshSelectedHistoryAsync(background: false, force: true);
    }

    private async Task<bool> RunConnectionOperationAsync(Func<CancellationToken, Task> operation, bool background = false)
    {
        if (!attached || connectionOperationInProgress || pageCancellation is null)
            return false;
        var generation = viewGeneration;
        connectionOperationInProgress = true;
        IsConnectionBusy = !background;
        NotifyConnectionProperties();
        if (!background)
            IsErrorOpen = false;
        try
        {
            await operation(pageCancellation.Token);
            if (attached && generation == viewGeneration)
            {
                ApplySnapshot(App.DshSessions.Current);
                if (!IsConnected)
                    ShowError(App.DshSessions.Current.Message);
                else
                    IsErrorOpen = false;
                return IsConnected;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (attached && generation == viewGeneration)
                ShowError(exception.Message);
        }
        finally
        {
            if (attached && generation == viewGeneration)
            {
                connectionOperationInProgress = false;
                IsConnectionBusy = false;
                NotifyConnectionProperties();
            }
        }
        return false;
    }

    private async Task RefreshWhileVisibleAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
                if (!attached || !WindowActivityService.IsVisible || !IsConnected || connectionOperationInProgress)
                    continue;
                if (await RunConnectionOperationAsync(App.DshSessions.RefreshAsync, background: true))
                    await RefreshSelectedHistoryAsync(background: true, force: false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void StopVisibleRefresh()
    {
        visibleRefreshCancellation?.Cancel();
        visibleRefreshCancellation?.Dispose();
        visibleRefreshCancellation = null;
    }

    private void UpdateVisibleRefresh()
    {
        StopVisibleRefresh();
        if (!attached || pageCancellation is null || !WindowActivityService.IsVisible) return;
        visibleRefreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(pageCancellation.Token);
        _ = RefreshWhileVisibleAsync(visibleRefreshCancellation.Token);
    }

    private void WindowVisibility_Changed(object? sender, bool visible)
    {
        var generation = viewGeneration;
        void Apply()
        {
            if (!attached || generation != viewGeneration || visible != WindowActivityService.IsVisible) return;
            UpdateVisibleRefresh();
            if (!visible)
            {
                CancelHistoryRead();
                IsHistoryLoading = false;
                loadedHistoryStamp = null;
                return;
            }
            ApplySnapshot(App.DshSessions.Current);
            ApplyTaskSnapshot(App.DshTasks.Current);
            ApplyVoiceSnapshot(App.VoiceAgent.Current);
            _ = RefreshAfterShowingAsync();
        }
        if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess) Apply();
        else dispatcherQueue.TryEnqueue(Apply);
    }

    private async Task RefreshAfterShowingAsync()
    {
        if (IsConnected && await RunConnectionOperationAsync(App.DshSessions.RefreshAsync, background: true))
            await RefreshSelectedHistoryAsync(background: true, force: false);
    }

    [RelayCommand(CanExecute = nameof(CanSelectVoiceTarget))]
    private void SelectVoiceTarget()
    {
        if (SelectedSession is not { } selection)
            return;
        try { App.DshSessions.SelectVoiceTarget(selection.Session); }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    [RelayCommand(CanExecute = nameof(CanClearVoiceTarget))]
    private void ClearVoiceTarget()
    {
        try { App.DshSessions.ClearVoiceTarget(); }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    [RelayCommand]
    private void BackToList() => IsDetailOpen = false;

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        if (!CanSendMessage || SelectedSession is not { } selection || pageCancellation is null || draftSessionKey is null)
            return;
        var key = draftSessionKey;
        var draft = MessageDraft;
        var generation = viewGeneration;
        var scope = GetCurrentScopeKey();
        var operation = ++sendOperation;
        var cancellationToken = pageCancellation.Token;
        IsSending = true;
        SetMessageStatus(key, "正在发送…");
        try
        {
            var result = await SendSelectedMessageAsync(selection, draft.Trim(), cancellationToken);
            if (result.Accepted)
            {
                if (messageDrafts.TryGetValue(key, out var latestDraft) && latestDraft == draft)
                {
                    messageDrafts[key] = string.Empty;
                    if (draftSessionKey == key)
                        MessageDraft = string.Empty;
                }
                SetMessageStatus(key, result.Completed
                    ? result.Success ? "已发送" : string.IsNullOrWhiteSpace(result.Message) ? "已接收，执行失败。" : result.Message
                    : "已接收，正在执行…");
            }
            else
                SetMessageStatus(key, result.ErrorCode == "unconfirmed"
                    ? "发送状态尚未确认。请先刷新历史，确认后再决定是否重试。"
                    : string.IsNullOrWhiteSpace(result.Message) ? "消息未接收，草稿已保留。" : result.Message);
            if (attached && generation == viewGeneration && scope == GetCurrentScopeKey())
            {
                ApplySnapshot(App.DshSessions.Current);
                if (SelectedSession?.Id == selection.Id)
                {
                    if (historicalWindow && result.Accepted)
                    {
                        // The send may finish while minimized. Clear the old
                        // window now; showing the page will load the live tail.
                        ClearHistory();
                        await LoadHistoryAsync(reset: true, earlier: false);
                    }
                    else
                        await RefreshSelectedHistoryAsync(background: true, force: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetMessageStatus(key, "发送已中断，状态尚未确认。请先刷新历史，避免重复发送。");
        }
        catch (Exception exception)
        {
            SetMessageStatus(key, $"发送未确认，草稿已保留：{exception.Message}");
        }
        finally
        {
            if (operation == sendOperation)
                IsSending = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateSession))]
    private async Task NewSessionAsync(string? title)
    {
        if (!CanCreateSession || !TryValidateTitle(title, out var normalized) || pageCancellation is null)
            return;
        await RunSessionOperationAsync(async token =>
        {
            var session = await App.DshSessions.CreateSessionAsync(normalized, token);
            return session.Id;
        }, "会话已创建。", selectResult: true);
    }

    [RelayCommand(CanExecute = nameof(CanRenameSession))]
    private async Task RenameSessionAsync(string? title)
    {
        if (!CanRenameSession || SelectedSession is not { } selection || !TryValidateTitle(title, out var normalized))
            return;
        await RunSessionOperationAsync(async token =>
        {
            await App.DshSessions.RenameSessionAsync(selection.Session.Id, normalized, token);
            return selection.Id;
        }, "会话已重命名。", selectResult: true);
    }

    [RelayCommand(CanExecute = nameof(CanArchiveSession))]
    private async Task ArchiveSessionAsync()
    {
        if (!CanArchiveSession || SelectedSession is not { } selection)
            return;
        await RunSessionOperationAsync(async token =>
        {
            await App.DshSessions.SetSessionArchivedAsync(selection.Session.Id, true, token);
            return selection.Id;
        }, "会话已归档，可在已归档列表中恢复。", selectResult: false);
    }

    [RelayCommand(CanExecute = nameof(CanRestoreSession))]
    private async Task RestoreSessionAsync()
    {
        if (!CanRestoreSession || SelectedSession is not { } selection)
            return;
        await RunSessionOperationAsync(async token =>
        {
            await App.DshSessions.SetSessionArchivedAsync(selection.Session.Id, false, token);
            return selection.Id;
        }, "会话已恢复。", selectResult: true);
    }

    private bool TryValidateTitle(string? title, out string normalized)
    {
        normalized = title?.Trim() ?? string.Empty;
        if (normalized.Length is > 0 and <= 200 && !normalized.Any(character => char.IsControl(character)))
            return true;
        SessionOperationStatus = "会话名称需要 1–200 个字符，且不能包含换行或控制字符。";
        return false;
    }

    private async Task RunSessionOperationAsync(Func<CancellationToken, Task<string>> operation, string successStatus, bool selectResult)
    {
        if (!attached || pageCancellation is null || IsSessionOperationBusy)
            return;
        var generation = viewGeneration;
        var scope = GetCurrentScopeKey();
        var operationId = ++sessionOperation;
        var token = pageCancellation.Token;
        IsSessionOperationBusy = true;
        SessionOperationStatus = "正在处理…";
        var confirmed = false;
        try
        {
            var id = await operation(token);
            confirmed = true;
            await App.DshSessions.RefreshAsync(token);
            if (!attached || generation != viewGeneration || scope != GetCurrentScopeKey())
                return;
            ApplySnapshot(App.DshSessions.Current);
            if (!IsConnected)
            {
                SessionOperationStatus = $"{successStatus}列表刷新失败，请重新连接后刷新查看。";
                return;
            }
            if (selectResult)
            {
                SearchText = string.Empty;
                if (Sessions.FirstOrDefault(session => session.Id == id) is { } result)
                    OpenSession(result);
            }
            SessionOperationStatus = successStatus;
        }
        catch (OperationCanceledException)
        {
            if (attached && generation == viewGeneration && scope == GetCurrentScopeKey())
                SessionOperationStatus = confirmed ? $"{successStatus}请刷新列表查看。" : "操作已中断，结果尚未确认，请先刷新列表。";
        }
        catch (Exception exception)
        {
            if (attached && generation == viewGeneration && scope == GetCurrentScopeKey())
                SessionOperationStatus = confirmed ? $"{successStatus}列表刷新失败：{exception.Message}" : $"操作结果未确认，请先刷新列表：{exception.Message}";
        }
        finally
        {
            if (operationId == sessionOperation)
                IsSessionOperationBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadEarlier))]
    private async Task LoadEarlierAsync() => await LoadHistoryAsync(reset: false, earlier: true);

    [RelayCommand(CanExecute = nameof(CanRetryHistory))]
    private async Task RetryHistoryAsync()
        => await LoadHistoryAsync(reset: History.Count == 0, earlier: failedHistoryWasEarlier);

    private async Task RefreshSelectedHistoryAsync(bool background, bool force)
    {
        if (!attached || !IsConnected || SelectedSession is null || !WindowActivityService.IsVisible)
            return;
        // After paging beyond the retained window, don't merge a disjoint tail
        // into the older page the user is reading. Explicit refresh returns live.
        if (historicalWindow)
        {
            if (background) return;
            await LoadHistoryAsync(reset: true, earlier: false);
            return;
        }
        var stamp = GetHistoryStamp(SelectedSession);
        if (IsHistoryLoading)
        {
            // A snapshot arriving during a read must get another read if its source
            // changed. Repeated running snapshots alone must not create a read loop.
            if (force || readingHistoryStamp is not null && !readingHistoryStamp.SequenceEqual(stamp))
                historyRefreshPending = true;
            historyForceRefreshPending |= force;
            return;
        }
        if (!force && loadedHistoryStamp is not null && loadedHistoryStamp.SequenceEqual(stamp)
            && !(HasHistoryError && !failedHistoryWasEarlier)
            && !stamp.Any(source => source.UpdatedAt == DateTimeOffset.MinValue || source.Status is "running" or "unknown"))
            return;
        await LoadHistoryAsync(reset: historySessionId != SelectedSession.Id, earlier: false, background: background);
    }

    private HistorySourceStamp[] GetHistoryStamp(DshSessionListItem selection)
        => allSessions.Where(session => selection.IsDeviceControlGroup
                ? deviceSessionIds.Contains(session.Id) : session.Id == selection.Session.Id)
            .OrderBy(session => session.Id, StringComparer.Ordinal)
            .Select(session => new HistorySourceStamp(session.Id, session.UpdatedAt, session.RuntimeStatus)).ToArray();

    private sealed record HistorySourceStamp(string Id, DateTimeOffset UpdatedAt, string Status);

    private async Task LoadHistoryAsync(bool reset, bool earlier, bool background = false)
    {
        if (!attached || SelectedSession is not { } selection || pageCancellation is null || !IsConnected
            || !WindowActivityService.IsVisible)
            return;
        if (!reset && IsHistoryLoading)
            return;
        if (reset)
        {
            ClearHistory();
            historySessionId = selection.Id;
            historyCancellation = CancellationTokenSource.CreateLinkedTokenSource(pageCancellation.Token);
        }
        historyCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(pageCancellation.Token);
        var generation = historyGeneration;
        var token = historyCancellation.Token;
        var requestedStamp = GetHistoryStamp(selection);
        readingHistoryStamp = requestedStamp;
        isHistoryReadSilent = background && History.Count > 0;
        IsHistoryLoading = true;
        if (!background)
        {
            HasHistoryError = false;
            HistoryStatus = string.Empty;
            HistoryErrorDetail = string.Empty;
        }
        try
        {
            var requestedSourceIds = selection.IsDeviceControlGroup ? deviceSessionIds.ToArray() : [selection.Session.Id];
            IReadOnlyList<DshHistoryEntry> entries;
            long? nextSequence = null;
            string? nextDeviceCursor = null;
            bool hasMore, truncated;
            if (selection.IsDeviceControlGroup)
            {
                var page = await App.DshSessions.ReadDeviceHistoryAsync(requestedSourceIds, earlier ? beforeDeviceCursor : null, token);
                entries = page.Entries;
                nextDeviceCursor = page.BeforeCursor;
                hasMore = page.HasMore;
                truncated = page.Truncated;
            }
            else
            {
                var page = await App.DshSessions.ReadHistoryAsync(selection.Session.Id, earlier ? beforeSequence : null, token);
                entries = page.Entries.Select(entry => string.IsNullOrEmpty(entry.SessionId)
                    ? entry with { SessionId = selection.Session.Id } : entry).ToArray();
                nextSequence = page.BeforeSeq;
                hasMore = page.HasMore;
                truncated = page.Truncated;
            }
            if (!attached || !IsConnected || token.IsCancellationRequested || generation != historyGeneration || SelectedSession?.Id != selection.Id)
                return;
            var wasEmpty = History.Count == 0;
            var merged = History.ToDictionary(row => row.Identity);
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.SessionId))
                    throw new InvalidDataException("聚合历史缺少来源会话，已拒绝混合显示。");
                if (!requestedSourceIds.Contains(entry.SessionId, StringComparer.Ordinal))
                    throw new InvalidDataException("历史记录来自其他会话，已拒绝混合显示。");
                var identity = (entry.SessionId, entry.Sequence);
                if (merged.TryGetValue(identity, out var existing) && existing.Entry == entry)
                    continue;
                var row = new DshHistoryListItem(entry);
                if (existing is not null)
                {
                    row.IsExpanded = existing.IsExpanded;
                    row.IsOriginalExpanded = existing.IsOriginalExpanded;
                }
                merged[identity] = row;
            }
            var ordered = merged.Values.OrderBy(row => row.Entry.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(row => row.Entry.SessionId, StringComparer.Ordinal).ThenBy(row => row.Sequence).ToArray();
            var overBudget = ordered.Length > MaximumHistoryRows
                || ordered.Sum(HistoryTextBytes) > MaximumHistoryTextBytes;
            if (overBudget)
            {
                // Grouped pages may expand a source sequence suffix to handle
                // backdated timestamps. Never split that page from its opaque
                // cursor: retain it whole, even when it alone exceeds the soft
                // budget. Transport/source-page byte limits still bound it;
                // subsequent overflow replaces it instead of accumulating pages.
                var pageIds = entries.Select(entry => (entry.SessionId, entry.Sequence)).ToHashSet();
                ordered = ordered.Where(row => pageIds.Contains(row.Identity)).ToArray();
                if (earlier) SetHistoricalWindow(true);
            }
            UpdateRows(History, ordered);
            if (reset || earlier || wasEmpty || historyPagingNeedsReset || overBudget)
            {
                beforeSequence = nextSequence;
                beforeDeviceCursor = nextDeviceCursor;
                hasEarlierHistory = hasMore;
                historyPagingNeedsReset = false;
            }
            RebuildVisibleHistory();
            if (!earlier)
                loadedHistoryStamp = requestedStamp;
            if (!background || !HasHistoryError || !failedHistoryWasEarlier)
            {
                HasHistoryError = false;
                HistoryErrorDetail = string.Empty;
                HistoryStatus = historicalWindow ? "正在查看更早记录" : truncated ? "部分记录已截断" : string.Empty;
            }
            OnPropertyChanged(nameof(LoadEarlierVisibility));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (attached && generation == historyGeneration)
            {
                HistoryStatus = VisibleHistory.Count > 0 ? "历史读取失败，可重试。" : string.Empty;
                failedHistoryWasEarlier = earlier;
                HasHistoryError = true;
                HistoryErrorDetail = exception.Message;
            }
        }
        finally
        {
            if (attached && generation == historyGeneration)
            {
                readingHistoryStamp = null;
                IsHistoryLoading = false;
                isHistoryReadSilent = false;
                if (historyRefreshPending)
                {
                    var force = historyForceRefreshPending;
                    historyRefreshPending = false;
                    historyForceRefreshPending = false;
                    _ = RefreshSelectedHistoryAsync(background: true, force: force);
                }
            }
        }
    }

    private void UpdateSessionRows(IReadOnlyList<DshSessionListItem> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var existingIndex = -1;
            for (var next = index; next < Sessions.Count; next++)
                if (Sessions[next].Id == desired[index].Id) { existingIndex = next; break; }
            if (existingIndex < 0)
                Sessions.Insert(index, desired[index]);
            else
            {
                if (existingIndex != index) Sessions.Move(existingIndex, index);
                var existing = Sessions[index];
                if (existing.Session != desired[index].Session || existing.WorkingDirectory != desired[index].WorkingDirectory
                    || existing.TargetLabel != desired[index].TargetLabel || existing.MetadataText != desired[index].MetadataText)
                    Sessions[index] = desired[index];
            }
        }
        while (Sessions.Count > desired.Count) Sessions.RemoveAt(Sessions.Count - 1);
    }

    private void ResetDeviceMembershipHistory()
    {
        CancelHistoryRead();
        IsHistoryLoading = false;
        for (var index = History.Count - 1; index >= 0; index--)
            if (!deviceSessionIds.Contains(History[index].Entry.SessionId)) History.RemoveAt(index);
        RebuildVisibleHistory();
        loadedHistoryStamp = null;
        beforeDeviceCursor = null;
        hasEarlierHistory = false;
        historyPagingNeedsReset = true;
        HasHistoryError = false;
        HistoryStatus = string.Empty;
        HistoryErrorDetail = string.Empty;
        OnPropertyChanged(nameof(LoadEarlierVisibility));
        LoadEarlierCommand.NotifyCanExecuteChanged();
    }

    private void RebuildVisibleHistory()
    {
        UpdateRows(VisibleHistory, History.ToArray());
        OnPropertyChanged(nameof(EmptyHistoryVisibility));
        OnPropertyChanged(nameof(EmptyHistoryMessage));
    }

    private static long HistoryTextBytes(DshHistoryListItem row)
        => 2L * (row.Entry.Text.Length + row.Entry.SessionId.Length + row.Entry.Role.Length + row.Entry.Kind.Length);

    private void SetHistoricalWindow(bool value)
    {
        historicalWindow = value;
        OnPropertyChanged(nameof(LoadLatestVisibility));
        LoadLatestCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanLoadLatest))]
    private Task LoadLatestAsync() => LoadHistoryAsync(reset: true, earlier: false);

    private static void UpdateRows(ObservableCollection<DshHistoryListItem> rows, IReadOnlyList<DshHistoryListItem> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < rows.Count && ReferenceEquals(rows[index], desired[index]))
                continue;
            var existingIndex = -1;
            for (var next = index; next < rows.Count; next++)
                if (rows[next].Identity == desired[index].Identity) { existingIndex = next; break; }
            if (existingIndex >= 0)
            {
                if (existingIndex != index) rows.Move(existingIndex, index);
                if (!ReferenceEquals(rows[index], desired[index])) rows[index] = desired[index];
            }
            else
                rows.Insert(index, desired[index]);
        }
        while (rows.Count > desired.Count) rows.RemoveAt(rows.Count - 1);
    }

    private void CancelHistoryRead()
    {
        historyGeneration++;
        historyCancellation?.Cancel();
        historyCancellation?.Dispose();
        historyCancellation = null;
        readingHistoryStamp = null;
        historyRefreshPending = false;
        historyForceRefreshPending = false;
        isHistoryReadSilent = false;
    }

    private void ClearHistory()
    {
        CancelHistoryRead();
        SetHistoricalWindow(false);
        History.Clear();
        VisibleHistory.Clear();
        historySessionId = null;
        loadedHistoryStamp = null;
        historyPagingNeedsReset = false;
        beforeSequence = null;
        beforeDeviceCursor = null;
        hasEarlierHistory = false;
        failedHistoryWasEarlier = false;
        HasHistoryError = false;
        IsHistoryLoading = false;
        HistoryStatus = string.Empty;
        HistoryErrorDetail = string.Empty;
        OnPropertyChanged(nameof(EmptyHistoryVisibility));
        OnPropertyChanged(nameof(LoadEarlierVisibility));
        LoadEarlierCommand.NotifyCanExecuteChanged();
    }

    private void ShowError(string message)
    {
        ErrorMessage = string.IsNullOrWhiteSpace(message) ? "无法连接本机 DSH，请检查 DSH 配置后重试。" : message;
        IsErrorOpen = true;
    }

    internal static string DisplayTitle(DshSessionSummary session)
        => string.IsNullOrWhiteSpace(session.Title) ? "未命名会话" : session.Title;
}

public sealed class DshSessionListItem(DshSessionSummary session, string? voiceTargetId, string? deviceSessionId = null,
    IReadOnlyList<string>? groupedSessionIds = null, DateTimeOffset? latestUpdatedAt = null)
{
    public DshSessionSummary Session { get; } = session;
    public bool IsDeviceControlGroup => groupedSessionIds is not null;
    public string Id => IsDeviceControlGroup ? DshSessionsPageViewModel.DeviceHistoryId : Session.Id;
    public string Title => IsDeviceControlGroup || Session.Id == deviceSessionId ? "音箱控制" : DshSessionsPageViewModel.DisplayTitle(Session);
    public string WorkingDirectory => IsDeviceControlGroup ? $"{groupedSessionIds!.Count} 个设备会话的控制记录"
        : string.IsNullOrWhiteSpace(Session.WorkingDirectory) ? "未指定工作目录" : Session.WorkingDirectory;
    public string RuntimeStatusText => Session.IsArchived ? "已归档" : Session.StatusText;
    public string UpdatedAtText => Session.UpdatedAt == DateTimeOffset.MinValue
        ? "更新时间未知" : $"更新于 {Session.UpdatedAt.ToLocalTime():MM-dd HH:mm}";
    public string TargetLabel => IsDeviceControlGroup || Session.Id == deviceSessionId
        ? Session.Id == voiceTargetId ? "设备 · 目标" : "设备"
        : Session.Id == voiceTargetId ? "音箱目标" : string.Empty;
    public string MetadataText => ((latestUpdatedAt ?? Session.UpdatedAt) == DateTimeOffset.MinValue
        ? "时间未知" : (latestUpdatedAt ?? Session.UpdatedAt).ToLocalTime().ToString("MM-dd HH:mm")) + " · " + (Session.IsArchived ? "已归档" : Session.RuntimeStatus switch
        {
            "running" => "运行中",
            "idle" => "空闲",
            "detached" => "未加载",
            _ => "状态未知"
        });
}

public sealed partial class DshHistoryListItem(DshHistoryEntry entry) : ObservableObject
{
    private const string LegacyVoicePrefix = "你是 Halo PixelBar 的语音助手。请严格执行下面的用户口令；优先调用可用的 PixelBar 工具完成操作；完成后只用一句简短中文说明结果，不使用 Markdown。不要改变、扩展或猜测用户原意。用户口令：";
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool isOriginalExpanded;

    public DshHistoryEntry Entry => entry;
    public long Sequence => entry.Sequence;
    public (string SessionId, long Sequence) Identity => (entry.SessionId, entry.Sequence);
    public bool IsUser => entry.Role.Equals("user", StringComparison.OrdinalIgnoreCase) && IsConversation;
    public bool IsLegacyVoiceWrapper => entry.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
        && entry.Text.StartsWith(LegacyVoicePrefix, StringComparison.Ordinal);
    public bool IsTool => entry.Kind == "tool" || entry.Role.Equals("tool", StringComparison.OrdinalIgnoreCase);
    public bool IsConversation => !IsTool && (IsLegacyVoiceWrapper || (entry.Kind is "" or "conversation"
        && (entry.Role.Equals("user", StringComparison.OrdinalIgnoreCase) || entry.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))));
    public string Text => (IsLegacyVoiceWrapper ? entry.Text[LegacyVoicePrefix.Length..] : entry.Text)
        + (entry.Truncated ? "\n…（记录已截断）" : string.Empty);
    public string OriginalText => entry.Text;
    public bool IsCollapsedRecord => !IsConversation || Text.Length > 350 || Text.Count(character => character == '\n') > 8;
    public Visibility MessageTextVisibility => IsCollapsedRecord ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CollapsedRecordVisibility => IsCollapsedRecord ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OriginalTextVisibility => IsLegacyVoiceWrapper ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConversationMetadataVisibility => IsConversation ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UserBubbleVisibility => IsUser ? Visibility.Visible : Visibility.Collapsed;
    public HorizontalAlignment BubbleAlignment => !IsConversation ? HorizontalAlignment.Stretch
        : IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Thickness BubbleMargin => !IsConversation ? new Thickness(0)
        : IsUser ? new Thickness(36, 0, 0, 0) : new Thickness(0, 0, 36, 0);
    public string CollapsedSummary => (IsTool ? "执行工具记录" : !IsConversation ? "运行上下文" : IsUser ? "长消息" : "长回复")
        + $" · {Text.Length:N0} 字";
    public string CreatedAtText => entry.CreatedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? string.Empty;
    public string RoleText => IsTool ? "工具" : !IsConversation ? "上下文" : entry.Role.ToLowerInvariant() switch
    {
        "user" => "我",
        "assistant" => "助手",
        _ => "记录"
    };
}
