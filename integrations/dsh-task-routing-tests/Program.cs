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
    using var f=new TaskCase(); await f.Service.RouteVoiceAsync("新建任务"); f.Scope="profile-B";
    var result=await f.Service.RouteVoiceAsync("任务内容：旧目录执行");
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

await Test("voice two-question flow sends once only after all answers", async()=>
{
    using var f=new TaskCase(); await f.Start(); f.Client.SetRemote("waitingInput","q1",[Question()]); await Until(()=>f.Service.Current.NeedsAttention);
    var first=await f.Service.RouteVoiceAsync("工作区"); Check(first.Message.Contains("文件名")&&f.Client.Responses.Count==0,"first answered all");
    await f.Service.RouteVoiceAsync("notes.txt"); var answer=f.Client.Responses.Single();
    Check(answer.Answers is { Count:2 }&&answer.Answers["q1"]=="工作区"&&answer.Answers["q2"]=="notes.txt","answers misbound");
});

await Test("new question interaction clears answers collected for old request", async()=>
{
    using var f=new TaskCase(); await f.Start(); f.Client.SetRemote("waitingInput","q1",[Question("old")]); await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("old answer"); f.Client.SetRemote("waitingInput","q2",[Question("new")]);
    await Until(()=>f.Service.Current.PendingInteractions[0].Id=="new"); await f.Service.RouteVoiceAsync("new first");
    Check(f.Client.Responses.Count==0,"stale first answer reused"); await f.Service.RouteVoiceAsync("new second");
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
    await f.Service.RouteVoiceAsync("歌词放在这里");await f.Service.RouteVoiceAsync("氛围灯配置.txt");
    var response=f.Client.Responses.Single();Check(response.Answers!["q1"]=="歌词放在这里"&&response.Answers["q2"]=="氛围灯配置.txt","domain word stole question answer");
});

await Test("explicit device bypass preserves pending question and collected answers",async()=>
{
    using var f=new TaskCase();await f.Start();f.Client.SetRemote("waitingInput","q1",[Question()]);await Until(()=>f.Service.Current.NeedsAttention);
    await f.Service.RouteVoiceAsync("first answer");Check(!(await f.Service.RouteVoiceAsync("设备关灯")).Handled,"explicit device command swallowed");
    Check(f.Client.Responses.Count==0&&f.Service.Current.NeedsAttention,"device bypass completed pending");
    await f.Service.RouteVoiceAsync("second answer");Check(f.Client.Responses.Single().Answers!["q1"]=="first answer","bypass lost answers");
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
    Check(f.Service.Current.Detail=="文件名？","partial answer did not show next question");
    f.Client.ReadFailure=new IOException("temporary disconnect");await Until(()=>f.Service.Current.State=="disconnected");
    f.Client.ReadFailure=null;await Until(()=>f.Service.Current.State=="waitingInput");
    Check(f.Service.Current.Detail=="文件名？","reconnect displayed already answered question while binding next answer elsewhere");
    await f.Service.RouteVoiceAsync("notes.txt");Check(f.Client.Responses.Single().Answers!["q1"]=="工作区","reconnect discarded collected answer");
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

Console.WriteLine($"RESULT {passed}/{passed+failed} passed"); return failed==0?0:1;

internal sealed class TaskCase: IDisposable
{
    public FakeTaskClient Client {get;}=new();
    public DshTaskService Service {get;}
    public string Scope {get;set;}="scope-A";
    public string StateRoot {get;}=Path.Combine(Path.GetTempPath(),"halo-routing-case-"+Guid.NewGuid().ToString("N"));
    public System.Collections.Concurrent.ConcurrentQueue<string> Cues {get;}=new();
    public TaskCase() => Service=new(Client,()=>"C:/tasks",()=>Scope,feedback:(_,cue,_)=>{Cues.Enqueue(cue);return Task.CompletedTask;},
        stateRoot:StateRoot,
        pollInterval:TimeSpan.FromMilliseconds(10));
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
        =>ReadFailure is not null?Task.FromException<DshTaskRemoteState>(ReadFailure):!ServerAvailable||RequireAdoption&&!adopted
            ?Task.FromException<DshTaskRemoteState>(new IOException("host unavailable or task not adopted")):Task.FromResult(Remote);
    public Task RespondTaskInteractionAsync(string id,string interaction,string type,string? outcome,IReadOnlyDictionary<string,string>? answers,CancellationToken ct=default)
    {
        Responses.Add((id,interaction,type,outcome,answers));
        Remote=Remote with{Revision=Remote.Revision+"answered",PendingInteractions=Remote.PendingInteractions.Where(p=>p.Id!=interaction).ToArray(),TaskStatus="running"};
        return Task.CompletedTask;
    }
    public Task CancelTaskSessionAsync(string id,CancellationToken ct=default){CancelCount++;return Task.CompletedTask;}
    public Task ReleaseTaskSessionAsync(string id,CancellationToken ct=default){ReleaseCount++;return Task.CompletedTask;}
    public void SelectVoiceTarget(DshSessionSummary s)=>Current=Current with{VoiceTarget=s};
    public void ClearVoiceTarget()=>Current=Current with{VoiceTarget=null};
}
