using System.Diagnostics;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services;

var passed = 0; var failed = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(6)); passed++; Console.WriteLine("PASS " + name); }
    catch(Exception error) { failed++; Console.WriteLine("FAIL " + name + " -> " + error.GetType().Name + ": " + error.Message); }
}
void Check(bool value, string message) => ProbeAssert.Check(value, message);
async Task Throws<T>(Func<Task> action) where T:Exception
{
    try { await action(); } catch(T) { return; } throw new Exception("Expected " + typeof(T).Name);
}
async Task Until(Func<bool> value)
{
    var timer=Stopwatch.StartNew(); while(!value()) { if(timer.ElapsedMilliseconds>2000) throw new TimeoutException("condition"); await Task.Delay(5); }
}
DshTaskInteraction Approval(string id="approve-1") => new(id,"approval","exec","write file",[]);
DshTaskInteraction Question(string id="question-1") => new(id,"question","ask","",[
    new("q1","位置","放在哪里？",[new("工作区","")]), new("q2","名称","文件名？",[])]);
DshTaskInteraction ChoiceQuestion(string id="choice-1") => new(id,"question","ask","",[
    new("style","样式","请选择样式",[new("浅色纸张 (Recommended)",""),new("深色夜间","")]),
    new("features","功能","请选择需要的功能",[new("搜索",""),new("置顶",""),new("颜色标签","")],true)]);
SemaphoreSlim ActionGate(DshTaskService service) => (SemaphoreSlim)typeof(DshTaskService)
    .GetField("actions",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(service)!;
void PublishTask(DshTaskService service,DshTaskSnapshot snapshot) => typeof(DshTaskService)
    .GetMethod("Publish",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(service,[snapshot]);

await Test("voice empty create immediately lists and monitors session, then submits only content", async()=>
{
    using var f=new TaskCase(); var prepared=await f.Service.RouteVoiceAsync("在 C:\\tasks 目录下新建任务，任务名称：校验");
    Check(prepared.Handled&&prepared.Message.Contains("会话已新建")&&f.Client.Creates.Single() is { BaseDirectory:"C:\\tasks",Title:"校验",Prompt:"" },"empty session not created");
    Check(f.Service.Current is { State:"awaitingPrompt",IsMonitoring:true,IsBusy:false }&&f.Client.Current.Sessions.Count==1&&f.Client.Current.VoiceTarget?.Id==f.Service.Current.SessionId,"new empty session not visible or selected");
    Check(f.Client.Prompts.Count==0,"blank creation posted fabricated task prompt");
    var started=await f.Service.RouteVoiceAsync("任务内容：读取 README 并总结");
    Check(started.Handled&&f.Client.Creates.Count==1&&f.Client.Prompts.Single().Prompt=="读取 README 并总结"&&f.Client.Prompts[0].Id==f.Service.Current.SessionId,"new session content wrong or created twice");
});

await Test("voice direct create extracts only explicit raw prompt", async()=>
{
    using var f=new TaskCase(); await f.Service.RouteVoiceAsync("创建任务：只读检查配置");
    Check(f.Client.Creates.Single().Prompt=="只读检查配置"&&f.Client.Prompts.Single().Prompt=="只读检查配置","wrapper leaked");
});

await Test("awaiting empty session scope change refuses old content", async()=>
{
    using var f=new TaskCase(); await f.Service.RouteVoiceAsync("新建任务");
    // Exercise the voice-route guard itself before the polling loop clears the old task.
    var gate=ActionGate(f.Service);await gate.WaitAsync();f.Scope="profile-B";
    DshTaskVoiceResult result;try{result=await f.Service.RouteVoiceAsync("任务内容：旧目录执行");}finally{gate.Release();}
    Check(result.Handled&&result.Message.Contains("配置已改变")&&f.Client.Creates.Count==1&&f.Client.Prompts.Count==0&&!f.Service.Current.IsMonitoring,"old session content submitted");
});

await Test("cancel empty creation releases monitoring and preserves session without cancelling a round", async()=>
{
    using var f=new TaskCase(); await f.Service.RouteVoiceAsync("新建任务"); await f.Service.RouteVoiceAsync("取消创建");
    Check(f.Client.Creates.Count==1&&f.Client.CancelCount==0&&f.Client.ReleaseCount==1&&!f.Service.Current.IsMonitoring&&f.Client.Current.Sessions.Count==1,"cancel empty creation deleted session or cancelled a round");
});

await Test("ordinary lights/volume/lyrics stay on device route with active task", async()=>
{
    using var f=new TaskCase(); await f.Start();
    foreach(var command in new[]{"关灯","音量调到13","切到 Spotify 歌词","氛围灯加快一点","设备状态"})
        Check(!(await f.Service.RouteVoiceAsync(command)).Handled,"task stole device command: "+command);
    Check(f.Client.Prompts.Count==1,"device text posted as task");
});

await Test("ordinary time/scene commands stay on device route", async()=>
{
    using var f=new TaskCase(); await f.Start();
    foreach(var command in new[]{"显示现在的时间","切换场景到自定义第一个","恢复默认场景"})
        Check(!(await f.Service.RouteVoiceAsync(command)).Handled,"task stole device command: "+command);
});

await Test("scene changes bypass a completed selected task across clock categories and polite traditional phrases", async()=>
{
    using var f=new TaskCase();await f.Start();var target=f.Service.Current.SessionId;
    f.Client.SetRemote("completed","scene-route-completed",[]);await Until(()=>f.Service.Current.State=="completed");
    foreach(var command in new[]{"换一个时钟场景","请换一个时钟场景","帮我换个时钟场景","请帮我换一个时钟场景。",
        "換一個時鐘場景","請幫我換個時鐘場景","换一个自定义场景","切换到自定义第二个场景","请换个动画场景","換個時鐘場景"})
    {
        Check(DshVoiceIntentRouter.Classify(command)==DshVoiceIntent.DeviceControl,"clear scene request was not admitted to the device route: "+command);
        var routed=await f.Service.RouteVoiceAsync(command);
        Check(!routed.Handled&&f.Client.Prompts.Count==1&&f.Client.Creates.Count==1&&f.Client.Responses.Count==0,"scene change reached completed task history: "+command);
        Check(f.Service.Current.SessionId==target&&f.Client.Current.VoiceTarget?.Id==target&&f.Client.CancelCount==0&&f.Client.ReleaseCount==0,"device detour changed the selected task: "+command);
    }
});

await Test("draw or change ASR ambiguity is clarified locally without a target or remote side effects", async()=>
{
    using var f=new TaskCase();
    foreach(var command in new[]{"画一个时钟场景","画个时钟场景。","请帮我画一个时钟场景","畫一個時鐘場景"})
    {
        Check(DshVoiceIntentRouter.Classify(command)==DshVoiceIntent.ClarifySceneIntent,"ambiguous ASR text silently chose a route: "+command);
        var clarification=await f.Service.RouteVoiceAsync(command);
        Check(clarification.Handled&&clarification.ListenForReply&&!string.IsNullOrWhiteSpace(clarification.Message),"ambiguous scene request did not ask for a spoken clarification: "+command);
        Check(f.Client.Creates.Count==0&&f.Client.Prompts.Count==0&&f.Client.Responses.Count==0&&f.Client.AdoptCount==0,"ambiguous text created or continued a remote session: "+command);
    }
});

await Test("draw or change ASR ambiguity cannot append to a selected ordinary conversation", async()=>
{
    using var f=new TaskCase();var target=new DshSessionSummary("memo-task","备忘录前端链路验收","C:/tasks/memo",DateTimeOffset.Now,"idle");
    f.Client.SelectVoiceTarget(target);
    var clarification=await f.Service.RouteVoiceAsync("画一个时钟场景。");
    Check(clarification.Handled&&clarification.ListenForReply&&f.Client.Prompts.Count==0&&f.Client.Creates.Count==0&&f.Client.AdoptCount==1,"ASR scene ambiguity entered the selected memo conversation instead of only reading its pending state");
    Check(f.Client.Current.VoiceTarget?.Id==target.Id,"clarification replaced the ordinary task target");
});

await Test("an empty task accepts scene drawing as content while keeping explicit scene changes on the device route", async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建任务");var target=f.Service.Current.SessionId;
    Check(!(await f.Service.RouteVoiceAsync("换一个时钟场景")).Handled&&f.Client.Prompts.Count==0&&f.Service.Current.State=="awaitingPrompt","device scene change consumed the pending task body");
    Check(DshVoiceIntentRouter.Classify("画一个时钟场景",awaitingTaskContent:true)==DshVoiceIntent.Task,"explicitly requested task content remained ambiguous");
    var submitted=await f.Service.RouteVoiceAsync("画一个时钟场景");
    Check(submitted.Handled&&f.Client.Prompts.Single()==(target,"画一个时钟场景")&&f.Client.Creates.Count==1,"drawing content did not fill the same new task");
});

await Test("explicit task-content marker keeps a scene-change body in the newly created task", async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建任务");var target=f.Service.Current.SessionId;
    var submitted=await f.Service.RouteVoiceAsync("任务内容，换一个时钟场景");
    Check(submitted.Handled&&f.Client.Prompts.Single()==(target,"换一个时钟场景")&&f.Client.Creates.Count==1,"explicit task content escaped to device control");
});

await Test("a creation draft asking for task content accepts bare scene drawing without another clarification", async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建一个绘画进行DSH的任务");
    Check(f.Client.Creates.Count==0&&f.Client.Prompts.Count==0,"test did not establish an unsubmitted creation draft");
    Check(!(await f.Service.RouteVoiceAsync("换一个时钟场景")).Handled&&f.Client.Creates.Count==0,"scene control filled or erased the creation draft");
    var submitted=await f.Service.RouteVoiceAsync("画一个时钟场景");
    Check(submitted.Handled&&f.Client.Creates.Single().Prompt=="画一个时钟场景"&&f.Client.Prompts.Single().Prompt=="画一个时钟场景","requested task content was treated as ambiguous or routed to the device");
});

await Test("explicit drawing and task content stay in the ordinary task rather than changing device scenes", async()=>
{
    using var f=new TaskCase();await f.Start();var target=f.Service.Current.SessionId;
    foreach(var command in new[]{"绘制一个时钟场景","任务内容，画一个时钟场景","任务内容：换一个时钟场景"})
    {
        Check(DshVoiceIntentRouter.Classify(command)==DshVoiceIntent.Task,"explicit task content became a device command or clarification: "+command);
        var count=f.Client.Prompts.Count;var result=await f.Service.RouteVoiceAsync(command);
        Check(result.Handled&&f.Client.Prompts.Count==count+1&&f.Client.Prompts.Last()==(target,command)&&f.Client.Creates.Count==1,"explicit task text changed route or body: "+command);
    }
});

await Test("negated conditional and instructional scene phrases do not become device actions", async()=>
{
    using var f=new TaskCase();await f.Start();var target=f.Service.Current.SessionId;
    foreach(var command in new[]{"不要换时钟场景","如果有时钟场景就换一个","怎么切换时钟场景","教我换一个时钟场景"})
    {
        Check(DshVoiceIntentRouter.Classify(command)==DshVoiceIntent.Task,"non-imperative scene text was admitted as a device action: "+command);
        var count=f.Client.Prompts.Count;var result=await f.Service.RouteVoiceAsync(command);
        Check(result.Handled&&f.Client.Prompts.Count==count+1&&f.Client.Prompts.Last()==(target,command),"scene discussion changed route or body: "+command);
    }
});

await Test("selected task is restored before deciding whether scene-like speech answers its pending question", async()=>
{
    using var f=new TaskCase();var target=new DshSessionSummary("task-1","绘图任务","C:/tasks/drawing",DateTimeOffset.Now,"idle");
    f.Client.SelectVoiceTarget(target);
    var question=new DshTaskInteraction("restored-scene-answer","question","ask","",[new("content","内容","请说出要绘制的内容",[])]);
    f.Client.SetRemote("waitingInput","restored-scene-question",[question]);
    Check(!f.Service.Current.IsMonitoring,"test unexpectedly restored the selected task before speech");
    var review=await f.Service.RouteVoiceAsync("画一个时钟场景");
    Check(review.Handled&&review.ListenForReply&&f.Client.AdoptCount==1&&f.Service.Current.NeedsAttention&&f.Service.Current.VoiceAnswers["content"]=="画一个时钟场景"
        &&f.Client.Prompts.Count==0&&f.Client.Responses.Count==0,"restored pending answer was intercepted as ambiguity or appended as a task prompt");
    await f.Service.RouteVoiceAsync("确认提交");
    Check(f.Client.Responses.Single().Answers!["content"]=="画一个时钟场景"&&f.Client.Prompts.Count==0,"restored answer could not be confirmed without submitting another task prompt");
});

