using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services;
using HaloPixelToolBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HaloPixelToolBox;

public static class VoiceRuntimeProbe
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        var voice=App.VoiceAgent;var sessions=App.DshSessions;var tasks=App.DshTasks;
        voice.Starter=null;voice.Stopper=null;voice.Starts=voice.Stops=0;voice.Publish(VoiceAgentSnapshot.Initial);
        tasks.Reset();sessions.Publish(DshSessionsSnapshot.Initial);
        var savedProfile=Profiles.CrossVersionProfiles.DisplayFeatureProfile.DshProfileName;
        var savedRoot=Profiles.CrossVersionProfiles.DisplayFeatureProfile.DshTaskRootDirectory;
        var vm=new DshSessionsPageViewModel();
        check(!vm.CanToggleVoice && !vm.ToggleVoiceListeningCommand.CanExecute(null),"detached page cannot control microphone");
        check(vm.VoiceActionText=="开始监听" && vm.VoiceBusyVisibility==Visibility.Collapsed,"initial microphone action is idle");
        vm.Attach();
        check(vm.CanToggleVoice && vm.ToggleVoiceListeningCommand.CanExecute(null),"saved-profile voice start is available before DSH connection");
        check(vm.ConnectActionVisibility==Visibility.Visible,"disconnected page exposes connect action");
        var notifications=new List<string?>();vm.PropertyChanged+=(_,e)=>notifications.Add(e.PropertyName);
        sessions.Publish(sessions.Current with { IsConnected=true });
        check(vm.ConnectActionVisibility==Visibility.Collapsed,"connected page hides connect action");
        check(notifications.Contains(nameof(vm.ConnectActionVisibility)),"connection transition notifies connect visibility binding");
        sessions.Publish(sessions.Current with { IsConnected=false });
        check(vm.ConnectActionVisibility==Visibility.Visible,"disconnect restores connect action");

        tasks.Publish(new("TASK-VOICE","保留任务",@"C:\Tasks\voice","running","运行中","","",[],true,false));
        var monitored=tasks.Current;
        var pendingStart=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        voice.Starter=_=>pendingStart.Task;
        var starting=vm.ToggleVoiceListeningCommand.ExecuteAsync(null);
        check(vm.IsVoiceActionBusy && !vm.CanToggleVoice,"voice start prevents duplicate actions");
        check(vm.VoiceActionText=="启动中…" && vm.VoiceBusyVisibility==Visibility.Visible,"voice start has compact progress feedback");
        check(voice.Starts==1 && vm.VoiceStatusText=="正在启动","start uses shared service and its actual status");
        pendingStart.SetResult(true);await starting;
        check(!vm.IsVoiceActionBusy && vm.CanToggleVoice,"completed startup clears page action busy state");
        check(vm.VoiceActionText=="停止监听" && vm.VoiceStatusText=="正在等待唤醒","running microphone exposes stop and real wake status");
        check(vm.VoiceBusyVisibility==Visibility.Collapsed && vm.VoiceDetail.Contains("请说花再花再"),"ready voice status is not a perpetual progress state");
        check(tasks.Current==monitored && tasks.StopCalls==0 && tasks.CancelCalls==0,"microphone start does not end or detach monitored DSH task");
        check(savedProfile==Profiles.CrossVersionProfiles.DisplayFeatureProfile.DshProfileName && savedRoot==Profiles.CrossVersionProfiles.DisplayFeatureProfile.DshTaskRootDirectory,"voice shortcut never writes saved profile settings");
        voice.Publish(voice.Current with { Phase=VoiceAgentPhase.Executing,Status="任务正在执行",Detail="本地 DSH 已接受",LastTranscript="创建任务",LastResponse="已新建任务" });
        check(vm.VoiceStatusText=="任务正在执行" && vm.VoiceDetail.Contains("最近识别：创建任务") && vm.VoiceDetail.Contains("最近回复：已新建任务"),"live details expose latest transcript and response");
        check(vm.CanToggleVoice && vm.VoiceTooltip.Contains("不会结束"),"running command still permits stopping microphone without task cleanup");
        var pendingStop=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        voice.Stopper=_=>pendingStop.Task;
        var stopping=vm.ToggleVoiceListeningCommand.ExecuteAsync(null);
        check(vm.IsVoiceActionBusy && vm.VoiceActionText=="停止中…" && !vm.CanToggleVoice,"stop serializes repeated microphone actions");
        pendingStop.SetResult();await stopping;
        check(voice.Stops==1 && vm.VoiceActionText=="开始监听" && vm.VoiceStatusText=="麦克风已停止","stop reports actual shared-service completion");
        check(tasks.Current==monitored && tasks.StopCalls==0 && tasks.CancelCalls==0,"microphone stop leaves task monitoring and work intact");
        voice.Starter=_=>Task.FromResult(false);voice.Stopper=null;
        await vm.ToggleVoiceListeningCommand.ExecuteAsync(null);
        check(vm.VoiceStatusText=="麦克风不可用" && vm.VoiceDetail.Contains("输入设备"),"failed startup preserves service error instead of pretending success");
        check(vm.CanToggleVoice && vm.VoiceActionText=="开始监听","failed startup can be retried from session page");
        voice.Starter=_=>throw new InvalidOperationException("测试启动失败");
        await vm.ToggleVoiceListeningCommand.ExecuteAsync(null);
        check(vm.VoiceStatusText=="启动语音监听未完成" && vm.VoiceDetail.Contains("测试启动失败"),"thrown start error becomes actionable UI detail");
        // The stub deliberately keeps its current transition on arbitrary errors.
        voice.Publish(VoiceAgentSnapshot.Initial);
        foreach(var phase in new[]{ VoiceAgentPhase.Starting,VoiceAgentPhase.LoadingModel,VoiceAgentPhase.Stopping })
        {
            voice.Publish(new(phase,"过渡中","", "","",true));
            check(!vm.CanToggleVoice && !vm.ToggleVoiceListeningCommand.CanExecute(null),$"external {phase} disables duplicate lifecycle action");
            check(vm.VoiceBusyVisibility==Visibility.Visible,$"external {phase} exposes busy indicator");
        }
        voice.Publish(VoiceAgentSnapshot.Initial);
        var startCancelled=false;
        voice.Starter=async ct=>
        {
            try { await Task.Delay(Timeout.Infinite,ct); }
            catch(OperationCanceledException) { startCancelled=true;throw; }
            return true;
        };
        var cancelled=vm.ToggleVoiceListeningCommand.ExecuteAsync(null);
        var detachedStatus=vm.VoiceStatusText;
        vm.Detach();await cancelled;
        check(startCancelled && !voice.Current.IsRunning,"leaving page cancels pending startup through page token");
        check(!vm.IsVoiceActionBusy && !vm.CanToggleVoice,"detached page has no pending UI operation or available action");
        check(vm.VoiceStatusText==detachedStatus,"cancelled startup does not publish stale callback into detached page");
        voice.Starter=null;
        vm.Attach();
        check(vm.VoiceStatusText=="启动已取消" && vm.CanToggleVoice,"reattach reads real stopped state and supports retry");
        await vm.ToggleVoiceListeningCommand.ExecuteAsync(null);
        check(voice.IsRunning && vm.VoiceActionText=="停止监听","retry after cancelled startup works");
        var stopsBeforeLeave=voice.Stops;
        vm.Detach();
        check(voice.IsRunning && voice.Stops==stopsBeforeLeave,"leaving ready page never automatically stops microphone");
        voice.Publish(voice.Current with { Status="离页之后的状态" });
        check(vm.VoiceStatusText!="离页之后的状态","detached page unsubscribes live status events");
        vm.Attach();
        check(vm.VoiceStatusText=="离页之后的状态","reattach synchronizes current shared service status");
        DispatcherQueue.ForceQueue=true;
        var appliedStatuses=new List<string>();
        vm.PropertyChanged+=(_,e)=> { if(e.PropertyName==nameof(vm.VoiceStatusText)) appliedStatuses.Add(vm.VoiceStatusText); };
        voice.Publish(voice.Current with { Status="过时排队状态" });
        voice.Publish(voice.Current with { Status="最新排队状态" });
        check(vm.VoiceStatusText=="离页之后的状态","background service callback waits for UI dispatcher");
        DispatcherQueue.Drain();
        check(vm.VoiceStatusText=="最新排队状态","queued callback applies latest service snapshot");
        check(!appliedStatuses.Contains("过时排队状态"),"older queued snapshot cannot overwrite newer current snapshot");
        voice.Publish(voice.Current with { Phase=VoiceAgentPhase.Starting,Status="其它页面启动中" });
        check(!vm.CanToggleVoice,"live service transition disables action before queued snapshot is applied");
        DispatcherQueue.Drain();
        voice.Publish(voice.Current with { Phase=VoiceAgentPhase.ListeningForWake,Status="旧页面排队回调" });
        vm.Detach();vm.Attach();
        var reappliedCount=appliedStatuses.Count;
        check(vm.VoiceStatusText=="旧页面排队回调","reattached page reads current status immediately");
        DispatcherQueue.Drain();
        check(appliedStatuses.Count==reappliedCount,"prior attachment's queued callback is rejected by generation guard");
        voice.Publish(voice.Current with { Status="离页未执行的回调" });
        vm.Detach();var detachedQueuedStatus=vm.VoiceStatusText;
        DispatcherQueue.Drain();
        check(vm.VoiceStatusText==detachedQueuedStatus,"queued callback cannot publish after leaving page");
        DispatcherQueue.ForceQueue=false;
        vm.Detach();tasks.Reset();voice.Publish(VoiceAgentSnapshot.Initial);
    }
}
