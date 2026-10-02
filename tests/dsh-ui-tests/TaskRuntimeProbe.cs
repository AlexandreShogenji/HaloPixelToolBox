using System.Reflection;
using System.Xml.Linq;
using HaloPixelToolBox;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.ViewModels;
using HaloPixelToolBox.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

public static class TaskRuntimeProbe
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        var task=App.DshTasks;var service=App.DshSessions;var voice=App.VoiceAgent;
        var a=new DshSessionSummary("A","任务 A",@"C:\Tasks\a",DateTimeOffset.Now,"running");
        var b=new DshSessionSummary("B","任务 B",@"C:\Tasks\b",DateTimeOffset.Now,"idle");
        var device=new DshSessionSummary("DEVICE","音箱控制",@"C:\App",DateTimeOffset.Now,"idle") { IsDeviceControl=true,DeviceControlKind="canonical" };
        var caps=new DshSessionCapabilities(true,true,true,true,true) { CanCreateTasks=true,CanPromptTasks=true,CanMonitorTasks=true,CanRespondToTasks=true,CanCancelTasks=true,CanAdoptTasks=true };
        void Reset()
        {
            task.Reset();voice.Starts=0;voice.IsRunning=false;voice.Starter=null;
            DisplayFeatureProfile.DshProfileName="default";
            service.Publish(new(true,"http://127.0.0.1:8765","connected",[a,b,device],null) { Home=@"C:\DSH",DeviceSessionId="DEVICE",Capabilities=caps });
            service.HistoryReader=(_,_,_)=>Task.FromResult(new DshHistoryPage([],null,false));
        }
        async Task Wait(Func<bool> ready)
        {
            for(int i=0;i<150&&!ready();i++) await Task.Delay(10);
            if(!ready()) throw new Exception("UI probe timeout");
        }
        DshTaskSnapshot Active(string id="A",IReadOnlyList<DshTaskInteraction>? pending=null)
            => new(id,"任务 A",@"C:\Tasks\a","running","运行中","detail","",pending??[],true,false);
        var approval=new DshTaskInteraction("APPROVAL-1","approval","write_file",new string('字',800),[]);
        var question=new DshTaskInteraction("QUESTION-1","question","request_user_input","reason",
            [new("q1","方向","选择开发方向",[new("灯效","设备灯效")]),new("q2","范围","选择范围",[new("全部","全部文件")])]);
        Reset();var vm=new DshSessionsPageViewModel();vm.Attach();
        check(vm.CanStartTask&&vm.TaskPanelVisibility==Visibility.Collapsed,"task entry enabled only with complete task capabilities; idle monitor occupies no history height");
        vm.OpenSession(vm.Sessions.First(x=>x.Id=="A"));
        check(vm.CanMonitorSelectedTask,"ordinary unarchived session can be adopted for task monitoring");
        vm.OpenSession(vm.Sessions.First(x=>x.IsDeviceControlGroup));
        check(!vm.CanMonitorSelectedTask,"device control group is never adopted as an ordinary task");
        task.Publish(Active());
        check(!vm.CanStartTask&&vm.CanOpenTaskSession&&vm.CanStopTaskMonitoring&&vm.CanCancelTask,"running monitored task exposes view stop and capability-bound cancel; cannot silently replace running task");
        service.Publish(service.Current with { Capabilities=caps with { CanCancelTasks=false,CanAdoptTasks=false } });
        vm.OpenSession(vm.Sessions.First(x=>x.Id=="A"));
        check(!vm.CanCancelTask&&vm.CancelTaskVisibility==Visibility.Collapsed&&!vm.CanMonitorSelectedTask,"unsupported cancellation hidden and adoption capability respected");
        service.Publish(service.Current with { Capabilities=caps });
        await vm.CancelTaskCommand.ExecuteAsync(null);
        check(task.CancelCalls==1&&task.StopCalls==0&&vm.TaskOperationStatus.Contains("当前轮"),"cancel current turn is separate from stopping monitoring");
        await vm.StopTaskMonitoringCommand.ExecuteAsync(null);
        check(task.StopCalls==1&&task.CancelCalls==1&&!task.Current.IsMonitoring&&vm.TaskOperationStatus.Contains("仍保留"),"stop monitoring preserves real task and does not cancel it");
        task.Publish(DshTaskSnapshot.Initial);
        await vm.StartTaskAsync(new(@"C:\Tasks","测试","只读任务"));
        check(task.StartCalls==1&&voice.Starts==1&&vm.TaskSnapshot.IsMonitoring&&vm.TaskOperationStatus.Contains("已启动"),"new task starts microphone after accepted task creation without waiting for completion");
        task.Publish(DshTaskSnapshot.Initial);voice.IsRunning=true;
        await vm.StartTaskAsync(new(@"C:\Tasks","第二任务","只读任务"));
        check(task.StartCalls==2&&voice.Starts==1,"already-running microphone is not restarted");
        task.Publish(DshTaskSnapshot.Initial);voice.IsRunning=false;voice.Starter=_=>Task.FromResult(false);
        await vm.StartTaskAsync(new(@"C:\Tasks","第三任务","只读任务"));
        check(task.StartCalls==3&&vm.TaskSummary.Contains("麦克风启动失败")&&vm.TaskSnapshot.IsMonitoring,"microphone failure keeps confirmed task and appears in compact status");
        task.Publish(DshTaskSnapshot.Initial);voice.IsRunning=true;
        task.Starter=(_,_)=>Task.FromResult(Active("TASK") with { State="unknown",StatusText="未确认" });
        await vm.StartTaskAsync(new(@"C:\Tasks","未知提交","只读任务"));
        check(task.StartCalls==4&&vm.TaskSummary.Contains("未确认")&&!vm.CanStartTask,"unknown task submission stays monitored and is not recreated or falsely reported completed");
        task.Starter=null;task.Publish(Active("A"));
        vm.OpenSession(vm.Sessions.First(x=>x.Id=="A"));vm.MessageDraft="继续整理";
        int ordinary=0;service.MessageSender=(id,text,ct)=> { ordinary++;return Task.FromResult(new DshDeviceCommandResult(true,"ok","ok",id,[],[]) {Accepted=true,Completed=true}); };
        await vm.SendMessageCommand.ExecuteAsync(null);
        check(task.SendCalls==1&&ordinary==0&&vm.MessageDraft==string.Empty&&vm.SendStatus.Contains("正在执行"),"monitored session composer uses acknowledged nonblocking task prompt and clears accepted draft");
        task.Sender=(_,_)=>Task.FromResult(new DshTaskSubmission("A","R",false));vm.MessageDraft="保留这一句";
        await vm.SendMessageCommand.ExecuteAsync(null);
        check(task.SendCalls==2&&vm.MessageDraft=="保留这一句"&&vm.SendStatus.Contains("尚未确认"),"unacknowledged task prompt keeps draft and requires history verification");
        task.Publish(Active("A",[approval]));vm.MessageDraft="批准";
        check(!vm.CanSendMessage&&vm.ComposerPlaceholder.Contains("授权"),"ordinary composer cannot bypass pending approval or interpret chat text as permission");
        await vm.RespondTaskApprovalAsync("A","APPROVAL-OLD",true);
        check(task.ApproveCalls==0&&vm.TaskOperationStatus.Contains("已改变"),"stale approval id never authorizes replacement request");
        await vm.RespondTaskApprovalAsync("A",approval.Id,true);
        check(task.ApproveCalls==1&&task.LastApproval==("APPROVAL-1",true),"only exact current approval gets one explicit approved response");
        task.Publish(Active("A",[approval]));await vm.RespondTaskApprovalAsync("A",approval.Id,false);
        check(task.ApproveCalls==2&&task.LastApproval==("APPROVAL-1",false),"explicit refusal targets only current approval");
        task.Publish(Active("A",[question]));
        await vm.RespondTaskQuestionAsync("A",question.Id,new Dictionary<string,string>{{"q1","灯效"},{"q2","全部"}});
        check(task.QuestionCalls==1&&task.LastQuestion!.Value.Answers.Count==2&&task.LastQuestion.Value.Answers["q2"]=="全部","multi-question answers preserve individual stable question ids");
        task.Publish(Active("A"));vm.OpenSession(vm.Sessions.First(x=>x.Id=="B"));vm.MessageDraft="普通消息";
        await vm.SendMessageCommand.ExecuteAsync(null);
        check(ordinary==1&&task.SendCalls==2,"other ordinary sessions keep existing conversation message path");
        await vm.OpenTaskSessionCommand.ExecuteAsync(null);
        check(vm.SelectedSession?.Session.Id=="A","view monitored task opens exact task session instead of device group");
        task.Publish(DshTaskSnapshot.Initial);voice.IsRunning=false;
        var lateStart=new TaskCompletionSource<DshTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken lateStartToken=default;
        task.Starter=(_,ct)=>{lateStartToken=ct;return lateStart.Task;};
        var startPending=vm.StartTaskAsync(new(@"C:\Tasks","旧 scope","只读任务"));
        check(vm.IsTaskActionBusy,"task creation keeps duplicate actions disabled until admission returns");
        service.Publish(service.Current with {Home=@"C:\DSH2"});
        check(lateStartToken.IsCancellationRequested&&!vm.IsTaskActionBusy&&!vm.CanRespondToTask,"scope change cancels pending task UI action and invalidates interactions");
        lateStart.SetResult(Active("OLD-SCOPE"));await startPending;
        check(voice.Starts==2&&vm.TaskSnapshot.SessionId==string.Empty&&vm.TaskOperationStatus==string.Empty,"late old-scope start cannot launch microphone overwrite status or expose old task");
        vm.Detach();check(!vm.CanStartTask&&!vm.CanRespondToTask&&!vm.CanOpenTaskSession,"detached page cannot create operate or respond to tasks");

        Reset();var page=new DshSessionsPage();page.RaiseLoaded();
        void Click(string method)=>typeof(DshSessionsPage).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(page,[page,new RoutedEventArgs()]);
        StackPanel Body(ContentDialog dialog)=>(StackPanel)((ScrollViewer)dialog.Content!).Content!;
        ContentDialog.NextShow=dialog=>
        {
            var boxes=Body(dialog).Children.OfType<TextBox>().ToArray();
            check(boxes.Length==3&&boxes[1].Text==DisplayFeatureProfile.DshTaskRootDirectory,"task creation dialog defaults root to configured Documents task directory");
            boxes[0].Text="任务测试";boxes[2].Text="只读检查";boxes[1].Text=@"\\server\share";
            check(!dialog.IsPrimaryButtonEnabled,"task dialog forbids UNC task roots");
            boxes[1].Text=@"C:\Tasks";
            check(dialog.IsPrimaryButtonEnabled&&dialog.DefaultButton==ContentDialogButton.None,"task creation validates title directory and optional prompt and never implicit Enter submission");
            return Task.FromResult(ContentDialogResult.Primary);
        };
        Click("NewSpeakerTask_Click");await Wait(()=>task.StartCalls==1&&!page.ViewModel.IsTaskActionBusy);
        check(task.LastStart==new DshTaskStartRequest(@"C:\Tasks","任务测试","只读检查"),"task creation dialog sends exact reviewed directory title and prompt");
        task.Publish(Active("A",[approval]));
        ContentDialog.NextShow=dialog=>
        {
            var texts=Body(dialog).Children.OfType<TextBlock>().Select(x=>x.Text).ToArray();
            check(texts.Contains(approval.Reason)&&texts.Any(x=>x?.Contains("write_file")==true),"approval details show full tool and full long reason without truncation");
            check(dialog.PrimaryButtonText=="仅批准本次"&&dialog.SecondaryButtonText=="拒绝本次"&&dialog.DefaultButton==ContentDialogButton.None,"approval dialog exposes one-shot approval and refusal with no default approval");
            return Task.FromResult(ContentDialogResult.Primary);
        };
        Click("TaskDetails_Click");await Wait(()=>task.ApproveCalls==1&&!page.ViewModel.IsTaskActionBusy);
        check(task.LastApproval==("APPROVAL-1",true),"approval dialog uses captured current interaction id");
        task.Publish(Active("A",[question]));
        ContentDialog.NextShow=dialog=>
        {
            var boxes=Body(dialog).Children.OfType<TextBox>().ToArray();
            check(boxes.Length==2&&!dialog.IsPrimaryButtonEnabled,"question dialog gives each question independent input and requires all answers");
            boxes[0].Text="灯效";check(!dialog.IsPrimaryButtonEnabled,"partial multi-question answer cannot submit");
            boxes[1].Text="全部";check(dialog.IsPrimaryButtonEnabled,"complete multi-question answer enables only explicit submission");
            return Task.FromResult(ContentDialogResult.Primary);
        };
        Click("TaskDetails_Click");await Wait(()=>task.QuestionCalls==1&&!page.ViewModel.IsTaskActionBusy);
        check(task.LastQuestion!.Value.Answers["q1"]=="灯效"&&task.LastQuestion.Value.Answers["q2"]=="全部","question dialog submits each answer under captured question id");
        task.Publish(Active("A",[approval]));
        var pending=new TaskCompletionSource<ContentDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ContentDialog.NextShow=_=>pending.Task;Click("TaskDetails_Click");
        var stale=ContentDialog.Last!;
        task.Publish(Active("A",[approval with {Id="APPROVAL-2"}]));
        check(!stale.IsPrimaryButtonEnabled&&!stale.IsSecondaryButtonEnabled,"replacement pending request disables stale open dialog buttons");
        pending.SetResult(ContentDialogResult.Primary);await Task.Delay(30);
        check(task.ApproveCalls==1,"late stale dialog approval is not redirected to new pending request");
        task.Publish(DshTaskSnapshot.Initial);
        var createPending=new TaskCompletionSource<ContentDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ContentDialog.NextShow=dialog=>
        {
            var boxes=Body(dialog).Children.OfType<TextBox>().ToArray();boxes[0].Text="旧 scope";boxes[1].Text=@"C:\Tasks";boxes[2].Text="不要跨目录执行";
            return createPending.Task;
        };
        Click("NewSpeakerTask_Click");
        service.Publish(service.Current with { Home=@"C:\DSH2" });
        createPending.SetResult(ContentDialogResult.Primary);await Task.Delay(30);
        check(task.StartCalls==1,"scope change while creation dialog is open cannot create task in different DSH home");
        page.RaiseUnloaded();ContentDialog.NextShow=null;
        var xaml=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","DshSessionsPage.xaml"));
        XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        check(xaml.Descendants(ns+"Button").Any(x=>(string?)x.Attribute("Click")=="NewSpeakerTask_Click"),"XAML provides task creation entry");
        check(xaml.Descendants(ns+"MenuFlyoutItem").Any(x=>(string?)x.Attribute("Text")=="取消当前轮"&&(string?)x.Attribute("Visibility")=="{x:Bind ViewModel.CancelTaskVisibility, Mode=OneWay}"),"XAML cancellation remains separate and capability-gated");
        var panel=xaml.Descendants(ns+"Border").Single(x=>(string?)x.Attribute("Visibility")=="{x:Bind ViewModel.TaskPanelVisibility, Mode=OneWay}");
        check(panel.Descendants(ns+"TextBlock").Count()==1&&!panel.Descendants(ns+"Expander").Any(),"task monitor remains a single compact row and expands details outside history layout");
        await TaskFollowProbe.RunAsync(check);
        await AwaitingPromptProbe.RunAsync(check);
    }
}