await Test("pending free answers preserve bare draw and change scene words as answer data", async()=>
{
    foreach(var answer in new[]{"画一个时钟场景","换一个时钟场景","畫一個時鐘場景"})
    {
        using var f=new TaskCase();await f.Start();
        var question=new DshTaskInteraction("scene-answer","question","ask","",[new("content","内容","请说出要展示的内容",[])]);
        f.Client.SetRemote("waitingInput","scene-answer-pending",[question]);await Until(()=>f.Service.Current.NeedsAttention);
        Check(DshVoiceIntentRouter.Classify(answer,true)==DshVoiceIntent.InteractionReply,"pending answer was reinterpreted as a command: "+answer);
        var review=await f.Service.RouteVoiceAsync(answer);
        Check(review.Handled&&review.ListenForReply&&f.Service.Current.VoiceAnswers["content"]==answer&&f.Client.Responses.Count==0&&f.Client.Prompts.Count==1,"pending scene answer was not preserved for confirmation: "+answer);
        await f.Service.RouteVoiceAsync("确认提交");
        Check(f.Client.Responses.Single().Answers!["content"]==answer&&f.Client.Prompts.Count==1,"confirmed answer changed or also became task content: "+answer);
    }
});

await Test("explicit speaker scene control leaves a pending question and its existing answer intact", async()=>
{
    using var f=new TaskCase();await f.Start();
    var question=new DshTaskInteraction("scene-bypass","question","ask","",[new("content","内容","请说出要展示的内容",[])]);
    f.Client.SetRemote("waitingInput","scene-bypass-pending",[question]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("画一个时钟场景");var before=f.Service.Current;
    foreach(var command in new[]{"音箱控制，换一个时钟场景","音箱控制請換一個時鐘場景"})
    {
        Check(DshVoiceIntentRouter.Classify(command,true)==DshVoiceIntent.DeviceControl,"explicit speaker command lost priority during a question: "+command);
        Check(!(await f.Service.RouteVoiceAsync(command)).Handled&&f.Service.Current.NeedsAttention&&f.Service.Current.VoiceAnswers["content"]=="画一个时钟场景"
            &&f.Service.Current.VoiceAnswerRevision==before.VoiceAnswerRevision&&f.Client.Responses.Count==0&&f.Client.Prompts.Count==1,"speaker command consumed or replaced the pending answer: "+command);
    }
    await f.Service.RouteVoiceAsync("确认提交");
    Check(f.Client.Responses.Single().Answers!["content"]=="画一个时钟场景","device detour prevented confirming the original answer");
});

await Test("default scene aliases stay on device route instead of active task", async()=>
{
    using var f=new TaskCase(); await f.Start();
    foreach(var command in new[]{"切换为默认场景。","请回到默认场景","还原默认场景","默认场景","請恢復默認場景","切換為默認場景","切回默認場景"})
        Check(DshTaskVoiceParser.IsDeviceCommand(command)&&!(await f.Service.RouteVoiceAsync(command)).Handled,"task stole default scene alias: "+command);
    Check(f.Client.Prompts.Count==1,"default scene alias posted to task");
});

await Test("default scene discussion or negation does not become a device command", ()=>
{
    foreach(var command in new[]{"不需要恢复默认场景","编写恢复默认场景功能","分析默认场景代码","说明默认场景","如何回到默认场景"})
        Check(!DshTaskVoiceParser.IsDeviceCommand(command),"non-device text routed to device: "+command);
    return Task.CompletedTask;
});

await Test("approval requires exact pending ID and correct type", async()=>
{
    using var f=new TaskCase(); await f.Start(); f.Client.SetRemote("waitingApproval","a1",[Approval()]);
    await Until(()=>f.Service.Current.NeedsAttention);
    await Throws<InvalidOperationException>(()=>f.Service.RespondApprovalAsync("stale",true));
    await Throws<InvalidOperationException>(()=>f.Service.RespondQuestionAsync("approve-1",new Dictionary<string,string>()));
    Check(f.Client.Responses.Count==0,"invalid answer sent");
});

await Test("ordinary 好的 does not authorize or post while approval is pending", async()=>
{
    using var f=new TaskCase(); await f.Start(); f.Client.SetRemote("waitingApproval","a1",[Approval()]);
    await Until(()=>f.Service.Current.NeedsAttention); var response=await f.Service.RouteVoiceAsync("好的");
    Check(response.Handled&&f.Client.Responses.Count==0&&f.Client.Prompts.Count==1,"implicit approval");
});

await Test("exact approval and rejection use allowed-once/rejected", async()=>
{
    using var f=new TaskCase(); await f.Start(); f.Client.SetRemote("waitingApproval","a1",[Approval()]);
    await Until(()=>f.Service.Current.NeedsAttention); await f.Service.RouteVoiceAsync("批准本次。");
    Check(f.Client.Responses.Single().Outcome=="allowed-once","wrong approval");
    f.Client.SetRemote("waitingApproval","a2",[Approval("approve-2")]); await Until(()=>f.Service.Current.PendingInteractions.Any(p=>p.Id=="approve-2"));
    await f.Service.RouteVoiceAsync("拒绝本次"); Check(f.Client.Responses.Last().Outcome=="rejected","wrong rejection");
});

await Test("approval words with no live request are not task prompts", async()=>
{
    using var f=new TaskCase(); await f.Start(); var r=await f.Service.RouteVoiceAsync("批准本次");
    Check(r.Handled&&f.Client.Responses.Count==0&&f.Client.Prompts.Count==1,"stale authorization posted");
});

await Test("multi approval cannot be confirmed by one voice answer", async()=>
{
    using var f=new TaskCase(); await f.Start(); f.Client.SetRemote("waitingApproval","a1",[Approval("one"),Approval("two")]);
    await Until(()=>f.Service.Current.PendingInteractions.Count==2); var r=await f.Service.RouteVoiceAsync("批准本次");
    Check(r.Handled&&f.Client.Responses.Count==0,"multiple approved");
});

await Test("question submission needs complete precise answer keys", async()=>
{
    using var f=new TaskCase(); await f.Start(); f.Client.SetRemote("waitingInput","q1",[Question()]); await Until(()=>f.Service.Current.NeedsAttention);
    await Throws<ArgumentException>(()=>f.Service.RespondQuestionAsync("question-1",new Dictionary<string,string>{{"q1","one"}}));
    await Throws<ArgumentException>(()=>f.Service.RespondQuestionAsync("question-1",new Dictionary<string,string>{{"q1","one"},{"wrong","two"}}));
    Check(f.Client.Responses.Count==0,"partial response sent");
});

await Test("voice two-question flow sends once only after all answers and explicit confirmation", async()=>
{
    using var f=new TaskCase(); await f.Start(); f.Client.SetRemote("waitingInput","q1",[Question()]); await Until(()=>f.Service.Current.NeedsAttention);
    var first=await f.Service.RouteVoiceAsync("工作区"); Check(first.Message.Contains("文件名")&&f.Client.Responses.Count==0,"first answered all");
    var review=await f.Service.RouteVoiceAsync("notes.txt");
    Check(review.ListenForReply&&review.Message.Contains("确认提交")&&f.Client.Responses.Count==0,"all answers submitted without confirmation");
    var submitted=await f.Service.RouteVoiceAsync("确认提交");var answer=f.Client.Responses.Single();
    Check(!submitted.ListenForReply,"submitted answer opened an unrelated capture");
    Check(answer.Answers is { Count:2 }&&answer.Answers["q1"]=="工作区"&&answer.Answers["q2"]=="notes.txt","answers misbound");
});

await Test("new question interaction clears answers collected for old request", async()=>
{
    using var f=new TaskCase(); await f.Start(); f.Client.SetRemote("waitingInput","q1",[Question("old")]); await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("回答：old answer"); f.Client.SetRemote("waitingInput","q2",[Question("new")]);
    await Until(()=>f.Service.Current.PendingInteractions[0].Id=="new"); await f.Service.RouteVoiceAsync("回答：new first");
    Check(f.Client.Responses.Count==0,"stale first answer reused"); await f.Service.RouteVoiceAsync("new second");
    Check(f.Client.Responses.Count==0,"new question submitted without explicit confirmation");await f.Service.RouteVoiceAsync("确认提交");
    Check(f.Client.Responses.Single().Answers!["q1"]=="new first","old answer retained");
});

await Test("scope change invalidates pending approvals before sending", async()=>
{
    using var f=new TaskCase(); await f.Start(); f.Client.SetRemote("waitingApproval","a1",[Approval()]); await Until(()=>f.Service.Current.NeedsAttention);
    f.Scope="profile-B"; f.Client.Signal(); await Throws<InvalidOperationException>(()=>f.Service.RespondApprovalAsync("approve-1",true));
    Check(f.Client.Responses.Count==0&&!f.Service.Current.IsMonitoring,"cross-scope approval");
});

await Test("cancel current round keeps monitoring and does not release", async()=>
{
    using var f=new TaskCase(); await f.Start(); await f.Service.RouteVoiceAsync("停止当前任务");
    Check(f.Client.CancelCount==1&&f.Client.ReleaseCount==0&&f.Service.Current.IsMonitoring,"cancel was stop-monitoring");
});

await Test("stop monitoring releases ownership without cancelling real task", async()=>
{
    using var f=new TaskCase(); await f.Start(); await f.Service.RouteVoiceAsync("停止监控");
    Check(f.Client.CancelCount==0&&f.Client.ReleaseCount==1&&!f.Service.Current.IsMonitoring,"stop cancelled actual task");
});

await Test("task draft accepts common ASR content without punctuation", async()=>
{
    using var f=new TaskCase(); await f.Service.RouteVoiceAsync("新建任务"); await f.Service.RouteVoiceAsync("任务内容读取项目并总结");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Single().Prompt=="读取项目并总结","punctuation-free ASR cannot submit content");
});


await Test("service device commands still route while a task draft is pending",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建任务");
    Check(!(await f.Service.RouteVoiceAsync("关灯")).Handled,"task draft stole device command");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==0&&f.Service.Current.State=="awaitingPrompt","device prompt submitted task or lost waiting state");
});

await Test("no task target leaves ordinary text to original route",async()=>
{
    using var f=new TaskCase();Check(!(await f.Service.RouteVoiceAsync("查询设备状态")).Handled,"no-target task route stole text");
});

await Test("service archived/device sessions cannot be adopted as tasks",async()=>
{
    using var f=new TaskCase();
    foreach(var device in new[]{true,false})
    {
        var session=new DshSessionSummary("other","test","C:/test",DateTimeOffset.Now,"idle"){IsDeviceControl=device,IsArchived=!device};
        await Throws<ArgumentException>(()=>f.Service.MonitorAsync(session));
    }
    Check(!f.Service.Current.IsMonitoring,"invalid session monitored");
});

await Test("service old pending approval is cleared after disconnect",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingApproval","a1",[Approval()]);await Until(()=>f.Service.Current.NeedsAttention);
    f.Client.Disconnect();await Throws<InvalidOperationException>(()=>f.Service.RespondApprovalAsync("approve-1",true));
    Check(f.Client.Responses.Count==0,"disconnected pending approval sent");
});

await Test("question answers containing device domain words are still collected",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","q1",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("回答：歌词放在这里");await f.Service.RouteVoiceAsync("氛围灯配置.txt");await f.Service.RouteVoiceAsync("确认提交");
    var response=f.Client.Responses.Single();Check(response.Answers!["q1"]=="歌词放在这里"&&response.Answers["q2"]=="氛围灯配置.txt","domain word stole question answer");
});

await Test("explicit device bypass preserves pending question and collected answers",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","q1",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("回答：first answer");Check(!(await f.Service.RouteVoiceAsync("设备关灯")).Handled,"explicit device command swallowed");
    Check(f.Client.Responses.Count==0&&f.Service.Current.NeedsAttention,"device bypass completed pending");
    await f.Service.RouteVoiceAsync("second answer");await f.Service.RouteVoiceAsync("确认提交");Check(f.Client.Responses.Single().Answers!["q1"]=="first answer","bypass lost answers");
});

await Test("pending approval does not misroute device words without explicit prefix",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingApproval","a1",[Approval()]);await Until(()=>f.Service.Current.NeedsAttention);
    var answer=await f.Service.RouteVoiceAsync("歌词处理可以");Check(answer.Handled&&f.Client.Responses.Count==0,"domain answer became device command");
    Check(!(await f.Service.RouteVoiceAsync("音箱控制关闭歌词")).Handled,"explicit device prefix failed");
});

