using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;

namespace HaloPixelToolBox.ViewModels;

public partial class DshSessionsPageViewModel
{
    private CancellationTokenSource? taskActionCancellation;
    private int taskActionGeneration;
    private string taskScopeKey = string.Empty;
    private string? observedTaskSessionId;
    private bool observedTaskWasMonitoring;
    private string? followedTaskSessionId;
    private string? pendingTaskFollowId;
    private string? pendingTaskFollowScope;
    private int taskSelectionVersion;
    private int pendingTaskSelectionVersion;
    private bool applyingAutomaticSessionSelection;
    private string taskStatusScopeKey = string.Empty;
    private string taskStatusSessionId = string.Empty;
    private DshTaskInteraction? taskStatusInteraction;
    private bool settingTaskStatusContext;
    [ObservableProperty] private DshTaskSnapshot taskSnapshot = DshTaskSnapshot.Initial;
    [ObservableProperty] private bool isTaskActionBusy;
    [ObservableProperty] private string taskOperationStatus = string.Empty;

    private bool TaskScopeMatches => attached && taskScopeKey == GetCurrentScopeKey()
        && string.Equals(App.DshSessions.Current.Home.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            currentHome ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && DisplayFeatureProfile.DshProfileName == currentProfile;
    private bool TaskConnectionCurrent => IsConnected && App.DshSessions.Current.IsConnected;
    public bool CanStartTask => TaskScopeMatches && TaskConnectionCurrent && !IsTaskActionBusy && !TaskSnapshot.IsBusy
        && !IsSending && !IsSessionOperationBusy && sessionCapabilities.CanCreateTasks
        && sessionCapabilities.CanPromptTasks && sessionCapabilities.CanMonitorTasks
        && (!TaskSnapshot.IsMonitoring || TaskSnapshot.State is "completed" or "failed" or "cancelled" or "idle");
    public bool CanMonitorSelectedTask => TaskScopeMatches && TaskConnectionCurrent && !IsTaskActionBusy && !TaskSnapshot.IsBusy
        && SelectedSession is { IsDeviceControlGroup: false, Session.IsArchived: false }
        && sessionCapabilities.CanMonitorTasks && sessionCapabilities.CanAdoptTasks;
    public bool CanOpenTaskSession => TaskScopeMatches && TaskConnectionCurrent && !string.IsNullOrWhiteSpace(TaskSnapshot.SessionId);
    public bool CanStopTaskMonitoring => TaskScopeMatches && TaskSnapshot.IsMonitoring && !IsTaskActionBusy && !TaskSnapshot.IsBusy;
    public bool CanCancelTask => TaskScopeMatches && TaskConnectionCurrent && TaskSnapshot.IsMonitoring && !IsTaskActionBusy
        && !TaskSnapshot.IsBusy && TaskSnapshot.State != "awaitingPrompt"
        && sessionCapabilities.CanCancelTasks && App.DshSessions.Current.Capabilities.CanCancelTasks;
    public bool CanRespondToTask => TaskScopeMatches && TaskConnectionCurrent && !IsTaskActionBusy
        && TaskSnapshot.CanRespond && sessionCapabilities.CanRespondToTasks && App.DshSessions.Current.Capabilities.CanRespondToTasks;
    private bool IsSelectedMonitoredTask => SelectedSession is { IsDeviceControlGroup: false } selection
        && TaskSnapshot.IsMonitoring && selection.Session.Id == TaskSnapshot.SessionId;
    private bool CanSendToSelectedMonitoredTask => !IsSelectedMonitoredTask
        || TaskScopeMatches && TaskConnectionCurrent && sessionCapabilities.CanPromptTasks
            && !TaskSnapshot.NeedsAttention && !TaskSnapshot.IsBusy && !IsTaskActionBusy
            && App.DshTasks.Current.IsMonitoring && App.DshTasks.Current.SessionId == TaskSnapshot.SessionId
            && !App.DshTasks.Current.NeedsAttention && !App.DshTasks.Current.IsBusy;
    private bool TaskOperationNeedsAttention => TaskOperationStatus.Contains("失败", StringComparison.Ordinal)
        || TaskOperationStatus.Contains("未确认", StringComparison.Ordinal) || TaskOperationStatus.Contains("未完成", StringComparison.Ordinal)
        || TaskOperationStatus.Contains("已中断", StringComparison.Ordinal) || TaskOperationStatus.Contains("已改变", StringComparison.Ordinal);
    public string TaskSummary => TaskOperationNeedsAttention
        || !TaskSnapshot.IsMonitoring && !TaskSnapshot.IsBusy && !string.IsNullOrWhiteSpace(TaskOperationStatus)
        ? TaskOperationStatus : string.IsNullOrWhiteSpace(TaskSnapshot.Title)
        ? TaskSnapshot.StatusText : $"{TaskSnapshot.Title} · {TaskSnapshot.StatusText}";
    public string TaskDetails => $"{TaskSummary}\n{TaskSnapshot.WorkingDirectory}\n{TaskSnapshot.Detail}\n{TaskOperationStatus}".Trim();
    public string TaskDetailsActionText => TaskSnapshot.NeedsAttention ? $"处理 {TaskSnapshot.PendingInteractions.Count} 项" : "详情";
    public Visibility TaskPanelVisibility => TaskSnapshot.IsMonitoring || TaskSnapshot.IsBusy || IsTaskActionBusy
        || !string.IsNullOrWhiteSpace(TaskOperationStatus) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TaskOperationStatusVisibility => string.IsNullOrWhiteSpace(TaskOperationStatus) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CancelTaskVisibility => sessionCapabilities.CanCancelTasks && TaskSnapshot.State != "awaitingPrompt"
        ? Visibility.Visible : Visibility.Collapsed;

    private void AttachTasks()
    {
        App.DshTasks.Changed += Tasks_Changed;
        ApplyTaskSnapshot(App.DshTasks.Current);
    }

    private void DetachTasks()
    {
        App.DshTasks.Changed -= Tasks_Changed;
        InvalidateTaskActions();
    }

    private void InvalidateTaskActions()
    {
        taskActionGeneration++;
        taskActionCancellation?.Cancel();
        taskActionCancellation = null;
        IsTaskActionBusy = false;
        TaskOperationStatus = string.Empty;
        TaskSnapshot = DshTaskSnapshot.Initial;
        observedTaskSessionId = null;
        observedTaskWasMonitoring = false;
        followedTaskSessionId = null;
        taskSelectionVersion++;
        ClearPendingTaskFollow();
        taskScopeKey = GetCurrentScopeKey();
        NotifyTaskActions();
    }

    private void Tasks_Changed(object? sender, DshTaskSnapshot snapshot)
    {
        var generation = viewGeneration;
        var scope = GetCurrentScopeKey();
        void Apply()
        {
            if (attached && generation == viewGeneration && scope == GetCurrentScopeKey()
                && snapshot == App.DshTasks.Current)
                ApplyTaskSnapshot(snapshot);
        }
        if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess) Apply();
        else dispatcherQueue.TryEnqueue(Apply);
    }

