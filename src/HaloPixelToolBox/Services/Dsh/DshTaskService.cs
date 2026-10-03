using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HaloPixelToolBox.Services;

/// <summary>One monitored ordinary DSH task, with exact request-bound human responses.</summary>
public sealed partial class DshTaskService : IDisposable
{
    private readonly IDshTaskSessionClient client;
    private readonly Func<string> defaultRoot;
    private readonly Func<string> scopeFactory;
    private readonly Func<DshTaskSnapshot, string, CancellationToken, Task>? feedback;
    private readonly string stateRoot;
    private readonly TimeSpan pollInterval;
    private readonly TimeProvider timeProvider;
    private readonly Func<long>? displayRevisionProvider;
    private readonly AsyncLocal<long?> voiceDisplayRevision = new();
    private readonly SemaphoreSlim actions = new(1, 1);
    private readonly object stateGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? monitoring;
    private DshTaskSnapshot current = DshTaskSnapshot.Initial;
    private string activeScope = string.Empty;
    private string revision = string.Empty;
    private string lastCueIdentity = string.Empty;
    private string questionInteractionId = string.Empty;
    private DshTaskInteraction? questionInteractionSchema;
    private readonly Dictionary<string, string> voiceAnswers = [];
    private (string Directory, string Title, string Scope)? voiceDraft;
    private bool awaitingVoicePrompt;
    private string restoreAttemptedScope = string.Empty;
    private long generation;
    private TaskCompletionSource monitorWake = NewMonitorWake();
    private DshSessionSummary? observedSession;
    private bool observedConnected;
    private bool disposed;

    public DshTaskService(IDshTaskSessionClient client, Func<string>? defaultRoot = null,
        Func<string>? scopeKey = null, Func<DshTaskSnapshot, string, CancellationToken, Task>? feedback = null,
        string? stateRoot = null, TimeSpan? pollInterval = null, TimeProvider? timeProvider = null,
        Func<long>? displayRevisionProvider = null)
    {
        this.client = client;
        this.defaultRoot = defaultRoot ?? (() => DisplayFeatureProfile.DshTaskRootDirectory);
        scopeFactory = scopeKey ?? (() => Path.GetFullPath(DisplayFeatureProfile.DshHomePath).TrimEnd('\\').ToUpperInvariant()
            + "\n" + DisplayFeatureProfile.DshProfileName);
        this.feedback = feedback;
        this.stateRoot = stateRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaloPixelToolBox", "DshTasks");
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.displayRevisionProvider = displayRevisionProvider;
        client.Changed += ClientChanged;
    }

    public event EventHandler<DshTaskSnapshot>? Changed;
    public DshTaskSnapshot Current { get { lock (stateGate) return current; } }

