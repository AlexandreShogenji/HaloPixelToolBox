using HaloPixelToolBox.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

/// <summary>Screen summaries are independent of full spoken prompts and conversation history.</summary>
internal static class DshTaskSubtitleFormatter
{
    public const int MaxUtf8Bytes = 55;

    public static IReadOnlyList<string> ForTask(DshTaskSnapshot snapshot)
        => snapshot.NeedsAttention ? InteractionPages(snapshot) : [TaskSummary(snapshot)];

    public static IReadOnlyList<string> ForReply(DshTaskSnapshot snapshot, string text, bool isTaskReply = false)
    {
        text = Clean(text);
        if (text.Length == 0) return [];
        if (Fits(text)) return [text];
        // “Read the result” is locally routed but its body is still arbitrary agent prose.
        if (isTaskReply && text == Clean(snapshot.FinalText)) return ForTask(snapshot);
        // The structured request supplies stable option numbers; never split a spoken
        // paragraph mid-option or shorten a permission request into an apparent approval.
        if (isTaskReply && snapshot.NeedsAttention && Clean(snapshot.Detail) == text)
        {
            var pages = InteractionPages(snapshot);
            var notice = ReplyNotice(text);
            return notice is null ? pages : new[] { notice }.Concat(pages).ToArray();
        }
        if (isTaskReply && text.StartsWith("共", StringComparison.Ordinal) && text.Contains("个任务。"))
        {
            var entries = Regex.Matches(text, @"第(?<n>\d+)项：(?<title>[^；。]+)");
            if (entries.Count > 0)
                return entries.Select(entry => Label($"任务{entry.Groups["n"].Value}：", entry.Groups["title"].Value))
                    .Append("说切换任务到第几项").ToArray();
        }
        var summary = isTaskReply ? ReplyNotice(text) : null;
        return [summary ?? "回复较长；请听语音或查看会话"];
    }

    private static string TaskSummary(DshTaskSnapshot snapshot)
    {
        var detail = Clean(snapshot.Detail);
        return snapshot.State switch
        {
            "running" => detail.StartsWith("正在执行：", StringComparison.Ordinal) ? Label("执行中：", detail[5..])
                : detail.StartsWith("执行失败：", StringComparison.Ordinal) ? Label("工具失败：", detail[5..])
                : detail.StartsWith("已完成：", StringComparison.Ordinal) ? Label("继续执行；已完成：", detail[4..])
                : "任务执行中；可查询任务状态",
            "completed" => WithDetail("任务完成", Clean(snapshot.FinalText), "任务完成；说朗读任务结果"),
            "failed" => "任务失败；请查看会话原因",
            "cancelled" => "本轮已取消；可继续任务",
            "awaitingPrompt" => "任务已新建；请说任务内容",
            "idle" => "任务空闲；请说任务内容",
            "disconnected" => "监控已断开；请检查DSH连接",
            _ => "结果未确认；请查看会话"
        };
    }

