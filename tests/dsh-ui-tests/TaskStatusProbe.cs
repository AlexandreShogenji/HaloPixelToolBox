using HaloPixelToolBox;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.ViewModels;

public static class TaskStatusProbe
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var service = App.DshSessions;
        var task = App.DshTasks;
        var a = new DshSessionSummary("STATUS-A", "任务 A", @"C:\Tasks\a", DateTimeOffset.Now, "idle");
        var b = new DshSessionSummary("STATUS-B", "任务 B", @"C:\Tasks\b", DateTimeOffset.Now, "idle");
        var capabilities = new DshSessionCapabilities(true, true, true, true, true)
        {
            CanCreateTasks = true, CanPromptTasks = true, CanMonitorTasks = true,
            CanRespondToTasks = true, CanAdoptTasks = true, CanCancelTasks = true
        };
        task.Reset();
        DisplayFeatureProfile.DshProfileName = "default";
        App.VoiceAgent.IsRunning = true;
        service.HistoryReader = (_, _, _) => Task.FromResult(new DshHistoryPage([], null, false));
        service.Publish(new(true, "host", "connected", [a, b], null)
        {
            Home = @"C:\StatusHome", Capabilities = capabilities
        });
        var vm = new DshSessionsPageViewModel();
        vm.Attach();
        DshTaskSnapshot Active(DshSessionSummary session, IReadOnlyList<DshTaskInteraction>? pending = null)
            => new(session.Id, session.Title, session.WorkingDirectory, "running", "运行中", "", "", pending ?? [], true, false);
        var approval = new DshTaskInteraction("APPROVAL", "approval", "write_file", "写入本任务文件", [], DateTimeOffset.Now);
        var question = new DshTaskInteraction("QUESTION", "question", "request_user_input", "选择处理范围",
            [new("range", "范围", "处理哪些文件？", [new("全部", "所有文件"), new("文档", "文档文件")])], DateTimeOffset.Now);
        try
        {
            task.Publish(Active(a));
            vm.TaskOperationStatus = "任务操作未完成：当前任务连接失败";
            task.Publish(Active(a) with { StatusText = "执行新步骤" });
            check(vm.TaskOperationStatus.Contains("连接失败") && vm.TaskSummary.Contains("连接失败"),
                "same task polling preserves unresolved operation failure");
            task.Publish(Active(b));
            check(vm.TaskOperationStatus.Length == 0 && vm.TaskSummary == "任务 B · 运行中"
                && !vm.TaskDetails.Contains("连接失败"), "new monitored task clears previous task error and detail");

            task.Publish(Active(a) with { State = "completed", StatusText = "本轮完成" });
            task.Starter = (_, _) => Task.FromResult(Active(a) with { State = "unknown", StatusText = "提交未确认" });
            await vm.StartTaskAsync(new(@"C:\Tasks", "待确认任务", "只读检查"));
            check(vm.TaskOperationStatus.Contains("未确认"), "unconfirmed task admission retains actionable warning");
            task.Publish(Active(a) with { State = "unknown", StatusText = "正在查询" });
            check(vm.TaskSummary.Contains("未确认"), "same task refresh cannot erase unconfirmed admission warning");
            task.Publish(Active(b));
            check(vm.TaskOperationStatus.Length == 0, "another confirmed task does not inherit unconfirmed admission warning");
            task.Starter = null;

            var changedApproval = approval with { Reason = "写入另一个未经审阅的文件" };
            task.Publish(Active(a, [changedApproval]));
            check(!vm.IsTaskInteractionCurrent(a.Id, approval), "same approval id with changed reason invalidates reviewed request");
            await vm.RespondTaskApprovalAsync(a.Id, approval, true);
            check(task.ApproveCalls == 0 && vm.TaskOperationStatus.Contains("已改变"),
                "stale reviewed approval is rejected before invoking task service");
            task.Publish(Active(a, [changedApproval with { Questions = [] }]));
            check(vm.TaskOperationStatus.Length == 0 && vm.TaskSummary == "任务 A · 运行中",
                "replacement request clears old request warning without hiding current task");

            var equivalentQuestion = question with
            {
                Questions = question.Questions.Select(item => item with { Options = item.Options.ToArray() }).ToArray()
            };
            task.Publish(Active(a, [equivalentQuestion]));
            check(vm.IsTaskInteractionCurrent(a.Id, question), "equivalent deserialized questions match structurally across fresh arrays");
            var changedQuestion = equivalentQuestion with
            {
                Questions = [equivalentQuestion.Questions[0] with { Options = [new("仅代码", "代码文件")] }]
            };
            task.Publish(Active(a, [changedQuestion]));
            await vm.RespondTaskQuestionAsync(a.Id, question, new Dictionary<string, string> { ["range"] = "全部" });
            check(task.QuestionCalls == 0 && vm.TaskOperationStatus.Contains("已改变"),
                "same question id with changed options cannot receive old answer");
            task.Publish(Active(a, [changedQuestion]));
            check(vm.TaskOperationStatus.Length == 0, "stale answer warning does not cover replacement question on refresh");

            task.Publish(Active(a, [approval]));
            task.ApprovalResponder = (_, _, _) => Task.FromException(new IOException("本次答复连接失败"));
            await vm.RespondTaskApprovalAsync(a.Id, approval, true);
            check(vm.TaskOperationStatus.Contains("本次答复连接失败") && !vm.IsTaskActionBusy,
                "failed answer remains associated with unresolved current request");
            task.Publish(Active(a, [approval with { Questions = [] }]));
            check(vm.TaskSummary.Contains("本次答复连接失败"), "ordinary polling preserves current request submission failure");
            task.Publish(Active(a, [question]));
            check(vm.TaskOperationStatus.Length == 0, "resolved approval failure cannot cover next question in same task");
            task.ApprovalResponder = null;

            task.QuestionResponder = (_, _, _) => Task.FromException(new IOException("问题答复未确认"));
            await vm.RespondTaskQuestionAsync(a.Id, question, new Dictionary<string, string> { ["range"] = "文档" });
            task.Publish(Active(a, [equivalentQuestion]));
            check(vm.TaskSummary.Contains("问题答复未确认"), "current question unconfirmed response survives equivalent polling");
            task.Publish(Active(a, []) with { State = "disconnected", StatusText = "任务状态暂不可用" });
            check(vm.TaskOperationStatus.Contains("问题答复未确认"), "disconnect without authoritative request list preserves unconfirmed answer warning");
            task.Publish(Active(a, [equivalentQuestion]));
            check(vm.TaskOperationStatus.Contains("问题答复未确认"), "same pending request after reconnect retains its unconfirmed answer warning");
            task.Publish(Active(a, []));
            check(vm.TaskOperationStatus.Length == 0, "resolved question removes only its own old response warning");
            task.QuestionResponder = null;

            task.Publish(Active(a, [approval]));
            var delayedResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task.ApprovalResponder = (_, _, _) => delayedResponse.Task;
            var responding = vm.RespondTaskApprovalAsync(a.Id, approval, true);
            check(vm.IsTaskActionBusy, "pending explicit response keeps duplicate task actions disabled");
            task.Publish(Active(b, [approval]));
            delayedResponse.SetResult();
            await responding;
            check(task.ApproveCalls == 0 && !vm.IsTaskActionBusy && vm.TaskOperationStatus.Length == 0
                && vm.TaskSummary == "任务 B · 运行中", "late old task response cannot authorize or overwrite new task with reused request id");
            task.ApprovalResponder = null;

            task.Publish(Active(a) with { Detail = "远端交互释放未确认；请在原会话核对。" });
            await vm.StopTaskMonitoringCommand.ExecuteAsync(null);
            check(!vm.TaskSnapshot.IsMonitoring && vm.TaskSummary.Contains("远端交互释放未确认")
                && vm.TaskSummary.Contains("仍保留"), "local stop retains unconfirmed remote release warning in compact task status");

            vm.TaskOperationStatus = "任务操作未完成：旧数据目录错误";
            service.Publish(service.Current with { Home = @"C:\OtherStatusHome" });
            check(vm.TaskOperationStatus.Length == 0 && vm.TaskSnapshot.SessionId.Length == 0,
                "DSH home change clears operation status and request context together");
        }
        finally
        {
            vm.Detach();
            task.Reset();
        }
    }
}