await Test("directory parser ignores quoted input file after task-content marker",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建任务，任务内容：读取\"C:\\inputs\\source.md\"并总结");
    Check(f.Client.Creates.Single().BaseDirectory=="C:/tasks","input file became task root");
});

await Test("directory parser uses root before marker and ignores second quoted input",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("在 C:\\tasks 目录下新建任务，任务指令：读取\"D:\\inputs\\source.md\"并总结");
    Check(f.Client.Creates.Single().BaseDirectory=="C:\\tasks","input path overrode declared task root");
});

await Test("unspecified spoken directory asks clarification without creating a session",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("在指定目录下新建任务，任务内容：读取项目");
    Check(result.Handled&&result.Message.Contains("未识别")&&f.Client.Creates.Count==0,"unspecified directory silently defaulted");
});

await Test("incomplete drive-only directory asks clarification without creating",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("在D盘新建任务，任务内容：读取项目");
    Check(result.Handled&&result.Message.Contains("未识别")&&f.Client.Creates.Count==0,"drive-only directory silently defaulted");
});

await Test("ambiguous path wording asks clarification without creating",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("在路径下新建任务，任务内容：读取项目");
    Check(result.Handled&&result.Message.Contains("未识别")&&f.Client.Creates.Count==0,"ambiguous path silently defaulted");
});

await Test("default task body containing input path does not trigger directory clarification",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建任务，任务内容：在\"C:\\inputs\\source.md\"中读取标题");
    Check(f.Client.Creates.Count==1&&f.Client.Creates[0].BaseDirectory=="C:/tasks","body path triggered root clarification");
});

await Test("explicit default directory with quoted input after marker uses configured root",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("使用默认目录新建任务，任务指令：在\"C:\\inputs\\source.md\"中读取标题");
    Check(f.Client.Creates.Single().BaseDirectory=="C:/tasks","explicit default changed to input path");
});

await Test("colon create fallback prompt does not make quoted input a task root",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("使用默认目录新建任务：读取\"C:\\inputs\\source.md\"并总结");
    Check(f.Client.Creates.Single().BaseDirectory=="C:/tasks","fallback prompt file became root");
});

await Test("default-directory words inside prompt do not override ambiguous root",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("在指定目录下新建任务，任务内容：总结默认目录规则");
    Check(result.Handled&&result.Message.Contains("未识别")&&f.Client.Creates.Count==0,"body default-directory wording bypassed clarification");
});


await Test("user numeric creation with filler creates an empty ordinary session",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("呃，新建1个DSH任务。");
    Check(result.Handled&&f.Client.Creates.Count==1&&f.Client.Prompts.Count==0&&f.Service.Current.State=="awaitingPrompt","numeric spoken create did not create empty session");
    await f.Service.RouteVoiceAsync("任务内容：读取 README 并总结");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Single().Prompt=="读取 README 并总结","numeric session not retained");
});

await Test("numeric create colon prompt executes only explicit content",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("新建1个DSH任务：读取 README 并总结");
    Check(result.Handled&&f.Client.Creates.Single().Prompt=="读取 README 并总结","numeric create prompt not extracted");
});

await Test("full-width count creation cannot fall through to device",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("新建１个DSH任务。");
    Check(result.Handled&&f.Client.Creates.Count==1&&f.Client.Prompts.Count==0,"full-width create not created");
});

await Test("user colloquial drawing DSH creation is handled without guessing content",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("新建一个绘画进行DSH的任务。");
    Check(result.Handled&&result.Message.Contains("尚未创建")&&f.Client.Creates.Count==0&&f.Client.Prompts.Count==0,"colloquial create leaked or invented prompt");
    await f.Service.RouteVoiceAsync("任务内容：编写一个只输出 SVG 的绘图脚本");
    Check(f.Client.Creates.Single().Prompt=="编写一个只输出 SVG 的绘图脚本","colloquial draft not retained");
});

await Test("unselected explicit DSH work is handled locally without device fallback",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("让DSH读取项目并总结");
    Check(result.Handled&&f.Client.Creates.Count==0&&f.Client.Prompts.Count==0,"unselected task leaked to device or started automatically");
});

await Test("device-selected explicit DSH work cannot use device persona",async()=>
{
    using var f=new TaskCase();f.Client.SelectVoiceTarget(new("device","音箱控制","",DateTimeOffset.Now,"idle"){IsDeviceControl=true});
    var result=await f.Service.RouteVoiceAsync("让DSH读取项目并总结");
    Check(result.Handled&&f.Client.Creates.Count==0&&f.Client.Prompts.Count==0&&!f.Service.Current.IsMonitoring,"device selected adopted for task or fallback");
});

await Test("unselected coding work asks for task session instead of using device persona",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("帮我编写一个绘图脚本");
    Check(result.Handled&&f.Client.Creates.Count==0&&f.Client.Prompts.Count==0,"coding work leaked to device");
});

await Test("archived selected session cannot receive general DSH work",async()=>
{
    using var f=new TaskCase();f.Client.SelectVoiceTarget(new("archived","old","C:/test",DateTimeOffset.Now,"idle"){IsArchived=true});
    var result=await f.Service.RouteVoiceAsync("DSH分析这个项目");
    Check(result.Handled&&f.Client.Creates.Count==0&&f.Client.Prompts.Count==0&&!f.Service.Current.IsMonitoring,"archived task leaked or adopted");
});

await Test("new empty session accepts natural body without mandatory content prefix",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建一个DSH任务");
    var result=await f.Service.RouteVoiceAsync("读取 README 并总结");
    Check(result.Handled&&f.Client.Creates.Count==1&&f.Client.Prompts.Single().Prompt=="读取 README 并总结","natural task body not submitted");
});

await Test("task draft management queries are not submitted as task content",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建任务");await f.Service.RouteVoiceAsync("查询任务状态");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==0,"management query submitted as prompt");
    await f.Service.RouteVoiceAsync("读取 README 并总结");Check(f.Client.Creates.Count==1&&f.Client.Prompts.Single().Prompt=="读取 README 并总结","status query destroyed waiting session");
});

await Test("task draft permits device query then resumes natural task content",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建任务");
    Check(!(await f.Service.RouteVoiceAsync("查询设备状态")).Handled,"draft swallowed status device query");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==0,"status query submitted task");
    await f.Service.RouteVoiceAsync("读取 README 并总结");Check(f.Client.Creates.Count==1,"device query destroyed task draft");
});

await Test("unknown text with no target asks route rather than using device persona",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("你觉得怎么样");
    Check(result.Handled&&f.Client.Creates.Count==0&&f.Client.Prompts.Count==0,"unknown text leaked to device");
});

await Test("ordinary selected task receives raw DSH continuation without creating another",async()=>
{
    using var f=new TaskCase();f.Client.SelectVoiceTarget(new("task-1","existing","C:/test",DateTimeOffset.Now,"idle"));
    var result=await f.Service.RouteVoiceAsync("DSH读取 README 并总结");
    Check(result.Handled&&f.Service.Current.IsMonitoring&&f.Client.Creates.Count==0&&f.Client.Prompts.Single().Prompt=="DSH读取 README 并总结","ordinary target continuation lost");
});

await Test("non-session new-file instruction does not become a create-session draft",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("新建文件");
    Check(result.Handled&&f.Client.Creates.Count==0,"new file created session");
    await f.Service.RouteVoiceAsync("任务内容：读取 README 并总结");Check(f.Client.Creates.Count==0&&f.Client.Prompts.Count==0,"new file silently made a task draft");
});

await Test("negated create-session request does not become a task draft",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("不要新建任务");
    Check(result.Handled&&f.Client.Creates.Count==0,"negation created task");
    await f.Service.RouteVoiceAsync("任务内容：读取 README 并总结");Check(f.Client.Creates.Count==0&&f.Client.Prompts.Count==0,"negation left create draft");
});

await Test("summary about how to create DSH tasks does not become creation",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("总结如何新建DSH任务");
    Check(result.Handled&&f.Client.Creates.Count==0,"how-to question created task");
    await f.Service.RouteVoiceAsync("任务内容：读取 README 并总结");Check(f.Client.Creates.Count==0&&f.Client.Prompts.Count==0,"how-to text left create draft");
});

await Test("active task treats new-file instructions as ordinary raw prompt",async()=>
{
    using var f=new TaskCase();await f.Start();await f.Service.RouteVoiceAsync("新建文件，文件名 notes.txt");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Last().Prompt=="新建文件，文件名 notes.txt","file action mistaken for task creation");
});

await Test("active task treats create-task how-to as ordinary raw prompt",async()=>
{
    using var f=new TaskCase();await f.Start();await f.Service.RouteVoiceAsync("总结如何新建DSH任务");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Last().Prompt=="总结如何新建DSH任务","how-to hijacked active task");
});

await Test("count grammar keeps explicit header directory and prompt path separate",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("在 C:\\tasks 目录下新建1个DSH任务：读取\"D:\\input.md\"并总结");
    var create=f.Client.Creates.Single();Check(create.BaseDirectory=="C:\\tasks"&&create.Prompt=="读取\"D:\\input.md\"并总结","numeric grammar inconsistent across header/prompt/path");
});

await Test("creation grammar supports DSH/de/session word arrangements",async()=>
{
    foreach(var text in new[]{"新建一个DSH的会话任务","创建1个DSH任务","呃，帮我创建一个DSH的任务","创建一个会话任务"})
    {
        using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync(text);
        Check(result.Handled&&f.Client.Creates.Count==1&&f.Client.Prompts.Count==0,"grammar failed to create empty session: "+text);
        await f.Service.RouteVoiceAsync("读取 README 并总结");Check(f.Client.Creates.Count==1,"grammar draft missing: "+text);
    }
});

await Test("draft negation cancels creation instead of becoming submitted content",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建任务");await f.Service.RouteVoiceAsync("不要新建任务");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==0&&!f.Service.Current.IsMonitoring,"creation negation submitted as content");
    await f.Service.RouteVoiceAsync("任务内容：读取 README 并总结");Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==0,"cancelled creation still live");
});

await Test("draft how-to task body is submitted as raw content, not another creation",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建任务");await f.Service.RouteVoiceAsync("总结如何新建DSH任务");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Single().Prompt=="总结如何新建DSH任务","task how-to body misclassified");
});

await Test("draft simple acknowledgments cannot create work accidentally",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建任务");
    foreach(var text in new[]{"好的","继续","批准本次","拒绝本次"})await f.Service.RouteVoiceAsync(text);
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==0&&f.Service.Current.State=="awaitingPrompt","acknowledgment submitted a task");
    await f.Service.RouteVoiceAsync("读取 README 并总结");Check(f.Client.Creates.Count==1,"acknowledgment destroyed draft");
});

await Test("active unfinished task rejects new create draft and keeps same task",async()=>
{
    using var f=new TaskCase();await f.Start();var result=await f.Service.RouteVoiceAsync("新建1个DSH任务");
    Check(result.Handled&&f.Client.Creates.Count==1,"second task created");
    await f.Service.RouteVoiceAsync("读取 README 并总结");Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==2,"new draft hijacked active task");
});

await Test("idle remote polling preserves empty session waiting for content",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");
    f.Client.SetRemote("idle","empty-poll",[],hasSubmittedPrompt:false);await Task.Delay(40);
    Check(f.Service.Current.State=="awaitingPrompt"&&f.Service.Current.StatusText.Contains("等待内容")&&f.Client.Prompts.Count==0,"idle poll erased task-content waiting state");
});

await Test("repeated creation while awaiting content cannot make a second session",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");var id=f.Service.Current.SessionId;
    var response=await f.Service.RouteVoiceAsync("再新建1个DSH任务");
    Check(response.Handled&&response.Message.Contains("不会重复创建")&&f.Client.Creates.Count==1&&f.Service.Current.SessionId==id&&f.Client.Prompts.Count==0,"repeat made another session or prompt");
});

await Test("repeated create with explicit body submits once to existing empty session",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");var id=f.Service.Current.SessionId;
    await f.Service.RouteVoiceAsync("新建DSH任务，任务内容：只回复路由通过");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Single()==(id,"只回复路由通过"),"repeated header created another task or leaked wrapper");
});

await Test("manual first message switches the empty monitored session to running",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");var id=f.Service.Current.SessionId;
    await f.Service.SendMessageAsync("只回复界面通过");
    Check(f.Service.Current.State=="running"&&f.Client.Prompts.Single()==(id,"只回复界面通过")&&f.Client.Creates.Count==1,"manual content did not use same empty session");
});

