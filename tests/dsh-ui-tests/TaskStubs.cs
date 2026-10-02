using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services;
namespace HaloPixelToolBox;
public sealed class TaskServiceStub
{
    public DshTaskSnapshot Current { get; private set; } = DshTaskSnapshot.Initial;
    public event EventHandler<DshTaskSnapshot>? Changed;
    public int StartCalls, MonitorCalls, StopCalls, CancelCalls, ApproveCalls, QuestionCalls, SendCalls;
    public DshTaskStartRequest? LastStart;
    public (string Id,bool Approved)? LastApproval;
    public (string Id,IReadOnlyDictionary<string,string> Answers)? LastQuestion;
    public Func<DshTaskStartRequest,CancellationToken,Task<DshTaskSnapshot>>? Starter;
    public Func<CancellationToken,Task>? Monitor;
    public Func<string,CancellationToken,Task<DshTaskSubmission>>? Sender;
    public Func<DshTaskInteraction,bool,CancellationToken,Task>? ApprovalResponder;
    public Func<DshTaskInteraction,IReadOnlyDictionary<string,string>,CancellationToken,Task>? QuestionResponder;
    public void Publish(DshTaskSnapshot snapshot) { Current=snapshot;Changed?.Invoke(this,snapshot); }
    public async Task<DshTaskSnapshot> StartAsync(DshTaskStartRequest request,CancellationToken ct=default)
    {
        StartCalls++;LastStart=request;
        var started=Starter is null ? new("TASK","任务",@"C:\Tasks\task","running","任务已提交","","",[],true,false) : await Starter(request,ct);
        ct.ThrowIfCancellationRequested();
        Publish(started);return started;
    }
    public async Task MonitorAsync(DshSessionSummary session,CancellationToken ct=default)
    {
        MonitorCalls++;if(Monitor is not null) await Monitor(ct);ct.ThrowIfCancellationRequested();
        Publish(new(session.Id,session.Title,session.WorkingDirectory,"running","运行中","","",[],true,false));
    }
    public Task StopMonitoringAsync(CancellationToken ct=default) { StopCalls++;Publish(Current with { IsMonitoring=false,PendingInteractions=[],StatusText="已停止监控" });return Task.CompletedTask; }
    public Task CancelAsync(CancellationToken ct=default) { CancelCalls++;return Task.CompletedTask; }
    public Task RespondApprovalAsync(string id,bool approved,CancellationToken ct=default) { ApproveCalls++;LastApproval=(id,approved);Publish(Current with { PendingInteractions=Current.PendingInteractions.Where(x=>x.Id!=id).ToArray() });return Task.CompletedTask; }
    public Task RespondQuestionAsync(string id,IReadOnlyDictionary<string,string> answers,CancellationToken ct=default) { QuestionCalls++;LastQuestion=(id,answers);Publish(Current with { PendingInteractions=Current.PendingInteractions.Where(x=>x.Id!=id).ToArray() });return Task.CompletedTask; }
    public async Task RespondApprovalAsync(DshTaskInteraction expected,bool approved,CancellationToken ct=default)
    {
        var sessionId=Current.SessionId;
        if(expected.Type!="approval"||!Current.CanRespond||!Current.PendingInteractions.Any(item=>DshTaskInteractionIdentity.Matches(item,expected)))
            throw new InvalidOperationException("授权请求已改变。");
        if(ApprovalResponder is not null) await ApprovalResponder(expected,approved,ct);
        ct.ThrowIfCancellationRequested();
        if(Current.SessionId!=sessionId||!Current.PendingInteractions.Any(item=>DshTaskInteractionIdentity.Matches(item,expected)))
            throw new InvalidOperationException("授权请求已改变。");
        await RespondApprovalAsync(expected.Id,approved,ct);
    }
    public async Task RespondQuestionAsync(DshTaskInteraction expected,IReadOnlyDictionary<string,string> answers,CancellationToken ct=default)
    {
        var sessionId=Current.SessionId;
        if(expected.Type!="question"||!Current.CanRespond||!Current.PendingInteractions.Any(item=>DshTaskInteractionIdentity.Matches(item,expected)))
            throw new InvalidOperationException("问题已改变。");
        if(QuestionResponder is not null) await QuestionResponder(expected,answers,ct);
        ct.ThrowIfCancellationRequested();
        if(Current.SessionId!=sessionId||!Current.PendingInteractions.Any(item=>DshTaskInteractionIdentity.Matches(item,expected)))
            throw new InvalidOperationException("问题已改变。");
        await RespondQuestionAsync(expected.Id,answers,ct);
    }
    public Task<DshTaskSubmission> SendMessageAsync(string text,CancellationToken ct=default) { SendCalls++; return Sender?.Invoke(text,ct) ?? Task.FromResult(new DshTaskSubmission(Current.SessionId,"REQUEST",true)); }
    public void Reset()
    {
        StartCalls=MonitorCalls=StopCalls=CancelCalls=ApproveCalls=QuestionCalls=SendCalls=0;
        LastStart=null;LastApproval=null;LastQuestion=null;Starter=null;Monitor=null;Sender=null;ApprovalResponder=null;QuestionResponder=null;Publish(DshTaskSnapshot.Initial);
    }
}
public sealed class VoiceServiceStub
{
    public VoiceAgentSnapshot Current { get; private set; } = VoiceAgentSnapshot.Initial;
    public event EventHandler<VoiceAgentSnapshot>? StatusChanged;
    public bool IsRunning
    {
        get => Current.IsRunning;
        set => Publish(Current with { IsRunning = value, Phase = value ? VoiceAgentPhase.ListeningForWake : VoiceAgentPhase.Stopped,
            Status = value ? "正在等待唤醒" : "麦克风未监听" });
    }
    public int Starts, Stops;
    public Func<CancellationToken,Task<bool>>? Starter;
    public Func<CancellationToken,Task>? Stopper;
    public void Publish(VoiceAgentSnapshot snapshot) { Current=snapshot;StatusChanged?.Invoke(this,snapshot); }
    public async Task<bool> StartFromProfileAsync(CancellationToken ct=default)
    {
        Starts++;Publish(Current with { Phase=VoiceAgentPhase.Starting,Status="正在启动",IsRunning=true });
        try
        {
            var ok=Starter is null || await Starter(ct);ct.ThrowIfCancellationRequested();
            Publish(Current with { Phase=ok ? VoiceAgentPhase.ListeningForWake : VoiceAgentPhase.Error,
                Status=ok ? "正在等待唤醒" : "麦克风不可用",Detail=ok ? "请说花再花再" : "请检查输入设备",IsRunning=ok });
            return ok;
        }
        catch(OperationCanceledException)
        {
            Publish(Current with { Phase=VoiceAgentPhase.Stopped,Status="启动已取消",IsRunning=false });throw;
        }
    }
    public async Task StopAsync(CancellationToken ct=default)
    {
        Stops++;Publish(Current with { Phase=VoiceAgentPhase.Stopping,Status="正在停止",IsRunning=true });
        if(Stopper is not null) await Stopper(ct);ct.ThrowIfCancellationRequested();
        Publish(Current with { Phase=VoiceAgentPhase.Stopped,Status="麦克风已停止",Detail="当前未监听",IsRunning=false });
    }
}
