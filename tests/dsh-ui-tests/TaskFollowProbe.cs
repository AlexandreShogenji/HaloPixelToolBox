using HaloPixelToolBox;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.ViewModels;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;

public static class TaskFollowProbe
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        var service=App.DshSessions;var task=App.DshTasks;
        var a=new DshSessionSummary("FOLLOW-A","普通任务 A",@"C:\Tasks\a",DateTimeOffset.Now,"running");
        var b=new DshSessionSummary("FOLLOW-B","普通任务 B",@"C:\Tasks\b",DateTimeOffset.Now,"idle");
        var c=new DshSessionSummary("FOLLOW-C","新语音任务",@"C:\Tasks\c",DateTimeOffset.Now,"running");
        var device=new DshSessionSummary("FOLLOW-DEVICE","音箱控制",@"C:\App",DateTimeOffset.Now,"idle") {IsDeviceControl=true,DeviceControlKind="canonical"};
        var reads=new List<string>();
        var caps=new DshSessionCapabilities(true,true,true,true,true) {CanCreateTasks=true,CanPromptTasks=true,CanMonitorTasks=true,CanAdoptTasks=true,CanRespondToTasks=true};
        void Reset()
        {
            task.Reset();DisplayFeatureProfile.DshProfileName="default";reads.Clear();
            service.HistoryReader=(id,_,_)=> {reads.Add(id);return Task.FromResult(new DshHistoryPage([new(1,"user",id,null)],null,false));};
            service.Publish(new(true,"host","connected",[a,b,device],null) {Home=@"C:\FollowHome",DeviceSessionId=device.Id,Capabilities=caps});
        }
        DshTaskSnapshot Active(DshSessionSummary session)=>new(session.Id,session.Title,session.WorkingDirectory,"running","运行中","","",[],true,false);
        Reset();var vm=new DshSessionsPageViewModel();vm.Attach();
        check(vm.SelectedSession?.IsDeviceControlGroup==true,"without a monitored task attach retains device-control default");
        task.Publish(Active(a));
        check(vm.SelectedSession?.Session.Id==a.Id&&!vm.SelectedSession.IsDeviceControlGroup&&vm.IsDetailOpen,"new confirmed task id routes device view to exact ordinary task");
        check(vm.History.Single().Text==a.Id,"automatic task route reads ordinary task history rather than device aggregate");
        vm.OpenSession(vm.Sessions.First(row=>row.Session.Id==b.Id));
        task.Publish(Active(a) with {StatusText="新进展",FinalText="result"});
        await service.RefreshAsync(CancellationToken.None);
        check(vm.SelectedSession?.Session.Id==b.Id,"same-id task polling and ordinary catalog refresh do not steal user's other conversation");
        service.Publish(service.Current with {Sessions=[a,b,c,device]});task.Publish(Active(c));
        check(vm.SelectedSession?.Session.Id==b.Id,"new monitored task also preserves explicitly selected unrelated ordinary conversation");
        await vm.OpenTaskSessionCommand.ExecuteAsync(null);
        check(vm.SelectedSession?.Session.Id==c.Id,"explicit view still selects monitored task after automatic follow was suppressed");
        task.Publish(DshTaskSnapshot.Initial with {State="creating",IsBusy=true});
        task.Publish(Active(a));
        check(vm.SelectedSession?.Session.Id==a.Id,"new task replaces previous followed task after intermediate creating snapshot");
        service.Publish(service.Current with {DeviceSessionId="FOLLOW-DEVICE-2",Sessions=[a,b,c,device,device with {Id="FOLLOW-DEVICE-2"}]});
        check(vm.SelectedSession?.Session.Id==a.Id,"device membership refresh cannot switch active task view back to device control");
        vm.Detach();

        Reset();task.Publish(Active(a));vm=new();vm.Attach();
        check(vm.SelectedSession?.Session.Id==a.Id&&reads.All(id=>id==a.Id),"attach prefers existing monitored task and never briefly loads default device history");
        vm.Detach();vm.Attach();service.Publish(service.Current with {Sessions=[a,b,c,device]});task.Publish(Active(c));
        check(vm.SelectedSession?.Session.Id==c.Id,"reattaching selected monitored task preserves subsequent one-time task follow context");
        vm.OpenSession(vm.Sessions.First(row=>row.Session.Id==b.Id));vm.Detach();vm.Attach();
        check(vm.SelectedSession?.Session.Id==b.Id,"cached-page reattach preserves user's explicitly chosen unrelated ordinary conversation");
        vm.Detach();

        Reset();vm=new();vm.Attach();task.Publish(Active(c));
        check(vm.SelectedSession?.IsDeviceControlGroup==true&&!vm.Sessions.Any(row=>row.Session.Id==c.Id),"task event ahead of catalog waits for real row without inventing a session");
        service.Publish(service.Current with {Sessions=[a,b,c,device]});
        check(vm.SelectedSession?.Session.Id==c.Id&&vm.History.Single().Text==c.Id,"delayed ordinary row fulfills exactly one pending task route");
        vm.OpenSession(vm.Sessions.First(row=>row.Session.Id==b.Id));task.Publish(Active(c) with {StatusText="继续运行"});
        service.Publish(service.Current with {Sessions=[a,b,c with {UpdatedAt=DateTimeOffset.Now.AddMinutes(1)},device]});
        check(vm.SelectedSession?.Session.Id==b.Id,"completed route is never rearmed by same-id metadata updates");vm.Detach();

        Reset();vm=new();vm.Attach();task.Publish(Active(c));
        vm.OpenSession(vm.Sessions.First(row=>row.Session.Id==b.Id));
        service.Publish(service.Current with {Sessions=[a,b,c,device]});task.Publish(Active(c) with {StatusText="继续"});
        check(vm.SelectedSession?.Session.Id==b.Id,"manual selection during delayed catalog arrival cancels pending task route");vm.Detach();

        Reset();vm=new();vm.Attach();task.Publish(Active(c));
        vm.OpenSession(vm.SelectedSession!);
        service.Publish(service.Current with {Sessions=[a,b,c,device]});task.Publish(Active(c));
        check(vm.SelectedSession?.IsDeviceControlGroup==true,"even reclicking current device row explicitly cancels delayed automatic follow");vm.Detach();

        Reset();vm=new();vm.Attach();vm.SearchText="音箱控制";task.Publish(Active(c));
        service.Publish(service.Current with {Sessions=[a,b,c,device]});
        check(vm.SelectedSession?.Session.Id==c.Id&&vm.SearchText==string.Empty,"confirmed new task clears stale device filter only when real target row exists");vm.Detach();

        Reset();vm=new();vm.Attach();task.Publish(Active(c));
        service.Publish(service.Current with {Home=@"C:\OtherFollowHome"});task.Publish(DshTaskSnapshot.Initial);
        service.Publish(service.Current with {Sessions=[a,b,c,device]});
        check(vm.SelectedSession?.Session.Id!=c.Id,"scope change invalidates delayed task id before old row can arrive");vm.Detach();

        Reset();vm=new();vm.Attach();task.Publish(Active(c));
        DisplayFeatureProfile.DshProfileName="other";service.Publish(service.Current);task.Publish(DshTaskSnapshot.Initial);
        service.Publish(service.Current with {Sessions=[a,b,c,device]});
        check(vm.SelectedSession?.Session.Id!=c.Id,"profile change prevents old pending route from entering new profile");vm.Detach();

        Reset();vm=new();vm.Attach();task.Publish(Active(c));vm.Detach();
        service.Publish(service.Current with {Sessions=[a,b,c,device]});
        check(vm.SelectedSession?.IsDeviceControlGroup==true,"catalog arrival cannot navigate detached cached page");
        vm.Attach();check(vm.SelectedSession?.Session.Id==c.Id,"reattach consumes current confirmed task after detached catalog arrival");vm.Detach();

        Reset();task.Publish(Active(device));vm=new();vm.Attach();
        check(vm.SelectedSession?.IsDeviceControlGroup==true&&vm.Sessions.Count==3,"known device metadata never becomes ordinary task route or synthetic row");vm.Detach();
        task.Reset();
    }
}
