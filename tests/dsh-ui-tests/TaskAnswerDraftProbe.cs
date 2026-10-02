using HaloPixelToolBox.Models;
using HaloPixelToolBox.ViewModels;

public static class TaskAnswerDraftProbe
{
    public static void Run(Action<bool, string> check)
    {
        var question = new DshTaskQuestion("q1", "目录", "选择任务目录",
            [new("文档", "文档目录"), new("项目", "项目目录"), new("临时", "临时目录")]);
        var answer = new DshTaskQuestionAnswer(question);
        var changes = 0;
        answer.Changed += (_, _) => changes++;
        check(answer.Answer == string.Empty && !answer.HasAnswer
            && Enumerable.Range(0, 3).All(index => !answer.IsOptionSelected(index)),
            "question starts with no default answer or selected option");
        answer.SetOptionSelected(0, true);
        check(answer.Answer == "文档" && answer.HasAnswer && changes == 1,
            "an explicit single-option selection supplies its label and notifies once");
        answer.SetOptionSelected(1, true);
        check(!answer.IsOptionSelected(0) && answer.IsOptionSelected(1) && answer.Answer == "项目",
            "single-option selection replaces the previous choice");
        answer.SetOptionSelected(1, true);
        check(changes == 2, "unchanged option selection does not emit another change");
        answer.FreeText = "  自定义路径  ";
        check(answer.Answer == "自定义路径" && answer.HasAnswer
            && Enumerable.Range(0, 3).All(index => !answer.IsOptionSelected(index)),
            "custom text clears option choices and trims the submitted answer");
        answer.SetOptionSelected(2, true);
        check(answer.FreeText == string.Empty && answer.Answer == "临时",
            "an option choice clears custom text rather than combining answer sources");
        answer.SetOptionSelected(2, false);
        check(!answer.HasAnswer && answer.Answer == string.Empty,
            "deselecting the final choice requires a fresh explicit answer");
        answer.FreeText = "   \r\n ";
        check(!answer.HasAnswer && answer.Answer.Length == 0, "whitespace is not a question answer");
        answer.FreeText = new string('字', 4096);
        check(answer.HasAnswer, "4096-character explicit answer is accepted");
        answer.FreeText = new string('字', 4097);
        check(!answer.HasAnswer, "long custom answer is retained as a draft but cannot be submitted");
        answer.FreeText = string.Empty;
        check(!answer.HasAnswer, "clearing custom text does not restore a previously selected option");
        check(!answer.IsOptionSelected(-1) && !answer.IsOptionSelected(3),
            "unknown option indices cannot appear selected");
        var invalidIndexRejected = false;
        try { answer.SetOptionSelected(3, true); }
        catch (ArgumentOutOfRangeException) { invalidIndexRejected = true; }
        check(invalidIndexRejected && !answer.HasAnswer, "invalid option mutation fails without changing the draft");

        var multi = new DshTaskQuestionAnswer(question with { MultiSelect = true });
        multi.SetOptionSelected(2, true);
        multi.SetOptionSelected(0, true);
        check(multi.Answer == "文档 | 临时" && multi.IsOptionSelected(0) && multi.IsOptionSelected(2),
            "multiple selections retain all explicit choices in stable option order");
        multi.SetOptionSelected(0, false);
        check(multi.Answer == "临时" && multi.IsOptionSelected(2),
            "multi-select deselection leaves the other choices intact");
        multi.FreeText = "用户解释";
        check(multi.Answer == "用户解释" && !multi.IsOptionSelected(2),
            "custom text clears all multi-select choices");
        multi.SetOptionSelected(1, true);
        check(multi.FreeText.Length == 0 && multi.Answer == "项目", "multi-select choice also replaces custom text");
        var longOption = new DshTaskQuestionAnswer(question with { Options = [new(new string('字', 4097), "长选项")] });
        longOption.SetOptionSelected(0, true);
        check(!longOption.HasAnswer, "overlong selected option cannot bypass answer size validation");

        var createdAt = DateTimeOffset.Parse("2026-10-02T12:00:00+08:00");
        var request = new DshTaskInteraction("request1", "question", "request_user_input", "选择目录", [question], createdAt);
        var equivalent = request with
        {
            Questions = request.Questions.Select(item => item with
            {
                Options = item.Options.Select(option => new DshTaskOption(option.Label, option.Description)).ToList()
            }).ToList()
        };
        check(DshTaskInteractionIdentity.Matches(request, equivalent),
            "structurally identical requests match even when question and option list references differ");
        check(DshTaskInteractionIdentity.Matches(null, null) && !DshTaskInteractionIdentity.Matches(request, null)
            && !DshTaskInteractionIdentity.Matches(null, request), "request identity handles absent requests explicitly");
        foreach (var (changed, name) in new (DshTaskInteraction, string)[]
        {
            (request with { Id = "request2" }, "request id"),
            (request with { Type = "approval" }, "request type"),
            (request with { ToolName = "other_tool" }, "tool name"),
            (request with { Reason = "新原因" }, "request reason"),
            (request with { CreatedAt = createdAt.AddSeconds(1) }, "creation time"),
            (request with { Questions = [] }, "question count"),
            (request with { Questions = [question with { Id = "q2" }] }, "question id"),
            (request with { Questions = [question with { Header = "新标题" }] }, "question header"),
            (request with { Questions = [question with { Question = "新的问题" }] }, "question text"),
            (request with { Questions = [question with { MultiSelect = true }] }, "multi-select mode"),
            (request with { Questions = [question with { Options = question.Options.Take(2).ToArray() }] }, "option count"),
            (request with { Questions = [question with { Options = [new("新选项", "文档目录"), ..question.Options.Skip(1)] }] }, "option label"),
            (request with { Questions = [question with { Options = [new("文档", "新说明"), ..question.Options.Skip(1)] }] }, "option description"),
            (request with { Questions = [question with { Options = question.Options.Reverse().ToArray() }] }, "option order")
        })
            check(!DshTaskInteractionIdentity.Matches(request, changed), $"request identity detects changed {name}");

        var drafts = new DshTaskAnswerDrafts();
        DshTaskSnapshot Snapshot(string session = "session1", IReadOnlyList<DshTaskInteraction>? pending = null)
            => new(session, "任务", @"C:\Tasks", "waiting", "等待回答", "", "", pending ?? [request], true, false);
        drafts.Synchronize("scope1", Snapshot());
        var first = drafts.GetOrCreate("scope1", "session1", request);
        first[0].FreeText = "保留答案";
        drafts.Synchronize("scope1", Snapshot(pending: [equivalent]));
        check(ReferenceEquals(first, drafts.GetOrCreate("scope1", "session1", equivalent)) && first[0].Answer == "保留答案",
            "closing and reopening an unchanged request retains the same in-memory answer objects");
        drafts.Synchronize("scope1", Snapshot() with { IsBusy = true });
        check(ReferenceEquals(first, drafts.GetOrCreate("scope1", "session1", request)),
            "temporary task activity does not discard an unchanged pending answer");

        var request2 = request with { Id = "request2" };
        drafts.Synchronize("scope1", Snapshot(pending: [request, request2]));
        var second = drafts.GetOrCreate("scope1", "session1", request2);
        second[0].SetOptionSelected(1, true);
        check(first[0].Answer == "保留答案" && second[0].Answer == "项目" && !ReferenceEquals(first[0], second[0]),
            "multiple pending requests keep independent drafts");
        drafts.Synchronize("scope1", Snapshot(pending: [request2]));
        check(ReferenceEquals(second, drafts.GetOrCreate("scope1", "session1", request2)),
            "resolving one request preserves another request still pending");
        check(!ReferenceEquals(first, drafts.GetOrCreate("scope1", "session1", request))
            && !drafts.GetOrCreate("scope1", "session1", request)[0].HasAnswer,
            "a request removed from pending state cannot resurrect its old answer");

        var beforeChange = drafts.GetOrCreate("scope1", "session1", request);
        beforeChange[0].FreeText = "旧答案";
        var changedSchema = request with { Questions = [question with { Question = "重新选择目录" }] };
        drafts.Synchronize("scope1", Snapshot(pending: [changedSchema]));
        var changedDraft = drafts.GetOrCreate("scope1", "session1", changedSchema);
        check(!ReferenceEquals(beforeChange, changedDraft) && !changedDraft[0].HasAnswer,
            "same request id with a changed question schema never reuses old answers");
        changedDraft[0].FreeText = "另一个答案";
        var updatedReason = changedSchema with { Reason = "更新的请求说明" };
        var afterDirectChange = drafts.GetOrCreate("scope1", "session1", updatedReason);
        check(!ReferenceEquals(changedDraft, afterDirectChange) && !afterDirectChange[0].HasAnswer,
            "GetOrCreate itself detects same-id content changes before the next synchronization");

        afterDirectChange[0].FreeText = "本地答案";
        drafts.Synchronize("scope2", Snapshot(pending: [updatedReason]));
        var otherScope = drafts.GetOrCreate("scope2", "session1", updatedReason);
        check(!ReferenceEquals(afterDirectChange, otherScope) && !otherScope[0].HasAnswer,
            "switching DSH scope clears drafts even for the same session and request ids");
        otherScope[0].FreeText = "其他环境";
        var otherSession = drafts.GetOrCreate("scope2", "session2", updatedReason);
        check(!ReferenceEquals(otherScope, otherSession) && !otherSession[0].HasAnswer,
            "switching session directly cannot expose another session's answer");
        drafts.Synchronize("scope2", Snapshot("session1", [updatedReason]));
        check(!ReferenceEquals(otherScope, drafts.GetOrCreate("scope2", "session1", updatedReason)),
            "returning to a former task session does not revive cleared drafts");
        var stopped = drafts.GetOrCreate("scope2", "session1", updatedReason);
        stopped[0].FreeText = "停止前的答案";
        drafts.Synchronize("scope2", Snapshot(pending: [updatedReason]) with { IsMonitoring = false });
        check(!ReferenceEquals(stopped, drafts.GetOrCreate("scope2", "session1", updatedReason)),
            "stopping task monitoring invalidates pending request drafts");
        var expired = drafts.GetOrCreate("scope2", "session1", updatedReason);
        drafts.Synchronize("scope2", Snapshot(pending: []));
        check(!ReferenceEquals(expired, drafts.GetOrCreate("scope2", "session1", updatedReason)),
            "empty pending requests remove expired drafts");

        var mutableOptions = question.Options.ToList();
        var mutableQuestions = new List<DshTaskQuestion> { question with { Options = mutableOptions } };
        var mutableRequest = request with { Questions = mutableQuestions };
        var mutableDraft = drafts.GetOrCreate("scope2", "session1", mutableRequest);
        mutableDraft[0].SetOptionSelected(0, true);
        mutableOptions[0] = new("新目录", "新目录说明");
        check(mutableDraft[0].Answer == "文档", "pending draft uses a stable question snapshot when source option list changes");
        check(!ReferenceEquals(mutableDraft, drafts.GetOrCreate("scope2", "session1", mutableRequest)),
            "mutating an existing source option list invalidates its captured draft schema");
        var mutableQuestionDraft = drafts.GetOrCreate("scope2", "session1", mutableRequest);
        mutableQuestions[0] = question with { Header = "新标题" };
        check(!ReferenceEquals(mutableQuestionDraft, drafts.GetOrCreate("scope2", "session1", mutableRequest)),
            "mutating an existing source question list also invalidates its draft schema");

        var removed = drafts.GetOrCreate("scope2", "session1", request);
        drafts.Remove("different-scope", "session1", request.Id);
        check(ReferenceEquals(removed, drafts.GetOrCreate("scope2", "session1", request)),
            "removing a draft from another scope cannot alter the active request");
        drafts.Remove("scope2", "session1", request.Id);
        check(!ReferenceEquals(removed, drafts.GetOrCreate("scope2", "session1", request)),
            "explicitly removing the current request clears its answers");
        var cleared = drafts.GetOrCreate("scope2", "session1", request);
        drafts.Clear();
        check(!ReferenceEquals(cleared, drafts.GetOrCreate("scope2", "session1", request)),
            "clearing the page's memory resets all task answers");
    }
}
