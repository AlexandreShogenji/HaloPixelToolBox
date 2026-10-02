using System.Reflection;
using HaloPixelToolBox;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.ViewModels;
using HaloPixelToolBox.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

public static class AwaitingPromptProbe
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        var service=App.DshSessions;var task=App.DshTasks;var voice=App.VoiceAgent;
        var device=new DshSessionSummary("EMPTY-DEVICE","音箱控制",@"C:\App",DateTimeOffset.Now,"idle") { IsDeviceControl=true,DeviceControlKind="canonical" };
        var empty=new DshSessionSummary("EMPTY-TASK","新空任务",@"C:\Tasks\empty",DateTimeOffset.Now,"idle");
        var caps=new DshSessionCapabilities(true,true,true,true,true)
            {CanCreateTasks=true,CanPromptTasks=true,CanMonitorTasks=true,CanCancelTasks=true,CanAdoptTasks=true};
        var waiting=new DshTaskSnapshot(empty.Id,empty.Title,empty.WorkingDirectory,"awaitingPrompt","任务已新建，等待内容",
            "请在会话输入框发送任务内容。","",[],true,false);
        int ordinarySends=0,deviceSends=0;
        void Reset()
        {
            task.Reset();voice.IsRunning=true;voice.Starts=0;voice.Starter=null;DisplayFeatureProfile.DshProfileName="default";
            service.HistoryReader=(_,_,_)=>Task.FromResult(new DshHistoryPage([],null,false));
            service.MessageSender=(id,_,_)=> {ordinarySends++;return Task.FromResult(new DshDeviceCommandResult(true,"ok","ok",id,[],[]));};
            service.DeviceSender=(_,_)=> {deviceSends++;return Task.FromResult(new DshDeviceCommandResult(true,"ok","ok",device.Id,[],[]));};
            service.Publish(new(true,"host","connected",[device],null) {Home=@"C:\EmptyHome",DeviceSessionId=device.Id,Capabilities=caps});
        }
        Reset();var vm=new DshSessionsPageViewModel();vm.Attach();
        service.Publish(service.Current with {Sessions=[device,empty]});task.Publish(waiting);
        check(vm.SelectedSession?.Session.Id==empty.Id&&!vm.SelectedSession.IsDeviceControlGroup,"awaitingPrompt confirmed ordinary id opens the new empty task from device view");
        check(vm.TaskSummary.Contains("等待内容")&&vm.SelectedRuntimeStatus==waiting.StatusText
            &&vm.EmptyHistoryMessage.Contains("输入任务内容")&&vm.ComposerPlaceholder.Contains("任务内容"),"empty task panel header history and composer consistently indicate pending task content");
        check(!vm.CanStartTask&&!vm.CanCancelTask&&vm.CancelTaskVisibility==Visibility.Collapsed&&vm.CanStopTaskMonitoring,
            "unsubmitted empty task blocks replacement and current-turn cancellation but can stop monitoring");
        await vm.CancelTaskCommand.ExecuteAsync(null);
        check(task.CancelCalls==0,"empty task cannot submit cancellation of a nonexistent execution round");
        check(!vm.CanSendMessage,"blank task composer cannot submit an invented prompt");
        service.Publish(service.Current with {Capabilities=caps with {CanSendMessages=false}});vm.MessageDraft="只读取项目目录";
        check(vm.CanSendMessage,"awaiting task accepts actual draft through prompt capability without ordinary-chat capability");
        var notified=new HashSet<string>();vm.PropertyChanged+=(_,e)=> {if(e.PropertyName is not null)notified.Add(e.PropertyName);};
        var sendResult=new TaskCompletionSource<DshTaskSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        task.Sender=(_,_)=>sendResult.Task;
        var sending=vm.SendMessageCommand.ExecuteAsync(null);
        await vm.SendMessageCommand.ExecuteAsync(null);
        check(task.SendCalls==1&&ordinarySends==0&&deviceSends==0&&vm.IsSending,"first task prompt is submitted once exclusively to task service and repeated send is blocked");
        task.Publish(waiting with {State="running",StatusText="消息已提交",Detail="正在执行"});
        sendResult.SetResult(new(empty.Id,"FIRST-PROMPT",true));await sending;
        check(vm.MessageDraft==string.Empty&&vm.TaskSnapshot.State=="running"&&!vm.ComposerPlaceholder.Contains("任务内容")
            &&!vm.EmptyHistoryMessage.Contains("输入任务内容")&&vm.SelectedRuntimeStatus=="消息已提交"&&vm.CanCancelTask,
            "acknowledged first prompt clears draft and waiting hints without recreating the session");
        check(notified.Contains(nameof(vm.SelectedRuntimeStatus))&&notified.Contains(nameof(vm.SelectedStatus))
            &&notified.Contains(nameof(vm.EmptyHistoryMessage))&&notified.Contains(nameof(vm.ComposerPlaceholder)),
            "task progress raises all bound selected header history and composer notifications");
        task.Publish(waiting);task.Sender=(_,_)=>Task.FromResult(new DshTaskSubmission(empty.Id,"UNKNOWN",false));vm.MessageDraft="保留未确认内容";
        await vm.SendMessageCommand.ExecuteAsync(null);
        check(vm.TaskSnapshot.State=="awaitingPrompt"&&vm.MessageDraft=="保留未确认内容"&&vm.SendStatus.Contains("尚未确认")
            &&ordinarySends==0,"unconfirmed first submission retains waiting state and draft without fallback or automatic retry");
        task.Sender=(_,_)=>throw new IOException("test transport failure");vm.MessageDraft="网络失败也保留";await vm.SendMessageCommand.ExecuteAsync(null);
        check(vm.TaskSnapshot.State=="awaitingPrompt"&&vm.MessageDraft=="网络失败也保留"&&ordinarySends==0&&deviceSends==0,
            "failed task submission stays on same empty task and never falls back to another send path");
        task.Publish(DshTaskSnapshot.Initial);vm.TaskSnapshot=waiting;vm.MessageDraft="陈旧状态不能发送";
        check(!vm.CanSendMessage,"stale task snapshot cannot use ordinary chat after authoritative task monitoring has stopped");vm.Detach();

        Reset();vm=new();vm.Attach();task.Starter=(request,_)=>
        {
            service.Publish(service.Current with {Sessions=[device,empty]});return Task.FromResult(waiting);
        };
        await vm.StartTaskAsync(new(@"C:\Tasks","新空任务",string.Empty));
        check(task.StartCalls==1&&task.SendCalls==0&&task.LastStart!.Prompt==string.Empty&&vm.TaskSnapshot.State=="awaitingPrompt"
            &&vm.SelectedSession?.Session.Id==empty.Id&&vm.TaskOperationStatus.Contains("会话已新建")&&!vm.TaskOperationStatus.Contains("任务已启动"),
            "UI task creation preserves empty prompt and reports created awaiting session rather than running task");vm.Detach();

        Reset();var page=new DshSessionsPage();page.RaiseLoaded();
        task.Starter=(request,_)=> {service.Publish(service.Current with {Sessions=[device,empty]});return Task.FromResult(waiting);};
        ContentDialog.NextShow=dialog=>
        {
            var body=(StackPanel)((ScrollViewer)dialog.Content!).Content!;
            var boxes=body.Children.OfType<TextBox>().ToArray();
            boxes[0].Text="新空任务";boxes[1].Text=@"C:\Tasks";
            check(dialog.IsPrimaryButtonEnabled&&dialog.PrimaryButtonText=="创建任务"&&boxes[2].Header!.ToString()!.Contains("稍后"),
                "creation dialog explicitly allows empty prompt and labels creation accurately");
            boxes[2].Text=new string('字',4097);check(!dialog.IsPrimaryButtonEnabled,"optional task prompt still rejects values above official length limit");
            boxes[2].Text=" \r\n ";check(dialog.IsPrimaryButtonEnabled,"whitespace-only optional prompt creates a genuinely empty task");
            return Task.FromResult(ContentDialogResult.Primary);
        };
        typeof(DshSessionsPage).GetMethod("NewSpeakerTask_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(page,[page,new RoutedEventArgs()]);
        for(int i=0;i<100&&(task.StartCalls==0||page.ViewModel.IsTaskActionBusy);i++)await Task.Delay(10);
        check(task.StartCalls==1&&task.LastStart!.Prompt==string.Empty&&task.SendCalls==0&&page.ViewModel.TaskSnapshot.State=="awaitingPrompt"
            &&page.ViewModel.SelectedSession?.Session.Id==empty.Id,"empty task dialog creates once and opens confirmed ordinary task with no prompt submission");
        page.RaiseUnloaded();ContentDialog.NextShow=null;task.Reset();
    }
}