    public async Task<DshTaskSnapshot> StartAsync(DshTaskStartRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var displayRequestRevision = CaptureDisplayRevision();
        await actions.WaitAsync(cancellationToken);
        try
        {
            if (Current.IsMonitoring && Current.State is not ("completed" or "failed" or "cancelled" or "idle"))
                throw new InvalidOperationException("已有任务正在监控，请先停止监控或结束当前任务。");
            var stopFailure = await StopCoreAsync(release: true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (stopFailure is not null) throw new InvalidOperationException(stopFailure);
            activeScope = scopeFactory();
            Publish(DshTaskSnapshot.Initial with { State = "creating", StatusText = "正在创建任务", IsBusy = true });
            if (!client.Current.IsConnected) await client.ConnectAsync(cancellationToken);
            EnsureScope();
            var session = await client.CreateTaskSessionAsync(request, cancellationToken);
            EnsureScope();
            BeginMonitoring(session, awaitingPrompt: string.IsNullOrWhiteSpace(request.Prompt));
            await FeedbackAsync("task_display_requested", monitoring!.Token, displayRequestRevision);
            if (awaitingVoicePrompt)
                await FeedbackAsync(string.Empty, monitoring!.Token);
            else try
            {
                var submission = await client.SubmitTaskPromptAsync(session.Id, request.Prompt, cancellationToken);
                EnsureScope();
                if (!submission.Accepted || submission.SessionId != session.Id)
                    throw new InvalidDataException("未确认任务消息已接受，请查看会话；不会自动重发。");
                Publish(Current with { State = "running", StatusText = "任务已提交", Detail = "正在执行，状态会显示到字幕屏。", IsBusy = false });
                await FeedbackAsync("task_started", monitoring!.Token);
            }
            catch (Exception exception)
            {
                if (activeScope != scopeFactory() || Current.SessionId != session.Id || !Current.IsMonitoring)
                    throw;
                Publish(Current with { State = "unknown", StatusText = "任务提交结果未确认", Detail = exception.Message + "；请核对会话，未自动重发。", IsBusy = false });
                await FeedbackAsync("task_failed", shutdown.Token);
            }
            SaveIdentity();
            _ = MonitorLoopAsync(session.Id, generation, monitoring!.Token);
            return Current;
        }
        catch
        {
            if (Current.SessionId.Length == 0 && activeScope == scopeFactory())
                Publish(Current with { State = "failed", StatusText = "任务创建未确认", Detail = "请核对目录和会话，未自动重建。", IsBusy = false });
            throw;
        }
        finally { actions.Release(); }
    }

    public Task MonitorAsync(DshSessionSummary session, CancellationToken cancellationToken = default)
        => MonitorCoreAsync(session, restoreAwaitingPrompt: false, cancellationToken);

    private async Task MonitorCoreAsync(DshSessionSummary session, bool restoreAwaitingPrompt, CancellationToken cancellationToken,
        string? expectedRestoreScope = null, long expectedRestoreGeneration = -1, bool requestDisplay = true)
    {
        var displayRequestRevision = requestDisplay && expectedRestoreScope is null ? CaptureDisplayRevision() : null;
        await actions.WaitAsync(cancellationToken);
        try
        {
            // An automatic restore may have waited behind a user action. It must never
            // replace a task created or selected while the saved identity was being read.
            if (expectedRestoreScope is not null && (expectedRestoreScope != scopeFactory()
                || expectedRestoreGeneration != generation || Current.IsMonitoring || Current.IsBusy))
                return;
            if (session.IsDeviceControl || session.IsArchived) throw new ArgumentException("请选择未归档的普通任务会话。");
            var stopFailure = await StopCoreAsync(release: true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (stopFailure is not null) throw new InvalidOperationException(stopFailure);
            if (expectedRestoreScope is not null && expectedRestoreScope != scopeFactory()) return;
            activeScope = scopeFactory();
            await client.AdoptTaskSessionAsync(session.Id, cancellationToken);
            EnsureScope();
            // The first utterance may be an answer to an existing question.
            // Fetch its actual state before making this task interactive.
            var remote = await client.ReadTaskStateAsync(session.Id, cancellationToken);
            EnsureScope();
            cancellationToken.ThrowIfCancellationRequested();
            var awaitingPrompt = restoreAwaitingPrompt && !remote.HasSubmittedPrompt
                && remote.TaskStatus == "idle" && remote.PendingInteractions.Count == 0;
            BeginMonitoring(session, awaitingPrompt);
            if (requestDisplay && expectedRestoreScope is null)
                await FeedbackAsync("task_display_requested", monitoring!.Token, displayRequestRevision);
            await ApplyRemoteStateAsync(remote, monitoring!.Token, force: true);
            SaveIdentity();
            _ = MonitorLoopAsync(session.Id, generation, monitoring!.Token);
        }
        finally { actions.Release(); }
    }

    private void BeginMonitoring(DshSessionSummary session, bool awaitingPrompt = false)
    {
        monitoring?.Cancel();
        monitoring = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        generation++;
        lock (stateGate)
        {
            monitorWake = NewMonitorWake();
            observedSession = client.Current.Sessions.FirstOrDefault(item => item.Id == session.Id) ?? session;
            observedConnected = client.Current.IsConnected;
        }
        revision = string.Empty;
        lastCueIdentity = string.Empty;
        voiceAnswers.Clear();
        questionInteractionId = string.Empty;
        questionInteractionSchema = null;
        awaitingVoicePrompt = awaitingPrompt;
        client.SelectVoiceTarget(session);
        Publish(new(session.Id, session.Title, session.WorkingDirectory,
            awaitingPrompt ? "awaitingPrompt" : "idle",
            awaitingPrompt ? "任务已新建，等待内容" : "已开始监控任务",
            awaitingPrompt ? "请继续说要执行的指令，或在会话输入框发送；将使用当前会话。" : "",
            "", [], true, false));
    }

    public async Task<DshTaskSubmission> SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var displayRequestRevision = CaptureDisplayRevision();
        await actions.WaitAsync(cancellationToken);
        var firstMessage = false;
        var id = string.Empty;
        try
        {
            EnsureActive();
            if (Current.NeedsAttention) throw new InvalidOperationException("当前有等待回答的请求，请先回答或明确拒绝。");
            id = Current.SessionId;
            firstMessage = awaitingVoicePrompt;
            await FeedbackAsync("task_display_requested", monitoring!.Token, displayRequestRevision);
            var result = await client.SubmitTaskPromptAsync(id, message, cancellationToken);
            EnsureActive(id);
            if (!result.Accepted || result.SessionId != id)
                throw new InvalidDataException("未确认任务消息已接受，请查看会话；不会自动重发。");
            awaitingVoicePrompt = false;
            revision = string.Empty;
            Publish(Current with { State = "running", StatusText = "消息已提交", Detail = "继续监控当前任务。", FinalText = string.Empty });
            RequestMonitorRefresh();
            SaveIdentity();
            if (firstMessage) await FeedbackAsync("task_started", monitoring!.Token);
            return result;
        }
        catch (Exception exception)
        {
            if (id.Length > 0 && activeScope == scopeFactory() && Current.SessionId == id && Current.IsMonitoring)
            {
                Publish(Current with { State = "unknown", StatusText = firstMessage ? "任务内容提交结果未确认" : "消息提交结果未确认",
                    Detail = exception.Message + "；请核对当前会话，未自动重发或重建。", IsBusy = false });
                RequestMonitorRefresh();
                SaveIdentity();
                await FeedbackAsync(firstMessage ? "task_failed" : string.Empty, shutdown.Token);
            }
            throw;
        }
        finally { actions.Release(); }
    }

    public Task RespondApprovalAsync(string interactionId, bool approve, CancellationToken cancellationToken = default)
    {
        var snapshot = Current;
        return RespondAsync(snapshot.PendingInteractions.SingleOrDefault(item => item.Id == interactionId && item.Type == "approval"),
            "approval", approve ? "allowed-once" : "rejected", null, snapshot.SessionId, scopeFactory(), cancellationToken);
    }

    public Task RespondApprovalAsync(DshTaskInteraction? expected, bool approve, CancellationToken cancellationToken = default)
        => RespondAsync(expected, "approval", approve ? "allowed-once" : "rejected", null,
            Current.SessionId, scopeFactory(), cancellationToken);

    public Task RespondQuestionAsync(string interactionId, IReadOnlyDictionary<string, string> answers,
        CancellationToken cancellationToken = default)
    {
        var snapshot = Current;
        return RespondAsync(snapshot.PendingInteractions.SingleOrDefault(item => item.Id == interactionId && item.Type == "question"),
            "question", null, new Dictionary<string, string>(answers), snapshot.SessionId, scopeFactory(), cancellationToken);
    }

    public Task RespondQuestionAsync(DshTaskInteraction? expected, IReadOnlyDictionary<string, string> answers,
        CancellationToken cancellationToken = default)
        => RespondAsync(expected, "question", null, new Dictionary<string, string>(answers),
            Current.SessionId, scopeFactory(), cancellationToken);

    private async Task RespondAsync(DshTaskInteraction? expected, string type, string? outcome,
        IReadOnlyDictionary<string, string>? answers, string expectedSessionId, string expectedScope,
        CancellationToken cancellationToken)
    {
        var displayRequestRevision = CaptureDisplayRevision();
        expected = expected is null ? null : CaptureInteraction(expected);
        await actions.WaitAsync(cancellationToken);
        var actionScope = string.Empty;
        var actionSessionId = string.Empty;
        try
        {
            actionScope = scopeFactory();
            actionSessionId = Current.SessionId;
            EnsureActive();
            if (expectedScope != actionScope || expectedSessionId != actionSessionId)
                throw new InvalidOperationException("当前任务已改变，未发送原请求的回答。");
            var pending = Current.PendingInteractions.SingleOrDefault(item => item.Id == expected?.Id && item.Type == type)
                ?? throw new InvalidOperationException("该请求已不在等待中，未发送回答。");
            if (!DshTaskInteractionIdentity.Matches(expected, pending))
                throw new InvalidOperationException("请求内容已改变，请重新查看后回答，未发送旧回答。");
            if (type == "question" && (answers is null || answers.Count != pending.Questions.Count
                || pending.Questions.Any(q => !answers.ContainsKey(q.Id))))
                throw new ArgumentException("请逐项回答本次请求中的所有问题。");
            var id = Current.SessionId;
            await FeedbackAsync("task_display_requested", monitoring!.Token, displayRequestRevision);
            Publish(Current with { IsBusy = true });
            await client.RespondTaskInteractionAsync(id, pending.Id, type, outcome, answers, cancellationToken);
            EnsureActive(id);
            voiceAnswers.Clear();
            questionInteractionId = string.Empty;
            questionInteractionSchema = null;
            revision = string.Empty;
            Publish(WithVoiceDraft(Current with { IsBusy = false, State = "running", StatusText = "回答已提交",
                PendingInteractions = Current.PendingInteractions.Where(p => p.Id != pending.Id).ToArray() }));
            RequestMonitorRefresh();
        }
        catch
        {
            if (activeScope == actionScope && actionScope == scopeFactory() && Current.SessionId == actionSessionId)
            {
                Publish(Current with { IsBusy = false });
                RequestMonitorRefresh();
            }
            throw;
        }
        finally { actions.Release(); }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        await actions.WaitAsync(cancellationToken);
        try
        {
            EnsureActive();
            var id = Current.SessionId;
            await client.CancelTaskSessionAsync(id, cancellationToken);
            EnsureActive(id);
            awaitingVoicePrompt = false;
            voiceDraft = null;
            revision = string.Empty;
            Publish(Current with { StatusText = "已请求取消当前轮", Detail = "DSH 会保留队列中尚未执行的消息；正在核对运行状态。" });
            RequestMonitorRefresh();
        }
        finally { actions.Release(); }
    }

    public async Task StopMonitoringAsync(CancellationToken cancellationToken = default)
    {
        await actions.WaitAsync(cancellationToken);
        try
        {
            var failure = await StopCoreAsync(release: true, cancellationToken);
            Publish(WithVoiceDraft(Current with { IsMonitoring = false, IsBusy = false, PendingInteractions = [], StatusText = "已停止监控",
                Detail = failure is null ? "真实任务未被取消。" : failure + " 真实任务未被取消。" }));
        }
        finally { actions.Release(); }
    }

    private async Task<string?> StopCoreAsync(bool release, CancellationToken token)
    {
        var previous = Current;
        var previousScope = activeScope;
        monitoring?.Cancel();
        generation++;
        voiceAnswers.Clear();
        questionInteractionId = string.Empty;
        questionInteractionSchema = null;
        voiceDraft = null;
        awaitingVoicePrompt = false;
        if (previous.IsMonitoring)
            Publish(WithVoiceDraft(previous with { IsMonitoring = false, IsBusy = false, PendingInteractions = [],
                StatusText = "已停止监控", Detail = "真实任务未被取消。" }));
        if (client.Current.VoiceTarget?.Id == previous.SessionId) client.ClearVoiceTarget();
        var failures = new List<string>();
        if (previous.IsMonitoring && previousScope.Length > 0)
        {
            try
            {
                var file = IdentityPath(previousScope);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception exception) { failures.Add("监控恢复记录清理失败：" + exception.Message); }
        }
        if (release && previous.IsMonitoring)
        {
            if (previousScope == scopeFactory() && client.Current.IsConnected)
            {
                try { await client.ReleaseTaskSessionAsync(previous.SessionId, token); }
                catch (Exception exception) { failures.Add("远端交互释放未确认：" + exception.Message + "；请在原会话核对。"); }
            }
            else failures.Add("远端交互释放未确认，请在原会话核对。");
        }
        var failure = failures.Count == 0 ? null : string.Join(" ", failures);
        if (failure is not null)
            Publish(Current with { StatusText = "已停止监控", Detail = failure + " 真实任务未被取消。" });
        return failure;
    }

    private async Task MonitorLoopAsync(string sessionId, long epoch, CancellationToken token)
    {
        var consecutiveFailures = 0;
        var unchangedIdlePolls = 0;
        while (!token.IsCancellationRequested && epoch == generation)
        {
            Task wake;
            lock (stateGate)
            {
                // Capture before reading so an action between the read and wait cannot
                // lose its wake-up. A signal already consumed means this read is the refresh.
                if (epoch != generation) return;
                if (monitorWake.Task.IsCompleted) monitorWake = NewMonitorWake();
                wake = monitorWake.Task;
            }
            try
            {
                await actions.WaitAsync(token);
                try
                {
                    if (!client.Current.IsConnected || Current.State == "disconnected")
                    {
                        if (activeScope != scopeFactory())
                        {
                            StopForScopeChange();
                            return;
                        }
                        if (!Current.IsMonitoring || Current.SessionId != sessionId
                            || token.IsCancellationRequested || epoch != generation) return;
                        // Rediscover a running host only. Re-adopt after a host replacement,
                        // without restarting the host, creating a session or replaying work.
                        await client.RefreshAsync(token);
                        if (token.IsCancellationRequested || epoch != generation) return;
                        EnsureActive(sessionId);
                        await client.AdoptTaskSessionAsync(sessionId, token);
                        if (token.IsCancellationRequested || epoch != generation) return;
                    }
                    EnsureActive(sessionId);
                    var remote = await client.ReadTaskStateAsync(sessionId, token);
                    if (token.IsCancellationRequested || epoch != generation) return;
                    EnsureActive(sessionId);
                    consecutiveFailures = 0;
                    var previousRevision = revision;
                    var previousState = Current.State;
                    await ApplyRemoteStateAsync(remote, token);
                    var snapshot = Current;
                    var unchangedIdle = !snapshot.NeedsAttention && !snapshot.IsBusy
                        && snapshot.State is ("idle" or "awaitingPrompt" or "completed" or "failed" or "cancelled")
                        && snapshot.State == previousState && remote.Revision == previousRevision;
                    unchangedIdlePolls = unchangedIdle ? Math.Min(unchangedIdlePolls + 1, 4) : 0;
                }
                finally { actions.Release(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || epoch != generation) { return; }
            catch (Exception exception)
            {
                if (token.IsCancellationRequested || epoch != generation) return;
                if (activeScope != scopeFactory())
                {
                    StopForScopeChange();
                    return;
                }
                consecutiveFailures = Math.Min(consecutiveFailures + 1, 16);
                unchangedIdlePolls = 0;
                if (Current.State != "disconnected" || Current.Detail != exception.Message)
                {
                    Publish(Current with { State = "disconnected", StatusText = "任务状态暂不可用", Detail = exception.Message,
                        PendingInteractions = [], IsBusy = false });
                    await FeedbackAsync(string.Empty, token);
                }
            }
            var delay = consecutiveFailures == 0
                ? TimeSpan.FromMilliseconds(Math.Max(pollInterval.TotalMilliseconds,
                    Math.Min(15000, pollInterval.TotalMilliseconds * Math.Pow(2, unchangedIdlePolls))))
                : TimeSpan.FromMilliseconds(Math.Min(30000, pollInterval.TotalMilliseconds * Math.Pow(2, consecutiveFailures)));
            try
            {
                await wake.WaitAsync(delay, timeProvider, token);
                unchangedIdlePolls = 0;
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { return; }
        }
    }

    private static TaskCompletionSource NewMonitorWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RequestMonitorRefresh()
    {
        lock (stateGate) monitorWake.TrySetResult();
    }

    private async Task ApplyRemoteStateAsync(DshTaskRemoteState remote, CancellationToken token, bool force = false)
    {
        if (!force && remote.Revision == revision && Current.State is not ("unknown" or "disconnected")) return;
        var previousRevision = revision;
        revision = remote.Revision;
        if (remote.HasSubmittedPrompt || remote.PendingInteractions.Count > 0) awaitingVoicePrompt = false;
        var state = remote.PendingInteractions.Any(p => p.Type == "approval") ? "waitingApproval"
            : remote.PendingInteractions.Any(p => p.Type == "question") ? "waitingInput"
            : awaitingVoicePrompt && remote.TaskStatus == "idle" ? "awaitingPrompt" : remote.TaskStatus;
        if (state is not ("idle" or "awaitingPrompt" or "running" or "waitingApproval" or "waitingInput" or "completed" or "failed" or "cancelled"))
            state = "unknown";
        var pending = remote.PendingInteractions.FirstOrDefault(p => p.Type == (state == "waitingApproval" ? "approval" : "question"));
        var nextQuestion = pending?.Type == "question" && questionInteractionId == pending.Id
            && DshTaskInteractionIdentity.Matches(questionInteractionSchema, pending)
            ? NextVoiceQuestion(pending)
            : pending?.Questions.FirstOrDefault();
        var detail = pending?.Type == "approval" ? ApprovalPrompt(remote.PendingInteractions)
            : pending?.Type == "question" ? QuestionPrompt(pending, nextQuestion)
                : (state == "awaitingPrompt" ? "请继续说要执行的指令；将使用当前会话。" : remote.ProgressText);
        var previousState = Current.State;
        var previousDetail = Current.Detail;
        Publish(WithVoiceDraft(Current with { State = state, StatusText = StatusLabel(state), Detail = detail,
            FinalText = remote.FinalText, PendingInteractions = remote.PendingInteractions }));
        var cue = state switch
        {
            "waitingApproval" => "approval_required", "waitingInput" => "input_required",
            "completed" => "task_completed", "failed" => "task_failed", "cancelled" => "task_cancelled",
            "awaitingPrompt" => string.Empty,
            _ => "task_progress"
        };
        var cueIdentity = state + ":" + (pending is null ? "" : JsonSerializer.Serialize(pending));
        var sameStateRecovered = previousState is "unknown" or "disconnected"
            && remote.Revision == previousRevision && lastCueIdentity == cueIdentity;
        var shouldCue = pending is not null ? lastCueIdentity != cueIdentity
            : state != previousState && !sameStateRecovered;
        lastCueIdentity = cueIdentity;
        if (shouldCue)
        {
            await FeedbackAsync(cue, token);
        }
        else if (detail != previousDetail || previousState is "unknown" or "disconnected")
            await FeedbackAsync(string.Empty, token);
        SaveIdentity();
    }

    public Task<DshTaskVoiceResult> RouteVoiceAsync(string text, CancellationToken cancellationToken = default)
        => RouteVoiceCheckedAsync(text, null, cancellationToken);

    public Task<DshTaskVoiceResult> RouteVoiceAsync(string text, DshTaskSnapshot expectedContext, CancellationToken cancellationToken = default)
        => RouteVoiceCheckedAsync(text, expectedContext, cancellationToken);

    private async Task<DshTaskVoiceResult> RouteVoiceCheckedAsync(string text, DshTaskSnapshot? expectedContext, CancellationToken cancellationToken)
    {
        var previousDisplayRevision = voiceDisplayRevision.Value;
        voiceDisplayRevision.Value = CaptureDisplayRevision();
        try
        {
            await voiceRouteGate.WaitAsync(cancellationToken);
            try
            {
                if (expectedContext is not null && !VoiceContextMatches(expectedContext, Current))
                    return new(true, "任务或问题已变化，刚才的指令未提交。" + Current.Detail) { ListenForReply = Current.NeedsAttention };
                return await RouteVoiceCoreAsync(text, expectedContext, cancellationToken);
            }
            finally { voiceRouteGate.Release(); }
        }
        finally { voiceDisplayRevision.Value = previousDisplayRevision; }
    }

    private async Task<DshTaskVoiceResult> RouteVoiceCoreAsync(string text, DshTaskSnapshot? expectedContext, CancellationToken cancellationToken)
    {
        var normalized = DshSpokenInteraction.NormalizeCommand(text);
        var intent = DshVoiceIntentRouter.Classify(text, Current.IsMonitoring && Current.NeedsAttention,
            awaitingVoicePrompt || voiceDraft is not null);
        // An explicit answer is data, even when its body contains a task/device command.
        if (Current.IsMonitoring && Current.NeedsAttention && DshVoiceIntentRouter.IsExplicitAnswer(text))
            return await RouteInteractionVoiceAsync(text, expectedContext, cancellationToken);
        if (awaitingVoicePrompt && activeScope != scopeFactory())
        {
            var previousSessionId = Current.SessionId;
            monitoring?.Cancel(); generation++;
            awaitingVoicePrompt = false; voiceDraft = null;
            Publish(DshTaskSnapshot.Initial with { State = "disconnected", StatusText = "配置已改变，监控已停止" });
            if (client.Current.VoiceTarget?.Id == previousSessionId) client.ClearVoiceTarget();
            return new(true, "DSH 配置已改变，请重新创建或选择任务。");
        }
        if (DshTaskVoiceParser.IsNegatedCreation(text) || normalized is "取消创建" or "取消新建任务")
        {
            voiceDraft = null;
            if (awaitingVoicePrompt)
            {
                await StopMonitoringAsync(cancellationToken);
                return new(true, "已取消等待任务内容，空会话已保留并停止监控。");
            }
            return new(true, "尚未创建任务，已取消创建草稿。");
        }
        if (normalized is "任务状态" or "查询任务状态" or "当前任务状态" or "查询当前任务状态")
            return new(true, Current.StatusText + (Current.Detail.Length > 0 ? "：" + Current.Detail : "")) { ListenForReply = Current.NeedsAttention || awaitingVoicePrompt };
        if (normalized is "朗读任务结果" or "读出任务结果")
            return new(true, Current.FinalText.Length > 0 ? Current.FinalText : "当前还没有任务结果。" + Current.StatusText);
        var selectionResult = await RouteTaskSelectionAsync(text, cancellationToken);
        if (selectionResult is not null) return selectionResult;
        if (normalized is "停止监控" or "停止任务监控")
        { await StopMonitoringAsync(cancellationToken); return new(true, Current.StatusText + "。" + Current.Detail); }
        if (normalized is "取消任务" or "取消当前任务" or "停止当前任务" or "取消当前轮")
        {
            if (awaitingVoicePrompt)
            {
                await StopMonitoringAsync(cancellationToken);
                return new(true, "当前尚未提交任务内容，已停止监控并保留空会话。");
            }
            await CancelAsync(cancellationToken);
            return new(true, "已请求取消当前轮；DSH 会保留尚未执行的队列消息。");
        }
        var isCreate = DshTaskVoiceParser.IsCreate(text);
        var creationCandidate = DshTaskVoiceParser.IsCreationCandidate(text);
        if (DshTaskVoiceParser.RequestsMultipleTasks(text))
            return new(true, "目前一次创建一个任务。请说明这个任务要执行的内容。");
        if (isCreate || creationCandidate)
        {
            if (Current.IsMonitoring && awaitingVoicePrompt)
            {
                var existingPrompt = isCreate ? DshTaskVoiceParser.ReadPrompt(text) : null;
                if (existingPrompt is not null)
                {
                    await SendMessageAsync(existingPrompt, cancellationToken);
                    return new(true, "任务内容已发送到刚才新建的会话，未重复创建。");
                }
                return new(true, "任务会话已经新建，请继续说要执行的内容；不会重复创建。") { ListenForReply = true };
            }
            if (Current.IsMonitoring && Current.State is not ("completed" or "failed" or "cancelled" or "idle"))
                return new(true, "已有任务正在执行或等待回答，请先完成当前任务，或明确说停止任务监控，再新建任务。");
            var explicitDirectory = DshTaskVoiceParser.ReadDirectory(text);
            if (explicitDirectory is null && !DshTaskVoiceParser.Normalize(DshTaskVoiceParser.Header(text)).Contains("默认目录")
                && System.Text.RegularExpressions.Regex.IsMatch(DshTaskVoiceParser.Header(text), @"在.*?(?:指定目录|路径|[A-Za-z]盘|[A-Za-z]:|目录下)"))
                return new(true, "未识别到完整任务目录，请重新说出完整路径；也可以说使用默认目录新建任务。") { ListenForReply = true };
            var directory = explicitDirectory ?? defaultRoot();
            var title = DshTaskVoiceParser.ReadTitle(text);
            // A loose ASR phrase chooses the task route but never invents its content.
            var prompt = isCreate ? DshTaskVoiceParser.ReadPrompt(text) : null;
            if (!isCreate)
            { voiceDraft = (directory, title, scopeFactory()); return new(true, "尚未创建任务，已保留创建草稿。请说明任务内容，加上要执行的指令。") { ListenForReply = true }; }
            var started = await StartAsync(new(directory, title, prompt ?? string.Empty), cancellationToken);
            if (started.State == "awaitingPrompt")
                return new(true, "任务会话已新建。请继续说要执行的内容。") { ListenForReply = true };
            return new(true, started.State == "unknown" ? started.StatusText + "，请核对会话。" : "任务已提交，接下来会持续监控状态。");
        }
        // Device-domain ASR ambiguity is resolved locally before any draft can
        // consume the utterance as a new task body. Real answers remain above.
        if (intent == DshVoiceIntent.ClarifySceneIntent && (voiceDraft is not null || awaitingVoicePrompt))
            return new(true, DshVoiceIntentRouter.SceneClarification(text)) { ListenForReply = true };
        if (voiceDraft is { } draft)
        {
            if (draft.Scope != scopeFactory())
            { voiceDraft = null; return new(true, "DSH 配置已改变，请重新创建任务。"); }
            if (intent == DshVoiceIntent.DeviceControl) return new(false, "");
            var prompt = DshTaskVoiceParser.ReadPrompt(text);
            if (prompt is null && (normalized.Length == 0 || normalized is "好的" or "好" or "嗯" or "继续"
                || DshTaskVoiceParser.IsApprovalReply(text)
                || DshTaskVoiceParser.IsCreationCandidate(text)))
                return new(true, "请告诉我新任务要做什么；或说取消创建。") { ListenForReply = true };
            prompt ??= text.Trim();
            voiceDraft = null;
            var started = await StartAsync(new(draft.Directory, draft.Title, prompt), cancellationToken);
            return new(true, started.State == "unknown" ? started.StatusText : "任务已提交，已开始监控。");
        }
        if (intent == DshVoiceIntent.DeviceControl) return new(false, "");
        if (awaitingVoicePrompt && !Current.NeedsAttention)
        {
            EnsureActive();
            var prompt = DshTaskVoiceParser.ReadPrompt(text);
            if (prompt is null && (normalized.Length == 0 || normalized is "好的" or "好" or "嗯" or "继续"
                || DshTaskVoiceParser.IsApprovalReply(text)))
                return new(true, "会话已新建，尚未提交任务内容。请告诉我要执行什么；或说取消创建。") { ListenForReply = true };
            await SendMessageAsync(prompt ?? text.Trim(), cancellationToken);
            return new(true, "任务内容已发送到刚才新建的会话，已开始监控。");
        }
        if (!Current.IsMonitoring)
        {
            if (DshTaskVoiceParser.IsApprovalReply(text))
                return new(true, "当前没有等待中的任务授权请求，未发送授权。");
            var target = client.Current.VoiceTarget;
            if (target is null || target.IsDeviceControl || target.IsArchived)
            {
                if (intent == DshVoiceIntent.ClarifySceneIntent)
                    return new(true, DshVoiceIntentRouter.SceneClarification(text)) { ListenForReply = true };
                return new(true, DshTaskVoiceParser.IsTaskRelated(text)
                    ? "当前没有选定的 DSH 任务。请先说新建一个 DSH 任务，或在会话页选择普通任务作为音箱目标。"
                    : "请说明是控制音箱，还是新建或继续 DSH 任务。");
            }
            // Merely loading context to disambiguate a voice reply is not an
            // explicit request to replace the user's current screen.
            await MonitorCoreAsync(target, restoreAwaitingPrompt: false, cancellationToken, requestDisplay: false);
        }
        EnsureActive();
        if (Current.State is "unknown" or "disconnected")
            return new(true, "当前任务状态尚未确认，请等待状态恢复后再说；未发送本次消息。");
        if (Current.NeedsAttention)
            return await RouteInteractionVoiceAsync(text, expectedContext, cancellationToken);
        if (intent == DshVoiceIntent.ClarifySceneIntent)
            return new(true, DshVoiceIntentRouter.SceneClarification(text)) { ListenForReply = true };
        if (IsInteractionCommand(normalized))
            return new(true, "当前没有等待回答的问题。可以说任务状态，或继续说任务指令。");
        // Approval words outside a live request must never become an implicit authorization.
        if (DshTaskVoiceParser.IsApprovalReply(text))
            return new(true, "当前没有等待中的授权请求，未发送授权。");
        await SendMessageAsync(text, cancellationToken);
        return new(true, "消息已发送到当前任务，会继续监控。");
    }

    private void EnsureScope()
    {
        if (activeScope != scopeFactory() || !client.Current.IsConnected)
            throw new InvalidOperationException("DSH 数据目录、Profile 或连接已改变，请重新选择任务。");
    }
    private void EnsureActive(string? id = null)
    {
        EnsureScope();
        if (!Current.IsMonitoring || Current.SessionId.Length == 0 || (id is not null && Current.SessionId != id))
            throw new InvalidOperationException("当前没有可交互的监控任务。");
    }
    private void Publish(DshTaskSnapshot snapshot)
    {
        lock (stateGate) current = snapshot;
        foreach (EventHandler<DshTaskSnapshot> handler in Changed?.GetInvocationList() ?? [])
            try { handler(this, snapshot); } catch (Exception ex) { Debug.WriteLine(ex); }
    }
    private long? CaptureDisplayRevision() => voiceDisplayRevision.Value ?? displayRevisionProvider?.Invoke();

    private async Task FeedbackAsync(string cue, CancellationToken token, long? displayRequestRevision = null)
    {
        var snapshot = Current;
        if (feedback is null || token.IsCancellationRequested || !snapshot.IsMonitoring || activeScope != scopeFactory()) return;
        if (cue == "task_display_requested")
            snapshot = snapshot with { DisplayRequestRevision = displayRequestRevision };
        try { await feedback(snapshot, cue, token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine("任务反馈失败：" + ex.Message); }
    }
    private static string StatusLabel(string state) => state switch
    {
        "running" => "任务运行中", "waitingApproval" => "任务等待授权", "waitingInput" => "任务等待回答",
        "completed" => "本轮任务已完成", "failed" => "本轮任务失败", "cancelled" => "当前轮已取消", "idle" => "任务空闲",
        "awaitingPrompt" => "任务已新建，等待内容", _ => "任务状态未确认"
    };
    private string IdentityPath() => IdentityPath(activeScope);
    private string IdentityPath(string scope) => Path.Combine(stateRoot,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + ".json");
    private static DshTaskInteraction CaptureInteraction(DshTaskInteraction source)
        => source with { Questions = source.Questions.Select(question => question with { Options = question.Options.ToArray() }).ToArray() };
    private void SaveIdentity()
    {
        if (Current.SessionId.Length == 0 || !Current.IsMonitoring || activeScope != scopeFactory()) return;
        try
        {
            Directory.CreateDirectory(stateRoot);
            var file = IdentityPath();
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { scope = activeScope, sessionId = Current.SessionId,
                title = Current.Title, workingDirectory = Current.WorkingDirectory, awaitingPrompt = awaitingVoicePrompt }));
            File.Move(temporary, file, true);
        }
        catch (Exception ex) { Debug.WriteLine("任务监控身份保存失败：" + ex.Message); }
    }
    private void ClientChanged(object? sender, DshSessionsSnapshot snapshot)
    {
        if (disposed) return;
        lock (stateGate)
        {
            if (current.IsMonitoring)
            {
                var session = snapshot.Sessions.FirstOrDefault(item => item.Id == current.SessionId);
                // Session-list refreshes are frequent. Only actual remote activity or
                // a connection transition should defeat the idle polling backoff.
                if (observedConnected != snapshot.IsConnected
                    || session?.RuntimeStatus != observedSession?.RuntimeStatus
                    || session?.UpdatedAt != observedSession?.UpdatedAt
                    || session?.IsArchived != observedSession?.IsArchived)
                    monitorWake.TrySetResult();
                observedSession = session;
                observedConnected = snapshot.IsConnected;
            }
        }
        if (Current.IsMonitoring && activeScope != scopeFactory())
            StopForScopeChange();
        if (snapshot.IsConnected && Current.SessionId.Length == 0 && !Current.IsBusy && restoreAttemptedScope != scopeFactory())
        { restoreAttemptedScope = scopeFactory(); _ = RestoreSavedAsync(); }
    }
    private void StopForScopeChange()
    {
        var previousSessionId = Current.SessionId;
        monitoring?.Cancel(); generation++;
        voiceDraft = null;
        awaitingVoicePrompt = false;
        voiceAnswers.Clear();
        questionInteractionId = string.Empty;
        questionInteractionSchema = null;
        Publish(DshTaskSnapshot.Initial with { State = "disconnected", StatusText = "配置已改变，监控已停止" });
        if (client.Current.VoiceTarget?.Id == previousSessionId) client.ClearVoiceTarget();
    }
    private async Task RestoreSavedAsync()
    {
        try
        {
            var scope = scopeFactory();
            var restoreGeneration = generation;
            var file = Path.Combine(stateRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + ".json");
            if (!File.Exists(file) || new FileInfo(file).Length > 16384) return;
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, shutdown.Token));
            if (doc.RootElement.GetProperty("scope").GetString() != scope) return;
            var id = doc.RootElement.GetProperty("sessionId").GetString();
            var existing = client.Current.Sessions.FirstOrDefault(s => s.Id == id && !s.IsArchived && !s.IsDeviceControl);
            if (existing is null && !client.Current.Sessions.Any(s => s.Id == id) && scope == scopeFactory()
                && restoreAttemptedScope == scope)
            {
                // Connected notifications can precede the first session-list response.
                // Leave the saved identity eligible for the next list notification.
                restoreAttemptedScope = string.Empty;
                return;
            }
            if (existing is not null && !Current.IsMonitoring && !Current.IsBusy && scope == scopeFactory())
            {
                var restoreWaiting = doc.RootElement.TryGetProperty("awaitingPrompt", out var waiting) && waiting.ValueKind == JsonValueKind.True;
                await MonitorCoreAsync(existing, restoreWaiting, shutdown.Token, scope, restoreGeneration);
            }
        }
        catch (Exception ex) { Debug.WriteLine("任务监控恢复失败，未重发任务：" + ex.Message); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        client.Changed -= ClientChanged;
        monitoring?.Cancel(); shutdown.Cancel();
    }
}