await Test("all acknowledgments and approval words stay unsent in empty session",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");
    foreach(var text in new[]{"好","好的","嗯","继续","同意","批准","允许本次","批准本次","同意本次","拒绝","不同意","拒绝本次","不允许"})
        Check((await f.Service.RouteVoiceAsync(text)).Handled,"empty session ignored response guard: "+text);
    Check(f.Client.Prompts.Count==0&&f.Client.Responses.Count==0&&f.Client.Creates.Count==1&&f.Service.Current.State=="awaitingPrompt","empty session submitted acknowledgment or stale authorization");
});

await Test("stopping empty session removes voice target and blocks stray first content",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");await f.Service.RouteVoiceAsync("停止任务监控");
    await f.Service.RouteVoiceAsync("读取 README 并总结");
    Check(!f.Service.Current.IsMonitoring&&f.Client.Current.VoiceTarget is null&&f.Client.Prompts.Count==0&&f.Client.ReleaseCount==1&&f.Client.Creates.Count==1,"stopped empty session still accepted stray content");
});

await Test("cancelling an empty task preserves the session without cancelling nonexistent round",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");var response=await f.Service.RouteVoiceAsync("取消当前任务");
    Check(response.Handled&&response.Message.Contains("尚未提交")&&!f.Service.Current.IsMonitoring&&f.Client.CancelCount==0&&f.Client.ReleaseCount==1&&f.Client.Current.Sessions.Count==1,"empty task cancellation sent nonexistent round cancel");
});

await Test("scope-change notification clears empty-session target before content submission",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");f.Scope="scope-B";f.Client.Signal();
    await f.Service.RouteVoiceAsync("任务内容：旧目录读取");
    Check(!f.Service.Current.IsMonitoring&&f.Client.Prompts.Count==0&&f.Client.Creates.Count==1,"changed scope accepted empty session first content");
});

await Test("persisted empty-session waiting state restores without replay or another create",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");var id=f.Service.Current.SessionId;f.Service.Dispose();
    using var restored=new DshTaskService(f.Client,()=>"C:/tasks",()=>f.Scope,stateRoot:f.StateRoot,pollInterval:TimeSpan.FromMilliseconds(10));
    f.Client.Signal();await Until(()=>restored.Current.IsMonitoring&&restored.Current.State=="awaitingPrompt");
    Check(restored.Current.SessionId==id&&f.Client.Creates.Count==1&&f.Client.Prompts.Count==0,"restored waiting task replayed or changed identity");
    await restored.RouteVoiceAsync("任务内容：只回复恢复通过");
    Check(f.Client.Prompts.Single()==(id,"只回复恢复通过")&&f.Client.Creates.Count==1,"restored content not sent to same session");
});

await Test("remote submitted prompt overrides persisted empty-session waiting flag",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");var id=f.Service.Current.SessionId;f.Service.Dispose();
    f.Client.SetRemote("running","external-submission",[],hasSubmittedPrompt:true);
    using var restored=new DshTaskService(f.Client,()=>"C:/tasks",()=>f.Scope,stateRoot:f.StateRoot,pollInterval:TimeSpan.FromMilliseconds(10));
    f.Client.Signal();await Until(()=>restored.Current.IsMonitoring&&restored.Current.State=="running");
    Check(restored.Current.SessionId==id&&f.Client.Prompts.Count==0&&f.Client.Creates.Count==1,"stale local waiting flag overrode external prompt or replayed");
});

await Test("low-confidence draft still waits without claiming or creating a session",async()=>
{
    using var f=new TaskCase();var result=await f.Service.RouteVoiceAsync("新建一个绘画进行DSH的任务");
    Check(result.Message.Contains("尚未创建")&&f.Client.Creates.Count==0&&!f.Service.Current.IsMonitoring,"uncertain intent announced a real task creation");
    await f.Service.RouteVoiceAsync("取消创建");await f.Service.RouteVoiceAsync("读取 README 并总结");
    Check(f.Client.Creates.Count==0&&f.Client.Prompts.Count==0,"cancelled low-confidence draft resumed");
});

await Test("first accepted empty-session message emits started feedback only once",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");
    Check(!f.Cues.Contains("task_started"),"empty creation pretended execution started");
    await f.Service.SendMessageAsync("只回复首轮通过");
    Check(f.Cues.Count(c=>c=="task_started")==1,"first message did not emit exactly one started cue");
    await f.Service.SendMessageAsync("只回复第二轮通过");
    Check(f.Cues.Count(c=>c=="task_started")==1&&f.Client.Prompts.Count==2,"continuation repeated initial task-start cue");
});

await Test("first-message transport failure retains same empty session without automatic resend",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");var id=f.Service.Current.SessionId;
    f.Client.SubmitFailure=new IOException("reply unavailable");
    await Throws<IOException>(()=>f.Service.RouteVoiceAsync("任务内容：只回复失败检查"));
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==1&&f.Service.Current.SessionId==id&&!f.Cues.Contains("task_started"),"failure recreated session or announced a confirmed start");
    await f.Service.RouteVoiceAsync("好的");
    Check(f.Client.Prompts.Count==1,"acknowledgment automatically resent uncertain first message");
    f.Client.SubmitFailure=null;await f.Service.RouteVoiceAsync("新建DSH任务，任务内容：只回复重试检查");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==2&&f.Client.Prompts.Last().Id==id,"explicit follow-up created another session");
});

await Test("unaccepted first message cannot mark empty session as submitted or started",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");f.Client.AcceptSubmit=false;
    await Throws<InvalidDataException>(()=>f.Service.SendMessageAsync("只回复未接受检查"));
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==1&&!f.Cues.Contains("task_started"),"unaccepted first message announced success");
    await f.Service.RouteVoiceAsync("批准本次");Check(f.Client.Prompts.Count==1&&f.Client.Responses.Count==0,"unaccepted prompt lost awaiting guard");
});

await Test("creation failure never publishes a successful empty session",async()=>
{
    using var f=new TaskCase();f.Client.CreateFailure=new IOException("create unavailable");
    await Throws<IOException>(()=>f.Service.RouteVoiceAsync("新建DSH任务"));
    Check(f.Service.Current.SessionId==""&&!f.Service.Current.IsMonitoring&&f.Service.Current.State=="failed"&&f.Client.Creates.Count==0&&f.Client.Prompts.Count==0,"failed creation published success or prompt");
});

await Test("remote pending interaction overrides empty waiting guard even before history catches up",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");
    f.Client.SetRemote("idle","pending-without-history",[Approval()],hasSubmittedPrompt:false);await Until(()=>f.Service.Current.NeedsAttention);
    Check(f.Service.Current.State=="waitingApproval","pending approval hidden by empty waiting state");
    await f.Service.RouteVoiceAsync("批准本次");Check(f.Client.Responses.Single().Outcome=="allowed-once"&&f.Client.Prompts.Count==0,"pending approval mistaken for first task prompt");
});

await Test("reconnect to same pending approval does not repeat attention cue",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingApproval","same-request",[Approval()]);
    await Until(()=>f.Service.Current.State=="waitingApproval"&&f.Cues.Count(c=>c=="approval_required")==1);
    f.Client.ReadFailure=new IOException("temporary disconnect");await Until(()=>f.Service.Current.State=="disconnected");
    f.Client.ReadFailure=null;await Until(()=>f.Service.Current.State=="waitingApproval");await Task.Delay(30);
    Check(f.Cues.Count(c=>c=="approval_required")==1&&f.Client.Responses.Count==0,"same pending request replayed attention cue or authorization");
});

await Test("reconnect to same completed revision does not repeat completion cue",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("completed","same-completion",[]);
    await Until(()=>f.Service.Current.State=="completed"&&f.Cues.Count(c=>c=="task_completed")==1);
    f.Client.ReadFailure=new IOException("temporary disconnect");await Until(()=>f.Service.Current.State=="disconnected");
    f.Client.ReadFailure=null;await Until(()=>f.Service.Current.State=="completed");await Task.Delay(30);
    Check(f.Cues.Count(c=>c=="task_completed")==1,"same completed turn replayed completion cue");
});

await Test("partial question answers keep the next unanswered question after reconnect",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","same-questions",[Question()]);
    await Until(()=>f.Service.Current.State=="waitingInput");await f.Service.RouteVoiceAsync("工作区");
    Check(f.Service.Current.Detail.Contains("文件名？"),"partial answer did not show next question");
    f.Client.ReadFailure=new IOException("temporary disconnect");await Until(()=>f.Service.Current.State=="disconnected");
    f.Client.ReadFailure=null;await Until(()=>f.Service.Current.State=="waitingInput");
    Check(f.Service.Current.Detail.Contains("文件名？"),"reconnect displayed already answered question while binding next answer elsewhere");
    await f.Service.RouteVoiceAsync("notes.txt");await f.Service.RouteVoiceAsync("确认提交");Check(f.Client.Responses.Single().Answers!["q1"]=="工作区","reconnect discarded collected answer");
});

await Test("saved monitoring restores when session list arrives after first connection event",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");var saved=f.Client.Current.Sessions.Single();f.Service.Dispose();
    f.Client.SetSessions([],signal:false);
    using var restored=new DshTaskService(f.Client,()=>"C:/tasks",()=>f.Scope,stateRoot:f.StateRoot,pollInterval:TimeSpan.FromMilliseconds(10));
    f.Client.Signal();await Task.Delay(50);f.Client.SetSessions([saved]);
    await Until(()=>restored.Current.IsMonitoring&&restored.Current.SessionId==saved.Id);
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==0,"late restore created or replayed task instead of adopting saved session");
});

await Test("unaccepted directly created task prompt cannot announce confirmed start",async()=>
{
    using var f=new TaskCase();f.Client.AcceptSubmit=false;var result=await f.Start();
    Check(result.State=="unknown"&&!f.Cues.Contains("task_started")&&f.Client.Prompts.Count==1,"direct initial unaccepted submission falsely announced task started");
});

await Test("background monitoring rediscovers and adopts same task without replaying work",async()=>
{
    using var f=new TaskCase();await f.Start();var id=f.Service.Current.SessionId;
    f.Client.Disconnect();await Until(()=>f.Service.Current.State=="disconnected");
    f.Client.RequireAdoption=true;f.Client.ServerAvailable=true;
    await Until(()=>f.Service.Current.State=="running"&&f.Client.AdoptCount>0);
    Check(f.Service.Current.SessionId==id&&f.Client.Creates.Count==1&&f.Client.Prompts.Count==1&&f.Client.ConnectCount==0,"background recovery started a host, created another task or replayed prompt");
});

await Test("unavailable background host retries read-only discovery with backoff",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.Disconnect();await Until(()=>f.Service.Current.State=="disconnected");
    await Task.Delay(200);
    Check(f.Client.RefreshCount is >0 and <=4&&f.Client.ConnectCount==0&&f.Client.Prompts.Count==1,"discovery loop did not back off or launched/submitted work");
});

await Test("monitor notices scope change even without a session-client notification",async()=>
{
    using var f=new TaskCase();await f.Start();f.Scope="scope-B";
    await Until(()=>!f.Service.Current.IsMonitoring);
    Check(f.Service.Current.SessionId==""&&f.Client.Current.VoiceTarget is null&&f.Client.Prompts.Count==1,"silent settings change retained old task identity or continued work");
});

await Test("scope change while offline stops old monitoring without rediscovering another profile",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.Disconnect();await Until(()=>f.Service.Current.State=="disconnected");
    f.Scope="scope-B";await Until(()=>!f.Service.Current.IsMonitoring);
    Check(f.Service.Current.SessionId==""&&f.Client.Current.VoiceTarget is null&&f.Client.Prompts.Count==1,"offline monitor retained old-scope task");
});

await Test("a new approval after reconnect still emits its own attention cue",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingApproval","first-request",[Approval("first")]);
    await Until(()=>f.Cues.Count(c=>c=="approval_required")==1);
    f.Client.ReadFailure=new IOException("temporary disconnect");await Until(()=>f.Service.Current.State=="disconnected");
    f.Client.SetRemote("waitingApproval","second-request",[Approval("second")]);f.Client.ReadFailure=null;
    await Until(()=>f.Cues.Count(c=>c=="approval_required")==2);
    Check(f.Service.Current.PendingInteractions.Single().Id=="second","new pending authorization was suppressed after recovery");
});

await Test("another completed turn in same session still emits completion cue",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("completed","first-turn",[]);
    await Until(()=>f.Cues.Count(c=>c=="task_completed")==1);
    await f.Service.SendMessageAsync("下一轮只回复通过");f.Client.SetRemote("completed","second-turn",[]);
    await Until(()=>f.Cues.Count(c=>c=="task_completed")==2);
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==2,"new turn created another session or replayed messages");
});