    private void ApplyTaskSnapshot(DshTaskSnapshot snapshot)
    {
        ReconcileTaskOperationStatus(snapshot);
        var monitoring = snapshot.IsMonitoring && !string.IsNullOrWhiteSpace(snapshot.SessionId);
        var newTaskSession = monitoring && (!observedTaskWasMonitoring || observedTaskSessionId != snapshot.SessionId);
        observedTaskWasMonitoring = monitoring;
        if (monitoring) observedTaskSessionId = snapshot.SessionId;
        taskScopeKey = GetCurrentScopeKey();
        TaskSnapshot = snapshot;
        NotifyTaskActions();
        if (!monitoring)
        {
            ClearPendingTaskFollow();
            if (snapshot.State != "creating") TryOpenDeviceSession();
            return;
        }
        if (newTaskSession)
        {
            ClearPendingTaskFollow();
            if (SelectedSession is { IsDeviceControlGroup: false } selected && selected.Session.Id == snapshot.SessionId)
                followedTaskSessionId = snapshot.SessionId;
            // A new confirmed task is an explicit routing event. Status polling never
            // reselects it after the user chooses another ordinary conversation.
            if (SelectedSession is null || SelectedSession.IsDeviceControlGroup
                || SelectedSession.Session.Id == followedTaskSessionId)
            {
                pendingTaskFollowId = snapshot.SessionId;
                pendingTaskFollowScope = GetCurrentScopeKey();
                pendingTaskSelectionVersion = taskSelectionVersion;
            }
        }
        TryApplyPendingTaskFollow();
    }

