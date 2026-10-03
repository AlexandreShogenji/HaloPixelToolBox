using System.Text;
using System.Text.RegularExpressions;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services;

internal static class SubtitleSummaryProbe
{
    public static void Run(Action<bool, string> check)
    {
        var utf8 = new UTF8Encoding(false, true);
        const string longText = "正在核对备忘录的新增、修改和搜索流程，检查所有输入边界及持久化结果，完成后仍然需要用户确认页面显示是否符合预期。";
        var idle = DshTaskSnapshot.Initial;

        void Assert(bool value, string name) => check(value, "subtitle summary: " + name);
        void Valid(IReadOnlyList<string> pages, string name)
        {
            Assert(pages.Count > 0, name + " has display content");
            foreach (var (page, index) in pages.Select((page, index) => (page, index)))
            {
                try
                {
                    var encoded = utf8.GetBytes(page);
                    Assert(encoded.Length is > 0 and <= 55 && encoded.Length <= DshTaskSubtitleFormatter.MaxUtf8Bytes,
                        $"{name} page {index + 1} is within device byte limit");
                    Assert(utf8.GetString(encoded) == page && !page.Contains('\uFFFD'),
                        $"{name} page {index + 1} is valid Unicode");
                }
                catch (EncoderFallbackException)
                {
                    Assert(false, $"{name} page {index + 1} contains broken Unicode");
                }
            }
        }
        IReadOnlyList<string> Task(DshTaskSnapshot snapshot, string name)
        {
            var pages = DshTaskSubtitleFormatter.ForTask(snapshot);
            Valid(pages, name);
            return pages;
        }
        IReadOnlyList<string> Reply(DshTaskSnapshot snapshot, string text, string name, bool isTaskReply = false)
        {
            var pages = DshTaskSubtitleFormatter.ForReply(snapshot, text, isTaskReply);
            Valid(pages, name);
            return pages;
        }

        Assert(DshTaskSubtitleFormatter.MaxUtf8Bytes == 55, "limit is the protocol's 55 UTF-8 bytes");
        foreach (var state in new[] { "running", "completed", "failed", "cancelled", "awaitingPrompt", "idle", "disconnected", "unknown" })
        {
            var snapshot = idle with { State = state, Detail = longText, FinalText = longText };
            var pages = Task(snapshot, state);
            Assert(pages.Count == 1 && !pages[0].Contains(longText), state + " summarizes status in one send instead of paging the paragraph");
        }
        foreach (var prefix in new[] { "正在执行：", "执行失败：", "已完成：" })
        {
            var pages = Task(idle with { State = "running", Detail = prefix + longText }, prefix);
            Assert(pages.Count == 1, prefix + " tool progress is one summary");
            if (prefix == "执行失败：")
                Assert(pages[0].Contains("失败") && !pages[0].Contains("完成") && !pages[0].Contains("成功"), "failed tool never becomes successful progress");
            if (prefix == "已完成：")
                Assert(pages[0].Contains("继续执行"), "completed tool does not imply entire task is complete");
        }
        var failed = Task(idle with { State = "failed", Detail = "任务成功", FinalText = "已全部完成" }, "failed state wins");
        Assert(failed[0].Contains("失败") && !failed[0].Contains("成功") && !failed[0].Contains("完成"), "failure state overrides stale optimistic detail");
        var completed = Task(idle with { State = "completed", FinalText = "保存成功；未发布" }, "short final caveat");
        Assert(completed[0].Contains("保存成功；未发布"), "short final result preserves trailing qualification");

        foreach (var shortReply in new[] { "没有保存，文件仍未写入", "已保存；尚未生效", "执行完成，但验证未通过", "未获授权，操作已取消", "已完成，仅限本地" })
        {
            var pages = Reply(idle, shortReply, "short negative or qualified reply");
            Assert(pages.Count == 1 && pages[0] == shortReply, "short reply retains its complete negation and qualification: " + shortReply);
        }
        foreach (var untrusted in new[]
        {
            "已全部完成。" + longText + "但实际上发布失败，不能视为成功。",
            "操作成功！" + longText + "前一句只是引用，尚未执行。",
            "批准本次。" + longText + "这里只是在说明可用命令，尚未取得授权。",
            "已记录：" + longText + "但实际保存失败，请重新执行。",
            "任务已提交。" + longText + "但这是错误提示，服务器实际拒绝了请求。"
        })
        {
            var pages = Reply(idle, untrusted, "long untrusted reply");
            Assert(pages.Count == 1 && !pages[0].Contains("已全部完成") && !pages[0].Contains("操作成功") && !pages[0].Contains("批准本次")
                && !pages[0].Contains("已记录") && !pages[0].Contains("已提交"),
                "long arbitrary reply never turns its first sentence into an asserted result");
            Assert(pages[0].Contains("语音") || pages[0].Contains("会话"), "long reply points to full content");
        }
        var unknownResult = Reply(idle, "未确认 DSH 执行结果。" + longText, "known uncertain response", true);
        Assert(unknownResult.Count == 1 && unknownResult[0].Contains("未确认"), "known local uncertainty stays uncertainty");
        var readResult = idle with { State = "completed", FinalText = "已记录：" + longText + "但实际保存失败" };
        var readResultPages = Reply(readResult, readResult.FinalText, "reading arbitrary task result through local route", true);
        Assert(readResultPages.Count == 1 && !readResultPages[0].Contains("答案已记录")
            && readResultPages.SequenceEqual(DshTaskSubtitleFormatter.ForTask(readResult)),
            "reading full result uses task summary rather than treating agent prefix as local acknowledgement");
        Assert(DshTaskSubtitleFormatter.ForReply(idle, " \r\n ").Count == 0, "empty reply does not send a blank screen message");

        var single = new DshTaskQuestion("theme", "配色方案", "请选择备忘录界面的主题配色方案", [
            new("浅色纸张", "白天使用"), new("深色夜间", "夜间使用"), new(longText + "🐈‍⬛", "长说明")]);
        var multi = new DshTaskQuestion("features", "功能选择", "请选择需要实现的功能", [
            new("搜索", "搜索正文"), new("置顶", "固定重要事项"), new("颜色标签", "按色分类")], true);
        var questionRequest = new DshTaskInteraction("request-1", "question", "ask_question", "", [single, multi]);
        var asking = idle with { State = "waitingForInput", PendingInteractions = [questionRequest], IsMonitoring = true,
            Detail = DshSpokenInteraction.BuildQuestionPrompt(single, 0, 2) };
        var firstPages = Task(asking, "single-choice question");
        Assert(firstPages.Any(page => page.Contains("1/2") && page.Contains("配色")), "question page states its number and subject");
        foreach (var ordinal in Enumerable.Range(1, single.Options.Count))
            Assert(firstPages.Any(page => page.StartsWith($"选项{ordinal}：", StringComparison.Ordinal)), "each choice retains self-contained option number " + ordinal);
        Assert(firstPages.Any(page => page.Contains("单选")), "single-choice instruction is retained");
        Assert(firstPages.Single(page => page.StartsWith("选项3：", StringComparison.Ordinal)).EndsWith("…"), "oversized option is explicitly ellipsized rather than split across anonymous pages");
        var spokenPages = Reply(asking, asking.Detail, "question spoken reply", true);
        Assert(spokenPages.SequenceEqual(firstPages), "reply uses same structured choices as task cue");
        var untrustedQuestion = Reply(asking, asking.Detail, "agent repeating a question");
        Assert(untrustedQuestion.Count == 1 && untrustedQuestion[0].Contains("会话"), "untrusted agent repetition cannot reactivate structured choices");

        var second = asking with { VoiceInteractionId = questionRequest.Id,
            VoiceAnswers = new Dictionary<string, string> { [single.Id] = "浅色纸张" },
            Detail = "已记录：浅色纸张。" + DshSpokenInteraction.BuildQuestionPrompt(multi, 1, 2) };
        var multiPages = Reply(second, second.Detail, "multiple-choice next question", true);
        Assert(multiPages.Any(page => page.Contains("2/2")), "answered first question advances to numbered second question");
        Assert(multiPages.Any(page => page.Contains("多选") && page.Contains("一和三")), "multi-choice instruction preserves how to combine choices");
        Assert(multiPages.Any(page => page.StartsWith("选项3：", StringComparison.Ordinal) && page.Contains("颜色标签")), "third multi-choice label stays associated with option 3");

        var refocus = asking with { Detail = DshSpokenInteraction.BuildQuestionPrompt(multi, 1, 2) };
        Assert(Task(refocus, "explicit focus").Any(page => page.Contains("2/2") && page.Contains("功能")), "explicit edit focus wins over earliest unanswered question");
        var stale = asking with { VoiceInteractionId = "old-request", VoiceAnswers = new Dictionary<string, string> { [single.Id] = "浅色纸张" } };
        Assert(Task(stale, "old request answers").Any(page => page.Contains("1/2")), "answers from old request cannot skip current question");

        var review = second with { VoiceAnswers = new Dictionary<string, string> { [single.Id] = "深色夜间", [multi.Id] = "搜索 | 颜色标签" },
            Detail = "请核对答案。第1题：深色夜间；第2题：搜索 | 颜色标签。说确认提交发送；说修改上一题或重新回答可以修改。" };
        var reviewPages = Reply(review, review.Detail, "answer review", true);
        Assert(reviewPages.Any(page => page.StartsWith("答1：", StringComparison.Ordinal) && page.Contains("深色夜间")), "review identifies first answer by question number");
        Assert(reviewPages.Any(page => page.StartsWith("答2：", StringComparison.Ordinal) && page.Contains("搜索") && page.Contains("颜色标签")), "review preserves both selected options and question number");
        Assert(reviewPages.Any(page => page.Contains("确认提交")), "review explicitly requires confirmation");
        var longReview = review with { VoiceAnswers = new Dictionary<string, string> { [single.Id] = longText, [multi.Id] = "搜索 | 颜色标签" } };
        Assert(Task(longReview, "long answer review").Any(page => page.StartsWith("答1：", StringComparison.Ordinal) && page.EndsWith("…")), "long review answer keeps number and signals omitted detail");

        var free = new DshTaskQuestion("body", "", "请说明具体需要执行的任务内容", []);
        var freeTask = asking with { PendingInteractions = [questionRequest with { Questions = [free] }], Detail = "" };
        Assert(Task(freeTask, "free-text question").Any(page => page.Contains("直接说答案")), "free-text question gives actionable spoken response instruction");
        Task(asking with { PendingInteractions = [questionRequest with { Questions = [] }] }, "empty question schema");

        var approval = new DshTaskInteraction("permission-1", "approval", "write_file", longText, []);
        var oneApproval = Task(asking with { PendingInteractions = [approval] }, "one permission");
        Assert(oneApproval.Any(page => page.Contains("write_file")) && oneApproval.Any(page => page.Contains("听完详情") && page.Contains("拒绝")), "one approval names tool and prompts decision only after hearing details");
        var manyApproval = Task(asking with { PendingInteractions = [approval, approval with { Id = "permission-2", ToolName = "run_command" }] }, "multiple permissions");
        Assert(manyApproval.Any(page => page.Contains("2项")) && manyApproval.Any(page => page.Contains("选择授权第几项")), "multiple approvals first ask which request to review");
        Assert(manyApproval.All(page => !page.Contains("批准") && !Regex.IsMatch(page, @"(?:第?一|第?1)[项个]")), "multiple approvals never suggest authorizing the first request");
        var secondApproval = approval with { Id = "permission-2", Reason = "写入备忘录正式目录，需要用户确认覆盖范围" };
        var selectedApproval = asking with { PendingInteractions = [approval, secondApproval],
            Detail = $"需要你授权。工具：{secondApproval.ToolName}。请求内容：{secondApproval.Reason}。说批准本次或拒绝本次；说重听可再听一遍。" };
        var selectedPages = Reply(selectedApproval, selectedApproval.Detail, "second permission with same tool", true);
        Assert(selectedPages.Any(page => page.Contains("批准本次") && page.Contains("拒绝本次")), "selected second approval proceeds to one-request decision");
        Assert(selectedPages.All(page => !page.Contains("选择授权")), "selected approval does not loop back to list even when tool names match");
        var unmatchedApproval = selectedApproval with { Detail = "需要你授权。工具：write_file。请求内容：已经失效的操作。说批准本次或拒绝本次；说重听可再听一遍。" };
        Assert(Task(unmatchedApproval, "stale approval focus").Any(page => page.Contains("选择授权")), "stale reason cannot focus another request solely by matching its tool");

        var tasks = Reply(idle, "共2个任务。第1项：备忘录前端；第2项：" + new string('中', 90) + "。说切换任务到第几项。", "task selection list", true);
        Assert(tasks.Any(page => page.StartsWith("任务1：", StringComparison.Ordinal)) && tasks.Any(page => page.StartsWith("任务2：", StringComparison.Ordinal)), "task list preserves stable numbered entries");

        foreach (var boundary in new[]
        {
            new string('a', 55), new string('中', 18) + "a", new string('中', 17) + "😀", new string('a', 51) + "😀"
        })
        {
            Assert(Encoding.UTF8.GetByteCount(boundary) == 55, "fixture is exactly 55 bytes");
            var fit = DshTaskSubtitleFormatter.Fit(boundary);
            Valid([fit], "exact boundary");
            Assert(fit == boundary, "55-byte value is retained losslessly");
        }
        foreach (var overflow in new[]
        {
            new string('a', 56), new string('中', 18) + "ab", new string('中', 16) + "😀😀", new string('a', 52) + "😀"
        })
        {
            Assert(Encoding.UTF8.GetByteCount(overflow) == 56, "fixture is exactly 56 bytes");
            var fit = DshTaskSubtitleFormatter.Fit(overflow);
            Valid([fit], "overflow boundary");
            Assert(fit.EndsWith("…", StringComparison.Ordinal) && fit.Count(c => c == '…') == 1, "overflow makes omission explicit with one Unicode ellipsis");
            Assert(overflow.StartsWith(fit[..^1], StringComparison.Ordinal), "overflow preserves original scalar prefix without corruption");
        }
        foreach (var text in new[] { new string('中', 400), string.Concat(Enumerable.Repeat("🧑🏽‍💻🐈‍⬛🇨🇳", 20)), string.Concat(Enumerable.Repeat("e\u0301", 50)) })
            Valid([DshTaskSubtitleFormatter.Fit(text)], "long Unicode labels");
    }
}