await Test("failed continuation is marked unknown without replay or new start cue",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("completed","old-completed",[]);await Until(()=>f.Service.Current.State=="completed");
    f.Client.SubmitFailure=new IOException("continuation reply unavailable");
    // The monitor now wakes immediately after uncertain submissions. Hold its
    // verification read so this assertion observes the uncertainty publication.
    var verification=new TaskCompletionSource<DshTaskRemoteState>(TaskCreationOptions.RunContinuationsAsynchronously);
    f.Client.ReadOverride=(_,ct)=>verification.Task.WaitAsync(ct);
    await Throws<IOException>(()=>f.Service.SendMessageAsync("只回复第二轮"));
    Check(f.Service.Current.State=="unknown"&&f.Service.Current.StatusText=="消息提交结果未确认"&&f.Client.Creates.Count==1&&f.Client.Prompts.Count==2&&f.Cues.Count(c=>c=="task_started")==1,"continuation failure kept stale completion or replayed prompt/start cue");
});

await Test("queued automatic restore cannot replace a task created by a user action",async()=>
{
    using var f=new TaskCase();await f.Service.RouteVoiceAsync("新建DSH任务");f.Service.Dispose();
    using var restored=new DshTaskService(f.Client,()=>"C:/tasks",()=>f.Scope,stateRoot:f.StateRoot,pollInterval:TimeSpan.FromMilliseconds(10));
    var field=typeof(DshTaskService).GetField("actions",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
    var gate=(SemaphoreSlim)field.GetValue(restored)!;await gate.WaitAsync();
    var start=restored.StartAsync(new("C:/test","user new task","new raw prompt"));
    f.Client.Signal();await Task.Delay(30);gate.Release();var created=await start;await Task.Delay(50);
    Check(restored.Current.SessionId==created.SessionId&&created.SessionId=="task-2"&&f.Client.Prompts.Single().Prompt=="new raw prompt","stale restore hijacked newly created task");
});

await Test("release failure still stops locally clears target and prevents automatic restoration",async()=>
{
    using var f=new TaskCase();await f.Start();await Until(()=>Directory.GetFiles(f.StateRoot,"*.json").Length==1);
    f.Client.ReleaseFailure=new IOException("release response lost");var response=await f.Service.RouteVoiceAsync("停止监控");
    Check(!f.Service.Current.IsMonitoring&&f.Client.Current.VoiceTarget is null&&f.Service.Current.PendingInteractions.Count==0,"release failure retained local monitor or target");
    Check(Directory.GetFiles(f.StateRoot,"*.json").Length==0&&response.Message.Contains("远端交互释放未确认"),"release failure retained restore identity or hid uncertainty");
    var reads=f.Client.ReadCount;await Task.Delay(40);
    Check(f.Client.ReadCount==reads&&f.Client.CancelCount==0&&f.Client.Creates.Count==1&&f.Client.Prompts.Count==1,"stopped loop kept polling or cancelled/replayed work");
    using var restored=new DshTaskService(f.Client,()=>"C:/tasks",()=>f.Scope,stateRoot:f.StateRoot,pollInterval:TimeSpan.FromMilliseconds(10));
    f.Client.Signal();await Task.Delay(30);Check(!restored.Current.IsMonitoring,"failed release resurrected stopped monitoring");
    f.Client.ReleaseFailure=null;await f.Start();Check(f.Client.Creates.Count==2,"stale running state blocked explicitly requested new task");
});

await Test("replacement release failure leaves old monitoring reliably stopped without creating work",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("completed","old-completed",[]);await Until(()=>f.Service.Current.State=="completed");
    f.Client.ReleaseFailure=new IOException("release failed");await Throws<InvalidOperationException>(()=>f.Start());
    Check(!f.Service.Current.IsMonitoring&&f.Client.Current.VoiceTarget is null&&Directory.GetFiles(f.StateRoot,"*.json").Length==0,"replacement failure left a dead monitor flagged active");
    Check(f.Client.Creates.Count==1&&f.Client.Prompts.Count==1&&f.Client.CancelCount==0&&f.Service.Current.Detail.Contains("未确认"),"replacement failure created work or hid remote uncertainty");
});

await Test("offline stop clears local identity and reports unconfirmed remote release",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.Disconnect();await Until(()=>f.Service.Current.State=="disconnected");
    await f.Service.StopMonitoringAsync();
    Check(!f.Service.Current.IsMonitoring&&f.Client.Current.VoiceTarget is null&&Directory.GetFiles(f.StateRoot,"*.json").Length==0,"offline stop kept monitoring identity");
    Check(f.Client.ReleaseCount==0&&f.Client.CancelCount==0&&f.Service.Current.Detail.Contains("远端交互释放未确认"),"offline stop attempted unsupported release/cancel or hid uncertainty");
});

await Test("cancelled release still completes local stop and cannot restart saved monitoring",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.ReleaseFailure=new OperationCanceledException("release timed out");
    await f.Service.StopMonitoringAsync();
    Check(!f.Service.Current.IsMonitoring&&f.Client.Current.VoiceTarget is null&&Directory.GetFiles(f.StateRoot,"*.json").Length==0&&f.Client.CancelCount==0,"cancelled release retained local monitoring or cancelled real task");
});

await Test("adopting selected task waits for actual questions before routing first spoken answer",async()=>
{
    using var f=new TaskCase();var selected=new DshSessionSummary("existing","existing task","C:/tasks/existing",DateTimeOffset.Now,"idle");
    f.Client.SetSessions([selected]);f.Client.SelectVoiceTarget(selected);f.Client.SetRemote("waitingInput","existing-question",[Question()]);
    var opening=new TaskCompletionSource<DshTaskRemoteState>(TaskCreationOptions.RunContinuationsAsynchronously);
    f.Client.ReadOverride=(_,ct)=>opening.Task.WaitAsync(ct);var routed=f.Service.RouteVoiceAsync("工作区");await Until(()=>f.Client.ReadCount==1);
    Check(!routed.IsCompleted&&f.Client.Prompts.Count==0&&f.Client.Responses.Count==0,"first utterance was submitted before task state arrived");
    opening.SetResult(f.Client.Remote);await routed;f.Client.ReadOverride=null;
    Check(f.Client.Prompts.Count==0&&f.Client.Responses.Count==0&&f.Service.Current.Detail.Contains("文件名？"),"first answer was queued as ordinary task content");
    await f.Service.RouteVoiceAsync("notes.txt");await f.Service.RouteVoiceAsync("确认提交");var answer=f.Client.Responses.Single();
    Check(answer.Id==selected.Id&&answer.Answers!["q1"]=="工作区"&&answer.Answers["q2"]=="notes.txt"&&f.Client.Creates.Count==0,"first adopted question answer lost its identity or created another session");
});

await Test("failed initial task-state read cannot submit spoken content and is safe to retry",async()=>
{
    using var f=new TaskCase();var selected=new DshSessionSummary("existing","existing task","C:/tasks/existing",DateTimeOffset.Now,"idle");
    f.Client.SetSessions([selected]);f.Client.SelectVoiceTarget(selected);f.Client.ReadFailure=new IOException("initial state unavailable");
    await Throws<IOException>(()=>f.Service.RouteVoiceAsync("读取 README"));
    Check(f.Client.Prompts.Count==0&&f.Client.Responses.Count==0&&f.Client.Creates.Count==0&&!f.Service.Current.IsMonitoring,"unverified task accepted speech or started an empty monitor");
    f.Client.ReadFailure=null;await f.Service.RouteVoiceAsync("读取 README");
    Check(f.Client.Prompts.Single().Id==selected.Id&&f.Client.Creates.Count==0,"explicit retry lost selected existing target");
});

await Test("unknown adopted state refuses speech until a verifiable state is available",async()=>
{
    using var f=new TaskCase();var selected=new DshSessionSummary("existing","existing task","C:/tasks/existing",DateTimeOffset.Now,"idle");
    f.Client.SetSessions([selected]);f.Client.SelectVoiceTarget(selected);f.Client.SetRemote("unsupported","unknown",[]);
    var result=await f.Service.RouteVoiceAsync("读取 README");
    Check(result.Message.Contains("未发送")&&f.Client.Prompts.Count==0&&f.Client.Responses.Count==0&&f.Client.Creates.Count==0,"unknown initial status submitted a new prompt");
});

await Test("disconnect and unchanged running recovery both refresh display silently",async()=>
{
    using var f=new TaskCase();await f.Start();await Until(()=>f.Client.ReadCount>0);var before=f.Feedback.Count;
    f.Client.ReadFailure=new IOException("temporary disconnect");await Until(()=>f.Feedback.Any(x=>x.Snapshot.State=="disconnected"));
    Check(f.Feedback.Last().Cue=="","disconnect replayed a sound cue");
    f.Client.ReadFailure=null;await Until(()=>f.Feedback.Skip(before).Any(x=>x.Snapshot.State=="running"&&x.Cue==""));
    Check(f.Cues.Count(c=>c=="task_started")==1&&f.Cues.Count(c=>c=="task_progress")==0,"unchanged running recovery repeated an audio cue");
});

await Test("same completion recovery refreshes display without repeating completion cue",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("completed","same-completion",[]);await Until(()=>f.Cues.Count(c=>c=="task_completed")==1);
    f.Client.ReadFailure=new IOException("temporary disconnect");await Until(()=>f.Feedback.Any(x=>x.Snapshot.State=="disconnected"));var afterDisconnect=f.Feedback.Count;
    f.Client.ReadFailure=null;await Until(()=>f.Feedback.Skip(afterDisconnect).Any(x=>x.Snapshot.State=="completed"&&x.Cue==""));
    Check(f.Cues.Count(c=>c=="task_completed")==1,"same completion recovery replayed terminal sound");
});

await Test("queued legacy approval refuses same-id request whose contents changed",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingApproval","original",[Approval()]);await Until(()=>f.Service.Current.NeedsAttention);
    var gate=ActionGate(f.Service);await gate.WaitAsync();var answer=f.Service.RespondApprovalAsync("approve-1",true);
    PublishTask(f.Service,f.Service.Current with{PendingInteractions=[Approval() with{Reason="different operation"}]});gate.Release();
    await Throws<InvalidOperationException>(()=>answer);Check(f.Client.Responses.Count==0,"queued id-only approval applied to changed request contents");
});

await Test("queued expected response cannot cross to another task with identical interaction",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingApproval","original",[Approval()]);await Until(()=>f.Service.Current.NeedsAttention);
    var original=f.Service.Current.PendingInteractions.Single();var gate=ActionGate(f.Service);await gate.WaitAsync();
    var answer=f.Service.RespondApprovalAsync(original,true);PublishTask(f.Service,f.Service.Current with{SessionId="other-task"});gate.Release();
    await Throws<InvalidOperationException>(()=>answer);Check(f.Client.Responses.Count==0,"queued answer crossed into a different task session");
});

await Test("same-id revised question discards previously collected voice answers",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","old-question",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("回答：old location");var changed=Question() with{Questions=[new("q1","目录","新的输出目录？",[]),new("q2","名称","新文件名？",[])]};
    f.Client.SetRemote("waitingInput","changed-question",[changed]);await Until(()=>f.Service.Current.Detail.Contains("新的输出目录？"));
    await f.Service.RouteVoiceAsync("new location");Check(f.Client.Responses.Count==0&&f.Service.Current.Detail.Contains("新文件名？"),"revised same-id question reused the old first answer");
    await f.Service.RouteVoiceAsync("new-name.txt");await f.Service.RouteVoiceAsync("确认提交");Check(f.Client.Responses.Single().Answers!["q1"]=="new location","revised question retained stale answer content");
});

await Test("queued question copies original option schema before mutable lists change",async()=>
{
    using var f=new TaskCase();await f.Start();var options=new List<DshTaskOption>{new("工作区","原选项")};
    var question=Question() with{Questions=[new("q1","位置","放在哪里？",options),new("q2","名称","文件名？",[])]};
    f.Client.SetRemote("waitingInput","question",[question]);await Until(()=>f.Service.Current.NeedsAttention);
    var gate=ActionGate(f.Service);await gate.WaitAsync();
    var answer=f.Service.RespondQuestionAsync("question-1",new Dictionary<string,string>{{"q1","工作区"},{"q2","notes.txt"}});
    options[0]=new("另一个位置","新选项");gate.Release();
    await Throws<InvalidOperationException>(()=>answer);Check(f.Client.Responses.Count==0,"queued question followed mutated original schema references");
});