    private static IReadOnlyList<string> InteractionPages(DshTaskSnapshot snapshot)
    {
        var approvals = snapshot.PendingInteractions.Where(p => p.Type == "approval").ToArray();
        if (approvals.Length > 0)
        {
            // ApprovalPrompt carries the selected request while several remain pending.
            // Match its full local template, not just a tool name occurring in a reason.
            var focusedApproval = approvals.Length == 1 ? approvals[0] : approvals.FirstOrDefault(p =>
                snapshot.Detail.StartsWith($"需要你授权。工具：{p.ToolName}。请求内容：{p.Reason}。说批准本次或拒绝本次", StringComparison.Ordinal));
            if (focusedApproval is not null)
                return [Label("待授权：", focusedApproval.ToolName), "听完详情说批准本次或拒绝本次"];
            return new[] { Label("待授权：", $"{approvals.Length}项") }
                .Concat(approvals.Select((p, i) => Label($"授权{i + 1}：", p.ToolName)))
                .Append("请说选择授权第几项").ToArray();
        }
        var request = snapshot.PendingInteractions.FirstOrDefault(p => p.Type == "question");
        if (request is null || request.Questions.Count == 0) return ["任务等待回答；请听语音提示"];
        var answers = snapshot.VoiceInteractionId == request.Id ? snapshot.VoiceAnswers : new Dictionary<string, string>();
        var index = request.Questions.ToList().FindIndex(q => !answers.ContainsKey(q.Id));
        // The service may be editing a later question while an earlier one is unanswered.
        var spokenIndex = Regex.Match(snapshot.Detail, @"第\s*(?<n>\d+)/\d+\s*题：");
        if (spokenIndex.Success && int.TryParse(spokenIndex.Groups["n"].Value, out var focused)
            && focused > 0 && focused <= request.Questions.Count) index = focused - 1;
        if (index < 0 || snapshot.Detail.StartsWith("请核对答案", StringComparison.Ordinal))
        {
            if (request.Questions.Count == 1 && request.Questions[0] is { MultiSelect: false } single
                && answers.TryGetValue(single.Id, out var answer))
            {
                // The retained draft, rather than words in a failed recognition,
                // owns the displayed choice until an explicit confirmation.
                var selected = single.Options.ToList().FindIndex(option => option.Label.Trim() == answer);
                if (selected >= 0)
                    return [Label($"已选第{selected + 1}项：", answer), "尚未提交；说确认或改选序号"];
            }
            return request.Questions.Select((q, i) => Label($"答{i + 1}：", answers.GetValueOrDefault(q.Id, "尚未回答")))
                .Append("尚未提交；说确认或修改第几题").ToArray();
        }
        var question = request.Questions[index];
        var title = string.IsNullOrWhiteSpace(question.Header) ? question.Question : question.Header;
        var pages = new List<string> { Label($"题{index + 1}/{request.Questions.Count}：", title) };
        for (var i = 0; i < question.Options.Count; i++)
            pages.Add(Label($"选项{i + 1}：", question.Options[i].Label));
        pages.Add(question.Options.Count == 0 ? "请直接说答案；可说重听问题"
            : question.MultiSelect ? "多选：说一和三；可重听问题" : "单选：说第一项；可重听问题");
        return pages;
    }

    private static string? ReplyNotice(string text)
    {
        // Only summarize known local response prefixes. Arbitrary agent text can contain
        // later qualifications/negations, so its first sentence is not a reliable summary.
        (string Prefix, string Summary)[] notices =
        [
            ("听到的是“画场景”", "切换场景还是绘制？请说明"),
            ("问题已更新", "问题已更新；旧回答未提交"),
            ("问题已经变化", "问题已变化；旧回答未提交"),
            ("原授权请求已变化", "授权已变化；旧决定未提交"),
            ("未确认", "结果未确认；请查看会话"),
            ("没有找到对应选项", "未找到选项；请重新选择"),
            ("没有这道题", "没有这道题；请重新选择"),
            ("这句话还不能确定", "选择未确认；请说选项序号"),
            ("还有问题没有回答", "还有未答题；请继续回答"),
            ("已清空本次答案", "答案已清空；请重新回答"),
            ("已清空该题选择", "本题已清空；请重新选择"),
            ("已记录：", "答案已记录；请继续回答"),
            ("已修改为：", "答案已修改；请听提示核对"),
            ("请重新回答", "请重新回答；可说重听问题"),
            ("本次问题的回答已提交", "答案已提交；等待任务继续"),
            ("已切换到任务：", "已切换任务；可查询任务状态"),
            ("任务已提交", "任务已提交；正在监控状态"),
            ("未发送指令", "未发送；请说明设备或任务"),
            ("这次指令未能确认完成", "结果未确认；请查看会话")
        ];
        return notices.FirstOrDefault(item => text.StartsWith(item.Prefix, StringComparison.Ordinal)).Summary;
    }

    private static string WithDetail(string label, string detail, string fallback)
        => detail.Length > 0 && Fits(label + "：" + detail) ? label + "：" + detail : fallback;

    // Only labels may be elided; state and answering instructions use complete short sentences.
    // An ellipsis explicitly signals that speech/history has the rest of a label.
    private static string Label(string prefix, string value) => Fit(prefix + Clean(value));
    public static string Fit(string text)
    {
        text = Clean(text);
        if (Fits(text)) return text;
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxUtf8Bytes - 3) break;
            result.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        return result + "…";
    }
    private static bool Fits(string text) => Encoding.UTF8.GetByteCount(text) <= MaxUtf8Bytes;
    private static string Clean(string text) => Regex.Replace(Regex.Replace(text ?? "", @"[`*#]+", ""), @"\s+", " ").Trim();
}