    private void ClearPendingTaskFollow()
    {
        pendingTaskFollowId = null;
        pendingTaskFollowScope = null;
    }

    private bool IsKnownDeviceSession(string id)
        => id == deviceSessionId || deviceSessionIds.Contains(id)
            || allSessions.Any(session => session.Id == id && session.IsDeviceControl);

    private bool ShouldWaitForMonitoredTask()
    {
        if (pendingTaskFollowId is not null) return true;
        var currentTask = App.DshTasks.Current;
        return currentTask.State == "creating" && currentTask.IsBusy
            || currentTask.IsMonitoring && !string.IsNullOrWhiteSpace(currentTask.SessionId)
                && !IsKnownDeviceSession(currentTask.SessionId);
    }

    private bool TryApplyPendingTaskFollow()
    {
        if (pendingTaskFollowId is not { } id) return false;
        if (!TaskScopeMatches || pendingTaskFollowScope != GetCurrentScopeKey()
            || pendingTaskSelectionVersion != taskSelectionVersion || !TaskSnapshot.IsMonitoring
            || TaskSnapshot.SessionId != id || !App.DshTasks.Current.IsMonitoring || App.DshTasks.Current.SessionId != id
            || IsKnownDeviceSession(id))
        {
            ClearPendingTaskFollow();
            return false;
        }
        var actual = allSessions.FirstOrDefault(session => session.Id == id && !session.IsDeviceControl && !session.IsArchived);
        if (actual is null) return false;
        var row = Sessions.FirstOrDefault(item => !item.IsDeviceControlGroup && item.Session.Id == id && !item.Session.IsArchived);
        if (row is null && !string.IsNullOrWhiteSpace(SearchText))
        {
            SearchText = string.Empty;
            return SelectedSession is { IsDeviceControlGroup: false } selection && selection.Session.Id == id;
        }
        if (row is null) return false;
        ClearPendingTaskFollow();
        followedTaskSessionId = id;
        OpenSessionAutomatically(row);
        return true;
    }

    private void OpenSessionAutomatically(DshSessionListItem session)
    {
        var previous = applyingAutomaticSessionSelection;
        applyingAutomaticSessionSelection = true;
        try { OpenSession(session); }
        finally { applyingAutomaticSessionSelection = previous; }
    }

    private void RecordManualSessionSelection(DshSessionListItem? selection)
    {
        if (applyingAutomaticSessionSelection) return;
        taskSelectionVersion++;
        ClearPendingTaskFollow();
        followedTaskSessionId = selection is { IsDeviceControlGroup: false }
            && TaskSnapshot.IsMonitoring && selection.Session.Id == TaskSnapshot.SessionId
            ? selection.Session.Id : null;
    }