await Test("queued question preserves answer values captured when submission was requested",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","question",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
    var answers=new Dictionary<string,string>{{"q1","工作区"},{"q2","notes.txt"}};var gate=ActionGate(f.Service);await gate.WaitAsync();
    var reply=f.Service.RespondQuestionAsync(f.Service.Current.PendingInteractions.Single(),answers);
    answers["q2"]="changed-later.txt";gate.Release();await reply;
    Check(f.Client.Responses.Single().Answers!["q2"]=="notes.txt","queued question sent subsequently edited draft content");
});

await Test("numbered single and multiple choices are announced and submitted as exact labels",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","choices",[ChoiceQuestion()]);await Until(()=>f.Service.Current.NeedsAttention);
    var prompt=f.Service.Current.Detail;
    Check(prompt.Contains("1：浅色纸张")&&prompt.Contains("2：深色夜间"),"initial prompt omitted numbered option labels");
    var next=await f.Service.RouteVoiceAsync("第一项");
    Check(next.ListenForReply&&next.Message.Contains("可多选")&&next.Message.Contains("3：颜色标签"),"next question omitted multiple-choice guidance");
    var review=await f.Service.RouteVoiceAsync("第一项和第三项");
    Check(review.ListenForReply&&review.Message.Contains("搜索 | 颜色标签")&&f.Client.Responses.Count==0,"choice review did not preserve a pending explicit confirmation");
    await f.Service.RouteVoiceAsync("确认提交");var answer=f.Client.Responses.Single().Answers!;
    Check(answer["style"]=="浅色纸张 (Recommended)"&&answer["features"]=="搜索 | 颜色标签","spoken indexes were sent instead of real option labels");
});

await Test("ambiguous out-of-range and negated choices never become implicit free text",async()=>
{
    using var f=new TaskCase();await f.Start();
    var question=ChoiceQuestion() with{Questions=[new("theme","样式","请选择样式",[new("浅色纸张",""),new("浅色天空",""),new("深色夜间","")])]};
    f.Client.SetRemote("waitingInput","ambiguous",[question]);await Until(()=>f.Service.Current.NeedsAttention);
    foreach(var text in new[]{"浅色","第四项","第一和第二项","不要第一项","随便","未知样式"})
    {
        var result=await f.Service.RouteVoiceAsync(text);
        Check(result.ListenForReply&&f.Client.Responses.Count==0&&f.Client.Prompts.Count==1,"uncertain choice mutated the real task: "+text);
        var confirm=await f.Service.RouteVoiceAsync("确认提交");
        Check(confirm.ListenForReply&&confirm.Message.Contains("没有回答")&&f.Client.Responses.Count==0,"uncertain choice became an accepted answer: "+text);
    }
    await f.Service.RouteVoiceAsync("回答：薄荷绿");await f.Service.RouteVoiceAsync("确认提交");
    Check(f.Client.Responses.Single().Answers!["theme"]=="薄荷绿","explicit custom choice lost its body or sent the prefix");
});

await Test("repeating options does not advance or overwrite collected answers",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","repeat",[ChoiceQuestion()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("第二项");
    foreach(var text in new[]{"重听","重复问题","读出选项","检查答案"})
    {
        var repeated=await f.Service.RouteVoiceAsync(text);
        Check(repeated.ListenForReply&&f.Client.Responses.Count==0&&f.Client.Prompts.Count==1,"repeat or review sent content: "+text);
    }
    await f.Service.RouteVoiceAsync("全选");await f.Service.RouteVoiceAsync("确认提交");
    var answers=f.Client.Responses.Single().Answers!;
    Check(answers["style"]=="深色夜间"&&answers["features"]=="搜索 | 置顶 | 颜色标签","repeating changed selected style or consumed the next answer");
});

await Test("previous question can be revised without discarding earlier answers",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","revise",[ChoiceQuestion()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("第一项");await f.Service.RouteVoiceAsync("第一和第三项");
    var revise=await f.Service.RouteVoiceAsync("修改上一题");
    Check(revise.ListenForReply&&revise.Message.Contains("请选择需要的功能")&&f.Client.Responses.Count==0,"review could not reopen last answered question");
    await f.Service.RouteVoiceAsync("第二项");await f.Service.RouteVoiceAsync("确认提交");
    var answers=f.Client.Responses.Single().Answers!;
    Check(answers["style"]=="浅色纸张 (Recommended)"&&answers["features"]=="置顶","last answer edit discarded earlier choice or preserved stale selection");
});

await Test("previous question navigation can revise the first answer before answering the second",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","back",[ChoiceQuestion()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("第一项");var previous=await f.Service.RouteVoiceAsync("上一题");
    Check(previous.ListenForReply&&previous.Message.Contains("请选择样式"),"previous question did not return to first choice");
    await f.Service.RouteVoiceAsync("第二项");await f.Service.RouteVoiceAsync("第一项");await f.Service.RouteVoiceAsync("确认提交");
    Check(f.Client.Responses.Single().Answers!["style"]=="深色夜间","first choice revision did not replace original answer");
});

await Test("confirmation and skip refuse incomplete question sets",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","incomplete",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
    foreach(var text in new[]{"确认提交","跳过此题","下一题"})
        Check((await f.Service.RouteVoiceAsync(text)).ListenForReply&&f.Client.Responses.Count==0,"incomplete question was skipped or submitted");
    await f.Service.RouteVoiceAsync("工作区");var confirm=await f.Service.RouteVoiceAsync("提交答案");
    Check(confirm.ListenForReply&&confirm.Message.Contains("文件名")&&f.Client.Responses.Count==0,"partial set accepted confirmation");
    await f.Service.RouteVoiceAsync("notes.txt");await f.Service.RouteVoiceAsync("确认答案");
    Check(f.Client.Responses.Count==1,"completed question set was not submitted exactly once");
});

await Test("clearing answers preserves task and requires a new complete confirmation",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","clear",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("工作区");await f.Service.RouteVoiceAsync("old.txt");
    var cleared=await f.Service.RouteVoiceAsync("取消回答");
    Check(cleared.ListenForReply&&f.Client.CancelCount==0&&f.Client.ReleaseCount==0&&f.Service.Current.IsMonitoring,"answer cancellation cancelled or released real task");
    await f.Service.RouteVoiceAsync("确认提交");Check(f.Client.Responses.Count==0,"cleared answer was still submitted");
    await f.Service.RouteVoiceAsync("工作区");await f.Service.RouteVoiceAsync("new.txt");await f.Service.RouteVoiceAsync("确认提交");
    Check(f.Client.Responses.Single().Answers!["q2"]=="new.txt","cleared draft recovered old answer");
});

await Test("confirmation after same-id schema change cannot submit the old completed draft",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","before-schema",[ChoiceQuestion()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("第一项");await f.Service.RouteVoiceAsync("全选");
    var changed=ChoiceQuestion() with{Questions=[new("style","新样式","请选择新样式",[new("绿色",""),new("蓝色","")]),new("features","新功能","请选择新功能",[new("导出","")])]};
    f.Client.SetRemote("waitingInput","after-schema",[changed]);await Until(()=>f.Service.Current.Detail.Contains("请选择新样式"));
    var confirm=await f.Service.RouteVoiceAsync("确认提交");
    Check(confirm.ListenForReply&&f.Client.Responses.Count==0,"same-id schema change reused previously completed voice draft");
    await f.Service.RouteVoiceAsync("第二项");await f.Service.RouteVoiceAsync("第一项");await f.Service.RouteVoiceAsync("确认提交");
    var answers=f.Client.Responses.Single().Answers!;
    Check(answers["style"]=="蓝色"&&answers["features"]=="导出","new schema answers still contained old option labels");
});

await Test("queued spoken confirmation cannot apply after request schema replacement",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","queued-old",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("工作区");await f.Service.RouteVoiceAsync("notes.txt");
    var gate=ActionGate(f.Service);await gate.WaitAsync();var confirm=f.Service.RouteVoiceAsync("确认提交");
    var changed=Question() with{Reason="updated question context"};
    f.Client.SetRemote("waitingInput","queued-new",[changed]);PublishTask(f.Service,f.Service.Current with{PendingInteractions=[changed]});gate.Release();
    var result=await confirm;
    Check(result.ListenForReply&&f.Client.Responses.Count==0,"queued confirmation sent answers to replaced request");
    await f.Service.RouteVoiceAsync("确认提交");Check(f.Client.Responses.Count==0,"second confirmation resurrected stale completed draft");
    await f.Service.RouteVoiceAsync("工作区");await f.Service.RouteVoiceAsync("new.txt");await f.Service.RouteVoiceAsync("确认提交");
    Check(f.Client.Responses.Single().Answers!["q2"]=="new.txt","replaced queued request did not accept a newly answered draft");
});

await Test("selected second authorization is described before only that request is approved",async()=>
{
    using var f=new TaskCase();await f.Start();var second=Approval("two") with{ToolName="read_directory",Reason="读取任务目录"};
    f.Client.SetRemote("waitingApproval","two-approvals",[Approval("one"),second]);await Until(()=>f.Service.Current.PendingInteractions.Count==2);
    var selected=await f.Service.RouteVoiceAsync("选择授权第二项");
    Check(selected.ListenForReply&&selected.Message.Contains(second.ToolName)&&selected.Message.Contains(second.Reason)&&f.Client.Responses.Count==0,"selection approved a request or omitted its details");
    var approved=await f.Service.RouteVoiceAsync("批准本次");var response=f.Client.Responses.Single();
    Check(response.Interaction=="two"&&response.Outcome=="allowed-once"&&f.Client.Remote.PendingInteractions.Single().Id=="one","approval did not target exactly selected second request");
});

await Test("changed selected authorization requires a new explicit selection",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingApproval","old-approval",[Approval("one"),Approval("two")]);await Until(()=>f.Service.Current.PendingInteractions.Count==2);
    await f.Service.RouteVoiceAsync("选择授权第二项");
    var changed=Approval("two") with{Reason="changed operation"};f.Client.SetRemote("waitingApproval","replaced-approval",[Approval("one"),changed]);
    await Until(()=>f.Service.Current.PendingInteractions.Any(p=>p.Reason=="changed operation"));
    var answer=await f.Service.RouteVoiceAsync("批准本次");
    Check(answer.ListenForReply&&f.Client.Responses.Count==0,"old selected focus authorized changed operation");
    await f.Service.RouteVoiceAsync("选择授权第二项");await f.Service.RouteVoiceAsync("拒绝本次");
    Check(f.Client.Responses.Single() is{Interaction:"two",Outcome:"rejected"},"reselected changed request could not be rejected individually");
});

await Test("repeated confirmation after successful reply never becomes a new task prompt",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","one-reply",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("工作区");await f.Service.RouteVoiceAsync("notes.txt");await f.Service.RouteVoiceAsync("确认提交");
    foreach(var text in new[]{"确认提交","提交答案","检查答案","重听"})
    {
        var result=await f.Service.RouteVoiceAsync(text);
        Check(result.Handled&&!result.ListenForReply&&f.Client.Prompts.Count==1&&f.Client.Responses.Count==1,"stale interaction command was posted or submitted twice: "+text);
    }
});

await Test("empty task creation and status query request a follow-up utterance",async()=>
{
    using var f=new TaskCase();var created=await f.Service.RouteVoiceAsync("新建DSH任务");
    Check(created.ListenForReply&&f.Client.Prompts.Count==0,"empty task creation fell back to wake mode without asking content");
    var status=await f.Service.RouteVoiceAsync("当前任务状态");Check(status.ListenForReply&&f.Client.Prompts.Count==0,"awaiting-prompt status query lost follow-up capture");
    var submitted=await f.Service.RouteVoiceAsync("只回复通过");Check(!submitted.ListenForReply&&f.Client.Prompts.Single().Prompt=="只回复通过","submitted content created a spurious follow-up answer window");
});

await Test("numbered question edits replace only the chosen answer and reject missing question numbers",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","numbered-edit",[ChoiceQuestion()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("第一项");await f.Service.RouteVoiceAsync("第一项和第三项");
    var invalid=await f.Service.RouteVoiceAsync("修改第三题");
    Check(invalid.ListenForReply&&f.Service.Current.VoiceAnswers.Count==2&&f.Client.Responses.Count==0,"out-of-range edit removed an existing answer");
    var edited=await f.Service.RouteVoiceAsync("修改第一题");
    Check(edited.ListenForReply&&edited.Message.Contains("请选择样式")&&!f.Service.Current.VoiceAnswers.ContainsKey("style")&&f.Service.Current.VoiceAnswers["features"]=="搜索 | 颜色标签","numbered edit cleared other question answers");
    await f.Service.RouteVoiceAsync("第二项");await f.Service.RouteVoiceAsync("确认提交");
    var answers=f.Client.Responses.Single().Answers!;
    Check(answers["style"]=="深色夜间"&&answers["features"]=="搜索 | 颜色标签","numbered edit did not preserve other answers");
});

await Test("out-of-order question focus survives disconnect and binds the next utterance correctly",async()=>
{
    using var f=new TaskCase();await f.Start();
    var questions=Question() with{Questions=[new("first","第一题","第一项内容",[]),new("second","第二题","第二项内容",[]),new("third","第三题","第三项内容",[])]};
    f.Client.SetRemote("waitingInput","focused-third",[questions]);await Until(()=>f.Service.Current.NeedsAttention);
    var focus=await f.Service.RouteVoiceAsync("重答三题");Check(focus.Message.Contains("第三项内容"),"explicit third question focus was ignored");
    f.Client.ReadFailure=new IOException("temporary disconnect");await Until(()=>f.Service.Current.State=="disconnected");
    f.Client.ReadFailure=null;await Until(()=>f.Service.Current.State=="waitingInput");
    Check(f.Service.Current.Detail.Contains("第三项内容"),"poll recovery displayed the first unanswered question instead of focused question");
    var next=await f.Service.RouteVoiceAsync("第三题答案");
    Check(f.Service.Current.VoiceAnswers["third"]=="第三题答案"&&next.Message.Contains("第一项内容"),"focused utterance bound to another question");
    await f.Service.RouteVoiceAsync("第一题答案");await f.Service.RouteVoiceAsync("第二题答案");await f.Service.RouteVoiceAsync("确认提交");
    var answers=f.Client.Responses.Single().Answers!;
    Check(answers["first"]=="第一题答案"&&answers["second"]=="第二题答案"&&answers["third"]=="第三题答案","out-of-order answers were misbound");
});

await Test("incremental multiple-choice additions and removals preserve other answers without submitting",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","incremental",[ChoiceQuestion()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("第一项");await f.Service.RouteVoiceAsync("第一项和第三项");
    var added=await f.Service.RouteVoiceAsync("加选第二项");
    Check(added.ListenForReply&&f.Service.Current.VoiceAnswers["features"]=="搜索 | 置顶 | 颜色标签"&&f.Client.Responses.Count==0,"add choice did not union the last multiple-choice answer");
    await f.Service.RouteVoiceAsync("取消选择第三项");await f.Service.RouteVoiceAsync("去掉第一项");
    Check(f.Service.Current.VoiceAnswers["features"]=="置顶"&&f.Service.Current.VoiceAnswers["style"]=="浅色纸张 (Recommended)","removing a choice changed the wrong question or removed other answers");
    foreach(var text in new[]{"加上第九项","去掉未知","加上回答：自由文本"})
    {
        var invalid=await f.Service.RouteVoiceAsync(text);
        Check(invalid.ListenForReply&&f.Service.Current.VoiceAnswers["features"]=="置顶"&&f.Client.Responses.Count==0,"invalid incremental choice silently mutated the answer: "+text);
    }
    await f.Service.RouteVoiceAsync("确认提交");Check(f.Client.Responses.Single().Answers!["features"]=="置顶","incremental choice summary was not the submitted answer");
});

await Test("removing all multiple choices reopens only that question and requires a new answer",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","remove-all",[ChoiceQuestion()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("第一项");await f.Service.RouteVoiceAsync("第一项");
    var removed=await f.Service.RouteVoiceAsync("取消选择第一项");
    Check(removed.ListenForReply&&!f.Service.Current.VoiceAnswers.ContainsKey("features")&&f.Service.Current.VoiceAnswers.ContainsKey("style"),"removing all choices retained an empty answer or cleared unrelated style");
    await f.Service.RouteVoiceAsync("确认提交");Check(f.Client.Responses.Count==0,"empty multiple-choice answer submitted implicitly");
    var restored=await f.Service.RouteVoiceAsync("加上第三项");
    Check(restored.ListenForReply&&f.Service.Current.VoiceAnswers["features"]=="颜色标签","incremental addition could not answer current reopened multiple-choice question");
    await f.Service.RouteVoiceAsync("确认提交");Check(f.Client.Responses.Single().Answers!["features"]=="颜色标签","reopened multi-choice answer was not submitted");
});

await Test("a disappeared selected authorization cannot transfer an old approval to the only remaining request",async()=>
{
    using var f=new TaskCase();await f.Start();var first=Approval("one") with{ToolName="read_directory",Reason="读取目录"};var second=Approval("two") with{ToolName="write_file",Reason="写入文件"};
    f.Client.SetRemote("waitingApproval","selected-two",[first,second]);await Until(()=>f.Service.Current.PendingInteractions.Count==2);
    await f.Service.RouteVoiceAsync("选择授权第二项");
    f.Client.SetRemote("waitingApproval","first-remains",[first]);await Until(()=>f.Service.Current.PendingInteractions.Count==1);
    var stale=await f.Service.RouteVoiceAsync("批准本次");
    Check(stale.ListenForReply&&stale.Message.Contains("原授权请求已变化")&&stale.Message.Contains(first.Reason)&&f.Client.Responses.Count==0,"approval of disappeared second request was transferred to the remaining first request");
    await f.Service.RouteVoiceAsync("拒绝本次");Check(f.Client.Responses.Single() is{Interaction:"one",Outcome:"rejected"},"new explicit decision could not address the newly described request");
});

await Test("a microphone snapshot cannot approve a different remaining authorization",async()=>
{
    using var f=new TaskCase();await f.Start();var first=Approval("one") with{Reason="读取目录"};var second=Approval("two") with{Reason="写入文件"};
    f.Client.SetRemote("waitingApproval","microphone-two",[first,second]);await Until(()=>f.Service.Current.PendingInteractions.Count==2);
    await f.Service.RouteVoiceAsync("选择授权第二项");var spokenContext=f.Service.Current;
    f.Client.SetRemote("waitingApproval","microphone-one",[first]);await Until(()=>f.Service.Current.PendingInteractions.Count==1);
    foreach(var decision in new[]{"批准本次","拒绝本次"})
    {
        var result=await f.Service.RouteVoiceAsync(decision,spokenContext);
        Check(result.Handled&&result.ListenForReply&&f.Client.Responses.Count==0&&f.Client.Prompts.Count==1,"stale microphone context was applied to the remaining authorization: "+decision);
        Check(f.Service.Current.PendingInteractions.Single().Id=="one"&&f.Client.CancelCount==0&&f.Client.Creates.Count==1,"stale microphone context changed the task or pending requests");
    }
});

await Test("microphone context is rechecked when schema changes while awaiting the action gate",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","microphone-old-schema",[ChoiceQuestion()]);await Until(()=>f.Service.Current.NeedsAttention);
    var spokenContext=f.Service.Current;var gate=ActionGate(f.Service);await gate.WaitAsync();
    var answer=f.Service.RouteVoiceAsync("第一项",spokenContext);
    Check(!answer.IsCompleted,"microphone answer did not wait for the action gate");
    var changed=ChoiceQuestion() with{Questions=[new("style","样式","新样式选择",[new("绿色",""),new("蓝色","")])]};
    f.Client.SetRemote("waitingInput","microphone-new-schema",[changed]);PublishTask(f.Service,f.Service.Current with{PendingInteractions=[changed]});gate.Release();
    var result=await answer;
    Check(result.Handled&&result.ListenForReply&&f.Client.Responses.Count==0&&f.Client.Prompts.Count==1&&f.Service.Current.VoiceAnswers.Count==0,"queued microphone answer was recorded or submitted after schema replacement");
    Check(f.Client.CancelCount==0&&f.Client.ReleaseCount==0&&f.Client.Creates.Count==1,"stale queued answer changed task lifecycle");
    await f.Service.RouteVoiceAsync("第一项",f.Service.Current);await f.Service.RouteVoiceAsync("确认提交",f.Service.Current);
    Check(f.Client.Responses.Single().Answers!["style"]=="绿色","fresh microphone context could not answer the replacement schema");
});

await Test("changed voice answer revision rejects old recordings without overwriting or submitting the newer draft",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","microphone-answer-revision",[ChoiceQuestion()]);await Until(()=>f.Service.Current.NeedsAttention);
    var firstQuestionContext=f.Service.Current;await f.Service.RouteVoiceAsync("第一项",firstQuestionContext);
    var firstAnswer=f.Service.Current;
    Check(firstAnswer.VoiceAnswerRevision>firstQuestionContext.VoiceAnswerRevision&&firstAnswer.VoiceAnswers.Count==1,"answer did not advance the microphone draft revision");
    var staleAnswer=await f.Service.RouteVoiceAsync("第二项",firstQuestionContext);
    Check(staleAnswer.Handled&&staleAnswer.ListenForReply&&f.Service.Current.VoiceAnswerRevision==firstAnswer.VoiceAnswerRevision
        &&f.Service.Current.VoiceAnswers.Count==1&&f.Service.Current.VoiceAnswers["style"]=="浅色纸张 (Recommended)","old recording answered the next question or overwrote the new draft");
    await f.Service.RouteVoiceAsync("第二项",f.Service.Current);var completeContext=f.Service.Current;
    await f.Service.RouteVoiceAsync("修改第一题",f.Service.Current);await f.Service.RouteVoiceAsync("第二项",f.Service.Current);
    var revised=f.Service.Current;
    Check(revised.VoiceAnswerRevision>completeContext.VoiceAnswerRevision&&revised.VoiceAnswers["style"]=="深色夜间"&&revised.VoiceAnswers["features"]=="置顶","test did not establish a newer completed draft");
    foreach(var staleCommand in new[]{"确认提交","取消回答"})
    {
        var rejected=await f.Service.RouteVoiceAsync(staleCommand,completeContext);
        Check(rejected.Handled&&rejected.ListenForReply&&f.Client.Responses.Count==0&&f.Client.Prompts.Count==1
            &&f.Service.Current.VoiceAnswerRevision==revised.VoiceAnswerRevision&&f.Service.Current.VoiceAnswers["style"]=="深色夜间"
            &&f.Service.Current.VoiceAnswers["features"]=="置顶","old recording submitted or cleared the revised draft: "+staleCommand);
    }
    await f.Service.RouteVoiceAsync("确认提交",f.Service.Current);
    Check(f.Client.Responses.Single().Answers!["style"]=="深色夜间"&&f.Client.Responses.Single().Answers!["features"]=="置顶","fresh recording failed to submit the revised draft exactly once");
});

await Test("stopping monitoring clears published voice drafts even if remote release fails",async()=>
{
    foreach(var releaseFailure in new Exception?[]{null,new IOException("release failed")})
    {
        using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","stop-draft",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
        await f.Service.RouteVoiceAsync("工作区");Check(f.Service.Current.VoiceAnswers.Count==1,"test did not establish a voice draft");
        f.Client.ReleaseFailure=releaseFailure;await f.Service.RouteVoiceAsync("停止监控");
        Check(!f.Service.Current.IsMonitoring&&f.Service.Current.VoiceInteractionId==""&&f.Service.Current.VoiceAnswers.Count==0&&f.Client.CancelCount==0,"stopped task retained a published answer draft or cancelled real task");
    }
});

await Test("voice answer snapshots are immutable and repeats do not invalidate the visible draft",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","visible-draft",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("工作区");var first=f.Service.Current;
    Check(first.VoiceInteractionId=="question-1"&&first.VoiceAnswers.Count==1&&first.VoiceAnswers["q1"]=="工作区","first spoken answer was not published for the UI");
    await f.Service.RouteVoiceAsync("重听");
    Check(f.Service.Current.VoiceAnswerRevision==first.VoiceAnswerRevision,"repeating prompt invalidated the visible answer draft");
    await f.Service.RouteVoiceAsync("notes.txt");var complete=f.Service.Current;
    Check(first.VoiceAnswers.Count==1&&!first.VoiceAnswers.ContainsKey("q2")&&complete.VoiceAnswers["q2"]=="notes.txt"&&complete.VoiceAnswerRevision>first.VoiceAnswerRevision,"later answer mutated an earlier snapshot or failed to advance revision");
    await f.Service.RouteVoiceAsync("确认提交");
    Check(f.Service.Current.VoiceInteractionId==""&&f.Service.Current.VoiceAnswers.Count==0,"submitted answer remained visible as an active voice draft");
});