    partial void OnTaskSnapshotChanged(DshTaskSnapshot value) => NotifyTaskActions();
    partial void OnIsTaskActionBusyChanged(bool value) => NotifyTaskActions();
    partial void OnTaskOperationStatusChanged(string value)
    {
        if (!settingTaskStatusContext)
        {
            taskStatusScopeKey = GetCurrentScopeKey();
            taskStatusSessionId = TaskSnapshot.SessionId;
            taskStatusInteraction = null;
        }
        OnPropertyChanged(nameof(TaskOperationStatusVisibility));
        OnPropertyChanged(nameof(TaskPanelVisibility));
        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(TaskDetails));
    }

    private void SetTaskOperationStatus(string status, string sessionId, DshTaskInteraction? interaction = null)
    {
        settingTaskStatusContext = true;
        try
        {
            taskStatusScopeKey = GetCurrentScopeKey();
            taskStatusSessionId = sessionId;
            taskStatusInteraction = interaction is null ? null : DshTaskInteractionIdentity.Capture(interaction);
            TaskOperationStatus = status;
        }
        finally { settingTaskStatusContext = false; }
    }

    private void ReconcileTaskOperationStatus(DshTaskSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(TaskOperationStatus)) return;
        // Polling preserves a current task's error or unconfirmed submission.
        // A different task, or a resolved/replaced request, cannot inherit it.
        if (taskStatusScopeKey != GetCurrentScopeKey() || taskStatusSessionId != snapshot.SessionId
            || snapshot.State != "disconnected" && !IsTaskActionBusy && taskStatusInteraction is { } interaction
                && !snapshot.PendingInteractions.Any(item => DshTaskInteractionIdentity.Matches(item, interaction)))
            SetTaskOperationStatus(string.Empty, snapshot.SessionId);
    }

    private void NotifyTaskActions()
    {
        SendMessageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSendMessage));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(SelectedRuntimeStatus));
        OnPropertyChanged(nameof(SelectedStatus));
        OnPropertyChanged(nameof(EmptyHistoryMessage));
        OnPropertyChanged(nameof(CanStartTask));
        OnPropertyChanged(nameof(CanMonitorSelectedTask));
        OnPropertyChanged(nameof(CanOpenTaskSession));
        OnPropertyChanged(nameof(CanStopTaskMonitoring));
        OnPropertyChanged(nameof(CanCancelTask));
        OnPropertyChanged(nameof(CanRespondToTask));
        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(TaskDetails));
        OnPropertyChanged(nameof(TaskDetailsActionText));
        OnPropertyChanged(nameof(TaskPanelVisibility));
        OnPropertyChanged(nameof(CancelTaskVisibility));
        MonitorSelectedTaskCommand.NotifyCanExecuteChanged();
        OpenTaskSessionCommand.NotifyCanExecuteChanged();
        StopTaskMonitoringCommand.NotifyCanExecuteChanged();
        CancelTaskCommand.NotifyCanExecuteChanged();
    }

    private async Task<DshDeviceCommandResult> SendSelectedMessageAsync(DshSessionListItem selection, string message, CancellationToken cancellationToken)
    {
        if (selection.IsDeviceControlGroup)
            return await App.DshSessions.ExecuteDeviceCommandAsync(message, cancellationToken: cancellationToken);
        if (App.DshTasks.Current.IsMonitoring && App.DshTasks.Current.SessionId == selection.Session.Id)
        {
            var submitted = await App.DshTasks.SendMessageAsync(message, cancellationToken);
            return new(submitted.Accepted, submitted.Accepted ? "消息已提交，正在监控任务。" : "消息接收状态未确认，草稿已保留。",
                string.Empty, submitted.SessionId, [], [])
            { Accepted = submitted.Accepted, Completed = false, RequestId = submitted.RequestId,
                ErrorCode = submitted.Accepted ? string.Empty : "unconfirmed" };
        }
        return await App.DshSessions.SendMessageAsync(selection.Session.Id, message, cancellationToken);
    }

    public async Task StartTaskAsync(DshTaskStartRequest request)
    {
        if (!CanStartTask) return;
        await RunTaskActionAsync(async cancellationToken =>
        {
            var started = await App.DshTasks.StartAsync(request, cancellationToken);
            if (string.IsNullOrWhiteSpace(started.SessionId) || !started.IsMonitoring)
                throw new InvalidOperationException("任务未确认启动，请查看会话状态后再决定下一步。");
            var message = started.State == "unknown"
                ? "任务会话已创建，但指令提交结果未确认；请查看会话，本轮不会自动重建。"
                : started.State == "awaitingPrompt" ? "任务会话已新建，可在输入框或通过音箱发送任务内容。"
                : "任务已启动，可通过音箱继续管理。";
            cancellationToken.ThrowIfCancellationRequested();
            if (!App.VoiceAgent.IsRunning)
            {
                try
                {
                    var startedVoice = await App.VoiceAgent.StartFromProfileAsync(cancellationToken);
                    if (!startedVoice)
                        message += " 麦克风启动失败，请在语音设置中查看原因。";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    message += $" 麦克风启动失败：{exception.Message}";
                }
            }
            return message;
        }, "正在创建音箱任务…");
    }

    [RelayCommand(CanExecute = nameof(CanMonitorSelectedTask))]
    private async Task MonitorSelectedTaskAsync()
    {
        if (!CanMonitorSelectedTask || SelectedSession is not { } selection) return;
        await RunTaskActionAsync(async cancellationToken =>
        {
            await App.DshTasks.MonitorAsync(selection.Session, cancellationToken);
            return "已监控当前会话，并设为音箱任务目标。";
        }, "正在连接任务监控…");
    }

    [RelayCommand(CanExecute = nameof(CanOpenTaskSession))]
    private async Task OpenTaskSessionAsync()
    {
        if (!CanOpenTaskSession || pageCancellation is null) return;
        var id = TaskSnapshot.SessionId;
        var generation = viewGeneration;
        var scope = GetCurrentScopeKey();
        try
        {
            var row = Sessions.FirstOrDefault(item => !item.IsDeviceControlGroup && item.Session.Id == id);
            if (row is null)
            {
                await App.DshSessions.RefreshAsync(pageCancellation.Token);
                if (!attached || generation != viewGeneration || scope != GetCurrentScopeKey() || TaskSnapshot.SessionId != id) return;
                ApplySnapshot(App.DshSessions.Current);
                SearchText = string.Empty;
                row = Sessions.FirstOrDefault(item => !item.IsDeviceControlGroup && item.Session.Id == id);
            }
            if (row is not null) OpenSession(row);
            else TaskOperationStatus = "监控会话尚未出现在列表，请刷新后重试。";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (attached && generation == viewGeneration && scope == GetCurrentScopeKey())
                TaskOperationStatus = $"无法打开任务会话：{exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopTaskMonitoring))]
    private async Task StopTaskMonitoringAsync()
    {
        if (!CanStopTaskMonitoring) return;
        await RunTaskActionAsync(async cancellationToken =>
        {
            await App.DshTasks.StopMonitoringAsync(cancellationToken);
            return "已停止监控；DSH 中的任务仍保留。" + App.DshTasks.Current.Detail;
        }, "正在停止监控…");
    }

    [RelayCommand(CanExecute = nameof(CanCancelTask))]
    private async Task CancelTaskAsync()
    {
        if (!CanCancelTask) return;
        await RunTaskActionAsync(async cancellationToken =>
        {
            await App.DshTasks.CancelAsync(cancellationToken);
            return "已请求取消当前轮；会话及排队消息仍保留，请查看任务状态。";
        }, "正在取消当前轮…");
    }

    public bool IsTaskInteractionCurrent(string sessionId, string interactionId, string type)
        => FindTaskInteraction(interactionId, type) is { } expected && IsTaskInteractionCurrent(sessionId, expected);

    public bool IsTaskInteractionCurrent(string sessionId, DshTaskInteraction expected)
        => !IsTaskActionBusy && HasCurrentTaskInteraction(sessionId, expected);

    private bool HasCurrentTaskInteraction(string sessionId, DshTaskInteraction expected)
        => TaskScopeMatches && TaskConnectionCurrent && TaskSnapshot.CanRespond && App.DshTasks.Current.CanRespond
            && sessionCapabilities.CanRespondToTasks && App.DshSessions.Current.Capabilities.CanRespondToTasks
            && App.DshTasks.Current.SessionId == sessionId && TaskSnapshot.SessionId == sessionId
            && App.DshTasks.Current.PendingInteractions.Any(item => DshTaskInteractionIdentity.Matches(item, expected))
            && TaskSnapshot.PendingInteractions.Any(item => DshTaskInteractionIdentity.Matches(item, expected));

    private DshTaskInteraction? FindTaskInteraction(string interactionId, string type)
        => TaskSnapshot.PendingInteractions.FirstOrDefault(item => item.Id == interactionId && item.Type == type);

    public async Task RespondTaskApprovalAsync(string sessionId, string interactionId, bool approved)
    {
        if (FindTaskInteraction(interactionId, "approval") is not { } expected)
        {
            SetTaskOperationStatus("授权请求已改变，请重新打开当前请求。", sessionId,
                new(interactionId, "approval", string.Empty, string.Empty, []));
            return;
        }
        await RespondTaskApprovalAsync(sessionId, expected, approved);
    }

    public async Task RespondTaskApprovalAsync(string sessionId, DshTaskInteraction expected, bool approved)
    {
        if (expected.Type != "approval" || !IsTaskInteractionCurrent(sessionId, expected))
        {
            SetTaskOperationStatus("授权请求已改变，请重新打开当前请求。", sessionId, expected);
            return;
        }
        await RunTaskActionAsync(async cancellationToken =>
        {
            if (!HasCurrentTaskInteraction(sessionId, expected))
                throw new InvalidOperationException("授权请求已改变，请重新打开当前请求。");
            await App.DshTasks.RespondApprovalAsync(expected, approved, cancellationToken);
            return approved ? "已仅批准本次请求。" : "已拒绝本次请求。";
        }, "正在提交本次授权答复…", sessionId, expected);
    }

    public async Task RespondTaskQuestionAsync(string sessionId, string interactionId, IReadOnlyDictionary<string, string> answers)
    {
        if (FindTaskInteraction(interactionId, "question") is not { } expected)
        {
            SetTaskOperationStatus("问题已改变，请重新打开当前问题。", sessionId,
                new(interactionId, "question", string.Empty, string.Empty, []));
            return;
        }
        await RespondTaskQuestionAsync(sessionId, expected, answers);
    }

    public async Task RespondTaskQuestionAsync(string sessionId, DshTaskInteraction expected, IReadOnlyDictionary<string, string> answers)
    {
        if (expected.Type != "question" || !IsTaskInteractionCurrent(sessionId, expected))
        {
            SetTaskOperationStatus("问题已改变，请重新打开当前问题。", sessionId, expected);
            return;
        }
        await RunTaskActionAsync(async cancellationToken =>
        {
            if (!HasCurrentTaskInteraction(sessionId, expected))
                throw new InvalidOperationException("问题已改变，请重新打开当前问题。");
            await App.DshTasks.RespondQuestionAsync(expected, answers, cancellationToken);
            return "已提交本次问题的答案。";
        }, "正在提交答案…", sessionId, expected);
    }

    private async Task RunTaskActionAsync(Func<CancellationToken, Task<string>> action, string status,
        string? expectedSessionId = null, DshTaskInteraction? expectedInteraction = null)
    {
        if (!attached || pageCancellation is null || IsTaskActionBusy || !TaskScopeMatches) return;
        var operation = ++taskActionGeneration;
        var generation = viewGeneration;
        var scope = GetCurrentScopeKey();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(pageCancellation.Token);
        taskActionCancellation = cancellation;
        IsTaskActionBusy = true;
        SetTaskOperationStatus(status, expectedSessionId ?? TaskSnapshot.SessionId, expectedInteraction);
        bool ContextCurrent() => attached && generation == viewGeneration && scope == GetCurrentScopeKey()
            && operation == taskActionGeneration && (expectedSessionId is null
                || TaskSnapshot.SessionId == expectedSessionId && App.DshTasks.Current.SessionId == expectedSessionId);
        try
        {
            var resultMessage = await action(cancellation.Token);
            if (ContextCurrent())
            {
                ApplyTaskSnapshot(App.DshTasks.Current);
                SetTaskOperationStatus(resultMessage, TaskSnapshot.SessionId);
            }
        }
        catch (OperationCanceledException)
        {
            if (ContextCurrent())
                SetTaskOperationStatus("操作已中断，请查看任务会话确认结果；不会自动重发。",
                    expectedSessionId ?? TaskSnapshot.SessionId, expectedInteraction);
        }
        catch (Exception exception)
        {
            if (ContextCurrent())
                SetTaskOperationStatus($"任务操作未完成：{exception.Message}",
                    expectedSessionId ?? TaskSnapshot.SessionId, expectedInteraction);
        }
        finally
        {
            if (operation == taskActionGeneration)
            {
                taskActionCancellation = null;
                IsTaskActionBusy = false;
                ReconcileTaskOperationStatus(App.DshTasks.Current);
                NotifyTaskActions();
            }
        }
    }
}