await Test("voice task list excludes device and archived sessions and requires list before numbered selection",async()=>
{
    using var f=new TaskCase();var now=DateTimeOffset.UtcNow;
    var first=new DshSessionSummary("ordinary-new","备忘录","C:/tasks/memo",now,"idle");
    var second=new DshSessionSummary("ordinary-old","清单","C:/tasks/list",now.AddMinutes(-1),"idle");
    f.Client.SetSessions([second,first,first with{Id="device",Title="音箱控制",IsDeviceControl=true},first with{Id="archive",Title="旧归档",IsArchived=true}]);
    f.Client.ReadOverride=(id,_)=>Task.FromResult(f.Client.Remote with{SessionId=id,TaskStatus="idle",HasSubmittedPrompt=false});
    var unlisted=await f.Service.RouteVoiceAsync("切换任务到第一项");
    Check(unlisted.ListenForReply&&f.Client.AdoptCount==0&&f.Client.Creates.Count==0,"an unheard task number adopted or created a task");
    var list=await f.Service.RouteVoiceAsync("任务列表");
    Check(list.ListenForReply&&list.Message.Contains("共2个任务")&&list.Message.Contains("第1项：备忘录")&&!list.Message.Contains("音箱控制")&&!list.Message.Contains("旧归档"),"spoken list included unsafe targets or wrong order");
    var selected=await f.Service.RouteVoiceAsync("切换任务到第二项");
    Check(selected.ListenForReply&&f.Service.Current.SessionId==second.Id&&f.Client.Current.VoiceTarget?.Id==second.Id&&f.Client.AdoptCount==1&&f.Client.Prompts.Count==0,"heard second task did not become the existing idle target");
});

await Test("duplicate spoken task names remain ambiguous but listed numbers can disambiguate",async()=>
{
    using var f=new TaskCase();var now=DateTimeOffset.UtcNow;
    var first=new DshSessionSummary("memo-new","备忘录","C:/tasks/memo-a",now,"idle");
    var second=new DshSessionSummary("memo-old","备忘录","C:/tasks/memo-b",now.AddMinutes(-1),"idle");
    f.Client.SetSessions([second,first]);f.Client.ReadOverride=(id,_)=>Task.FromResult(f.Client.Remote with{SessionId=id,TaskStatus="idle",HasSubmittedPrompt=false});
    await f.Service.RouteVoiceAsync("列出任务");var ambiguous=await f.Service.RouteVoiceAsync("切换任务到备忘录");
    Check(ambiguous.ListenForReply&&f.Client.AdoptCount==0&&!f.Service.Current.IsMonitoring,"duplicate title silently selected the first task");
    await f.Service.RouteVoiceAsync("切换任务到第二项");
    Check(f.Service.Current.SessionId==second.Id&&f.Client.Creates.Count==0,"explicit listed number could not resolve duplicate titles");
});

await Test("new sessions inserted after speech listing cannot renumber choices or cancel the old task",async()=>
{
    using var f=new TaskCase();var now=DateTimeOffset.UtcNow;
    var first=new DshSessionSummary("listed-first","检查配置","C:/tasks/config",now,"idle");
    var second=new DshSessionSummary("listed-second","备忘录","C:/tasks/memo",now.AddMinutes(-1),"idle");
    f.Client.SetSessions([first,second]);f.Client.ReadOverride=(id,_)=>Task.FromResult(f.Client.Remote with{SessionId=id,TaskStatus="running"});
    await f.Service.MonitorAsync(second);await f.Service.RouteVoiceAsync("有哪些任务");
    var inserted=first with{Id="inserted-newest",Title="刚插入",UpdatedAt=now.AddMinutes(1)};f.Client.SetSessions([inserted,first,second]);
    await f.Service.RouteVoiceAsync("切换任务到第一项");
    Check(f.Service.Current.SessionId==first.Id&&f.Client.AdoptCount==2&&f.Client.ReleaseCount==1&&f.Client.CancelCount==0,"list insertion changed heard number or switching cancelled the real old task");
    Check(f.Client.Creates.Count==0&&f.Client.Prompts.Count==0,"voice switching created a duplicate session or replayed work");
});

await Test("profile change invalidates previously announced task numbers",async()=>
{
    using var f=new TaskCase();var first=new DshSessionSummary("old-target","备忘录","C:/tasks/memo",DateTimeOffset.UtcNow,"idle");
    f.Client.SetSessions([first]);await f.Service.RouteVoiceAsync("任务列表");
    f.Scope="scope-B";f.Client.SetSessions([first with{Id="new-profile-target"}]);
    var result=await f.Service.RouteVoiceAsync("切换任务到第一项");
    Check(result.ListenForReply&&result.Message.Contains("先说任务列表")&&f.Client.AdoptCount==0&&f.Client.Prompts.Count==0&&!f.Service.Current.IsMonitoring,"old list number crossed into new profile");
});

await Test("failed spoken answer submission preserves draft without automatic retransmission",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","reply-failure",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("工作区");await f.Service.RouteVoiceAsync("notes.txt");
    f.Client.RespondFailure=new IOException("response unavailable");await Throws<IOException>(()=>f.Service.RouteVoiceAsync("确认提交"));
    await Task.Delay(30);
    Check(f.Client.Responses.Count==1&&f.Service.Current.NeedsAttention&&!f.Service.Current.IsBusy,"failed submission lost pending state or replayed automatically");
    var review=await f.Service.RouteVoiceAsync("检查答案");
    Check(review.ListenForReply&&review.Message.Contains("notes.txt")&&f.Client.Responses.Count==1,"failed submission lost draft or review retried it");
    f.Client.RespondFailure=null;await f.Service.RouteVoiceAsync("确认提交");
    Check(f.Client.Responses.Count==2&&f.Client.Responses.Last().Answers!["q2"]=="notes.txt","explicit retry could not send preserved draft");
});

await Test("explicit answer containing creation or device commands stays question data", async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","explicit-body",[Question()]);
    await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("我的答案是新建一个DSH任务并关闭歌词");
    Check(f.Service.Current.VoiceAnswers["q1"]=="新建一个DSH任务并关闭歌词"&&f.Client.Creates.Count==1&&f.Client.Prompts.Count==1,"explicit answer routed as a command");
    await f.Service.RouteVoiceAsync("notes.txt");await f.Service.RouteVoiceAsync("确认提交");
    Check(f.Client.Responses.Single().Answers!["q1"]=="新建一个DSH任务并关闭歌词","answer body changed on submission");
});

await PollingProbe.RunAsync(Test);

Console.WriteLine($"RESULT {passed}/{passed+failed} passed"); return failed==0?0:1;

internal sealed class TaskCase: IDisposable
{
    public FakeTaskClient Client {get;}=new();
    public DshTaskService Service {get;}
    public string Scope {get;set;}="scope-A";
    public string StateRoot {get;}=Path.Combine(Path.GetTempPath(),"halo-routing-case-"+Guid.NewGuid().ToString("N"));
    public System.Collections.Concurrent.ConcurrentQueue<string> Cues {get;}=new();
    public System.Collections.Concurrent.ConcurrentQueue<(DshTaskSnapshot Snapshot,string Cue)> Feedback {get;}=new();
    public TaskCase(TimeProvider? timeProvider=null, TimeSpan? interval=null) => Service=new(Client,()=>"C:/tasks",()=>Scope,feedback:(snapshot,cue,_)=>{Cues.Enqueue(cue);Feedback.Enqueue((snapshot,cue));return Task.CompletedTask;},
        stateRoot:StateRoot,
        pollInterval:interval??TimeSpan.FromMilliseconds(10), timeProvider:timeProvider);
    public Task<DshTaskSnapshot> Start()=>Service.StartAsync(new("C:/test","test","raw prompt"));
    public void Dispose()=>Service.Dispose();
}
internal sealed class FakeTaskClient:IDshTaskSessionClient
{
    public DshSessionsSnapshot Current {get;private set;}=new(true,"","",[],null);
    public event EventHandler<DshSessionsSnapshot>? Changed;
    public List<DshTaskStartRequest> Creates {get;}=[];
    public List<(string Id,string Prompt)> Prompts {get;}=[];
    public List<(string Id,string Interaction,string Type,string? Outcome,IReadOnlyDictionary<string,string>? Answers)> Responses {get;}=[];
    public int CancelCount {get;private set;} public int ReleaseCount {get;private set;}
    public Exception? CreateFailure {get;set;}
    public Exception? SubmitFailure {get;set;}
    public Exception? ReadFailure {get;set;}
    public Exception? ReleaseFailure {get;set;}
    public Exception? RespondFailure {get;set;}
    public Func<string,CancellationToken,Task<DshTaskRemoteState>>? ReadOverride {get;set;}
    public int ReadCount {get;private set;}
    public bool ServerAvailable {get;set;}=true;
    public bool RequireAdoption {get;set;}
    private bool adopted;
    public int RefreshCount {get;private set;} public int AdoptCount {get;private set;} public int ConnectCount {get;private set;}
    public bool AcceptSubmit {get;set;}=true;
    public DshTaskRemoteState Remote {get;private set;}=new("task-1","running","running","r1","","",true,[]);
    public void Signal()=>Changed?.Invoke(this,Current);
    public void Disconnect(){ServerAvailable=false;adopted=false;Current=Current with{IsConnected=false};Signal();}
    public void SetSessions(IReadOnlyList<DshSessionSummary> sessions,bool signal=true){Current=Current with{Sessions=sessions};if(signal)Signal();}
    public void SetRemote(string state,string revision,IReadOnlyList<DshTaskInteraction> interactions,bool hasSubmittedPrompt=true)=>Remote=Remote with{TaskStatus=state,Revision=revision,PendingInteractions=interactions,HasSubmittedPrompt=hasSubmittedPrompt};
    public Task RefreshAsync(CancellationToken ct=default){RefreshCount++;Current=Current with{IsConnected=ServerAvailable};Signal();return Task.CompletedTask;}
    public Task ConnectAsync(CancellationToken ct=default){ConnectCount++;ServerAvailable=true;Current=Current with{IsConnected=true};Signal();return Task.CompletedTask;}
    public Task<DshSessionSummary> CreateTaskSessionAsync(DshTaskStartRequest request,CancellationToken ct=default)
    {
        if(CreateFailure is not null)throw CreateFailure;
        Creates.Add(request);var s=new DshSessionSummary("task-"+Creates.Count,request.Title,request.BaseDirectory+"/child",DateTimeOffset.Now,"idle");
        Remote=Remote with{SessionId=s.Id,TaskStatus="idle",RuntimeStatus="idle",Revision="created-"+Creates.Count,HasSubmittedPrompt=false,PendingInteractions=[]};
        Current=Current with{Sessions=Current.Sessions.Append(s).ToArray()};Signal();return Task.FromResult(s);
    }
    public Task AdoptTaskSessionAsync(string id,CancellationToken ct=default){AdoptCount++;adopted=true;return Task.CompletedTask;}
    public Task<DshTaskSubmission> SubmitTaskPromptAsync(string id,string prompt,CancellationToken ct=default)
    {
        Prompts.Add((id,prompt));if(SubmitFailure is not null)throw SubmitFailure;
        if(AcceptSubmit)Remote=Remote with{SessionId=id,TaskStatus="running",RuntimeStatus="running",Revision=Remote.Revision+"-submitted",HasSubmittedPrompt=true};
        return Task.FromResult(new DshTaskSubmission(id,Guid.NewGuid().ToString(),AcceptSubmit));
    }
    public Task<DshTaskRemoteState> ReadTaskStateAsync(string id,CancellationToken ct=default)
    {
        ReadCount++;
        return ReadOverride is not null?ReadOverride(id,ct):ReadFailure is not null?Task.FromException<DshTaskRemoteState>(ReadFailure):!ServerAvailable||RequireAdoption&&!adopted
            ?Task.FromException<DshTaskRemoteState>(new IOException("host unavailable or task not adopted")):Task.FromResult(Remote);
    }
    public Task RespondTaskInteractionAsync(string id,string interaction,string type,string? outcome,IReadOnlyDictionary<string,string>? answers,CancellationToken ct=default)
    {
        Responses.Add((id,interaction,type,outcome,answers));
        if(RespondFailure is not null)throw RespondFailure;
        Remote=Remote with{Revision=Remote.Revision+"answered",PendingInteractions=Remote.PendingInteractions.Where(p=>p.Id!=interaction).ToArray(),TaskStatus="running"};
        return Task.CompletedTask;
    }
    public Task CancelTaskSessionAsync(string id,CancellationToken ct=default){CancelCount++;return Task.CompletedTask;}
    public Task ReleaseTaskSessionAsync(string id,CancellationToken ct=default){ReleaseCount++;return ReleaseFailure is null?Task.CompletedTask:Task.FromException(ReleaseFailure);}
    public void SelectVoiceTarget(DshSessionSummary s)=>Current=Current with{VoiceTarget=s};
    public void ClearVoiceTarget()=>Current=Current with{VoiceTarget=null};
}
