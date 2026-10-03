using HaloPixelToolBox.Models;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

public sealed partial class DshTaskService
{
    private readonly SemaphoreSlim voiceRouteGate = new(1, 1);
    private DshTaskInteraction? voiceApprovalFocus;
    private string voiceApprovalSession = string.Empty;
    private string voiceApprovalScope = string.Empty;
    private long voiceAnswerRevision;
    private string voiceQuestionFocus = string.Empty;

    private static bool IsRepeat(string text) => text is "重听" or "重听问题" or "重复问题" or "再说一遍" or "重复一遍" or "有哪些选项" or "读出选项" or "当前问题" or "继续回答"
        or "问题详情" or "选项详情" or "完整问题";
    private static bool IsInteractionCommand(string text) => IsRepeat(text) || text is "上一题" or "修改上一题" or "重选" or "重新选择"
        or "重新回答" or "取消回答" or "取消选择" or "清空答案" or "确认提交" or "提交答案" or "确认答案" or "检查答案" or "我选了哪些"
        || Regex.IsMatch(text, @"^(?:(?:修改|重答)第?.+题|(?:加选|加上|取消选择|去掉).+)$");

    private DshTaskQuestion? NextVoiceQuestion(DshTaskInteraction request)
        => request.Questions.FirstOrDefault(q => q.Id == voiceQuestionFocus && !voiceAnswers.ContainsKey(q.Id))
            ?? request.Questions.FirstOrDefault(q => !voiceAnswers.ContainsKey(q.Id));

    private string QuestionPrompt(DshTaskInteraction request, DshTaskQuestion? question)
    {
        if (question is not null)
            return DshSpokenInteraction.BuildQuestionPrompt(question, request.Questions.ToList().FindIndex(q => q.Id == question.Id), request.Questions.Count);
        return AnswerReview(request);
    }

    private string AnswerReview(DshTaskInteraction request)
    {
        if (request.Questions.Count == 1 && request.Questions[0] is { MultiSelect: false } single
            && single.Options.Count > 0 && voiceAnswers.TryGetValue(single.Id, out var answer))
        {
            var index = single.Options.ToList().FindIndex(option => option.Label.Trim() == answer);
            if (index >= 0)
                return $"已选第{index + 1}项：{DshSpokenInteraction.SummarizeLabel(single.Options[index].Label)}。说确认提交，或说其他序号改选。";
        }
        return "请核对答案。" + string.Join("；", request.Questions.Select((q, i) => $"第{i + 1}题：{voiceAnswers.GetValueOrDefault(q.Id, "尚未回答")}"))
            + "。说确认提交；可说修改上一题。";
    }

    private string ApprovalPrompt(IReadOnlyList<DshTaskInteraction> interactions)
    {
        var approvals = interactions.Where(p => p.Type == "approval").ToArray();
        var focused = approvals.Length == 1 ? approvals[0] : approvals.FirstOrDefault(p => voiceApprovalSession == Current.SessionId
            && voiceApprovalScope == scopeFactory() && DshTaskInteractionIdentity.Matches(voiceApprovalFocus, p));
        if (focused is not null)
            return $"需要你授权。工具：{focused.ToolName}。请求内容：{focused.Reason}。说批准本次或拒绝本次；说重听可再听一遍。";
        return $"有{approvals.Length}项授权。" + string.Join("；", approvals.Select((p, i) => $"第{i + 1}项：{p.ToolName}，{p.Reason}"))
            + "。请说选择授权第一项等，听完具体内容再决定。";
    }

    private static bool VoiceContextMatches(DshTaskSnapshot expected, DshTaskSnapshot actual)
        => expected.SessionId == actual.SessionId && expected.IsMonitoring == actual.IsMonitoring
            && expected.VoiceAnswerRevision == actual.VoiceAnswerRevision
            && expected.PendingInteractions.Count == actual.PendingInteractions.Count
            && expected.PendingInteractions.All(item => actual.PendingInteractions.Any(p => DshTaskInteractionIdentity.Matches(item, p)));

    private async Task<DshTaskVoiceResult> RouteInteractionVoiceAsync(string text, DshTaskSnapshot? expectedContext, CancellationToken token)
    {
        var observed = Current;
        var expectedScope = scopeFactory();
        var expected = observed.PendingInteractions.FirstOrDefault(p => p.Type == "approval")
            ?? observed.PendingInteractions.FirstOrDefault(p => p.Type == "question");
        expected = expected is null ? null : CaptureInteraction(expected);
        DshTaskInteraction? submission = null;
        Dictionary<string, string>? answers = null;
        string? outcome = null;
        await actions.WaitAsync(token);
        try
        {
            EnsureActive(observed.SessionId);
            if (expectedContext is not null && !VoiceContextMatches(expectedContext, Current))
                return new(true, "问题已更新，未使用刚才的回答。" + Current.Detail) { ListenForReply = Current.NeedsAttention };
            if (expectedScope != scopeFactory() || expected is null || !Current.PendingInteractions.Any(p => DshTaskInteractionIdentity.Matches(expected, p)))
                return new(true, "问题已更新，未使用刚才的回答。请听新问题后回答。") { ListenForReply = Current.NeedsAttention };
            var normalized = DshSpokenInteraction.NormalizeCommand(text);
            var approvals = Current.PendingInteractions.Where(p => p.Type == "approval").ToArray();
            if (approvals.Length > 0)
            {
                var selection = Regex.Match(normalized, @"^(?:选择|查看|处理)授权(?:请求)?(?<option>.+)$");
                if (selection.Success)
                {
                    var choices = new DshTaskQuestion("approval", "授权请求", "选择需要处理的授权请求", approvals.Select((p, i) => new DshTaskOption($"请求{i + 1}：{p.ToolName}", p.Reason)).ToArray());
                    var parsed = DshSpokenInteraction.ParseAnswer(choices, selection.Groups["option"].Value);
                    var index = choices.Options.ToList().FindIndex(o => o.Label == parsed.Answer);
                    if (!parsed.Success || index < 0) return InteractionReply("未确认要处理哪项授权。" + ApprovalPrompt(Current.PendingInteractions));
                    voiceApprovalFocus = CaptureInteraction(approvals[index]);
                    voiceApprovalSession = Current.SessionId; voiceApprovalScope = expectedScope;
                    return InteractionReply(ApprovalPrompt(Current.PendingInteractions));
                }
                if (voiceApprovalFocus is not null && voiceApprovalSession == Current.SessionId && voiceApprovalScope == expectedScope
                    && !approvals.Any(p => DshTaskInteractionIdentity.Matches(voiceApprovalFocus, p)))
                {
                    voiceApprovalFocus = approvals.Length == 1 ? CaptureInteraction(approvals[0]) : null;
                    return InteractionReply("原授权请求已变化，刚才的决定未提交。" + ApprovalPrompt(Current.PendingInteractions));
                }
                var focus = approvals.Length == 1 ? approvals[0] : approvals.FirstOrDefault(p => voiceApprovalSession == Current.SessionId
                    && voiceApprovalScope == expectedScope && DshTaskInteractionIdentity.Matches(voiceApprovalFocus, p));
                if (focus is not null && normalized is "同意" or "批准" or "允许本次" or "批准本次" or "同意本次")
                    outcome = "allowed-once";
                else if (focus is not null && normalized is "拒绝" or "不同意" or "拒绝本次" or "不允许")
                    outcome = "rejected";
                if (outcome is null) return InteractionReply(ApprovalPrompt(Current.PendingInteractions));
                submission = CaptureInteraction(focus!);
            }
            else
            {
                var question = expected;
                if (questionInteractionId != question.Id || !DshTaskInteractionIdentity.Matches(questionInteractionSchema, question))
                {
                    voiceAnswers.Clear(); questionInteractionId = question.Id;
                    voiceQuestionFocus = string.Empty;
                    questionInteractionSchema = CaptureInteraction(question);
                }
                var next = NextVoiceQuestion(question);
                if (normalized is "重新回答" or "取消回答" or "取消选择" or "清空答案")
                {
                    voiceAnswers.Clear();
                    voiceQuestionFocus = string.Empty;
                    return InteractionReply("已清空本次答案，任务仍在等待。" + QuestionPrompt(question, question.Questions.FirstOrDefault()));
                }
                var edit = Regex.Match(normalized, @"^(?:修改|重答)(?:第)?(?<number>[\d零一二两三四五六七八九十百]+)(?:题|个问题)$");
                if (edit.Success)
                {
                    var choices = new DshTaskQuestion("edit", "修改答案", "修改哪一题", question.Questions.Select((q, i) => new DshTaskOption($"问题{i + 1}", "")).ToArray());
                    var parsed = DshSpokenInteraction.ParseAnswer(choices, edit.Groups["number"].Value);
                    var index = choices.Options.ToList().FindIndex(o => o.Label == parsed.Answer);
                    if (!parsed.Success || index < 0) return InteractionReply("没有这道题。" + QuestionPrompt(question, next));
                    var target = question.Questions[index];
                    voiceAnswers.Remove(target.Id); voiceQuestionFocus = target.Id;
                    return InteractionReply("请重新回答。" + QuestionPrompt(question, target));
                }
                var change = Regex.Match(normalized, @"^(?<action>加选|加上|取消选择|去掉)(?<options>.+)$");
                if (change.Success)
                {
                    var target = next?.MultiSelect == true ? next : question.Questions.LastOrDefault(q => q.MultiSelect && voiceAnswers.ContainsKey(q.Id));
                    if (target is null) return InteractionReply("当前没有可增减的多选答案。" + QuestionPrompt(question, next));
                    var parsed = DshSpokenInteraction.ParseAnswer(target, change.Groups["options"].Value);
                    if (!parsed.Success) return InteractionReply(parsed.Error + "。" + QuestionPrompt(question, target));
                    var labels = parsed.Answer.Split(" | ", StringSplitOptions.None);
                    if (labels.Any(label => !target.Options.Any(o => o.Label.Trim() == label)))
                        return InteractionReply("请使用这道多选题的序号或选项名称。");
                    var selected = voiceAnswers.GetValueOrDefault(target.Id, "").Split(" | ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    if (change.Groups["action"].Value is "加选" or "加上") selected.UnionWith(labels); else selected.ExceptWith(labels);
                    var updated = string.Join(" | ", target.Options.Where(o => selected.Contains(o.Label.Trim())).Select(o => o.Label.Trim()));
                    if (updated.Length == 0) { voiceAnswers.Remove(target.Id); voiceQuestionFocus = target.Id; }
                    else voiceAnswers[target.Id] = updated;
                    return InteractionReply((updated.Length == 0 ? "已清空该题选择。" : "已修改为：" + updated + "。") + QuestionPrompt(question, NextVoiceQuestion(question)));
                }
                if (normalized is "上一题" or "修改上一题" or "重选" or "重新选择")
                {
                    var previous = question.Questions.LastOrDefault(q => voiceAnswers.ContainsKey(q.Id));
                    if (previous is not null) { voiceAnswers.Remove(previous.Id); voiceQuestionFocus = previous.Id; }
                    return InteractionReply("请重新回答。" + QuestionPrompt(question, previous ?? next));
                }
                if (normalized is "问题详情" or "选项详情" or "完整问题")
                {
                    var focused = next ?? question.Questions.LastOrDefault();
                    return InteractionReply(focused is null ? AnswerReview(question)
                        : focused.Question + "。" + string.Join("；", focused.Options.Select((option, index) =>
                            $"{index + 1}：{option.Label}。{option.Description}")) + "。请说第几项。");
                }
                if (IsRepeat(normalized) || normalized is "检查答案" or "我选了哪些")
                    return InteractionReply(normalized is "检查答案" or "我选了哪些" ? AnswerReview(question)
                        : QuestionPrompt(question, next ?? question.Questions.LastOrDefault()));
                if (normalized is "确认提交" or "提交答案" or "确认答案")
                {
                    if (next is not null) return InteractionReply("还有问题没有回答。" + QuestionPrompt(question, next));
                    submission = CaptureInteraction(question);
                    answers = new Dictionary<string, string>(voiceAnswers);
                }
                else
                {
                    if (next is null)
                    {
                        // A single completed choice still owns the answer window until
                        // confirmation. Saying another ordinal revises that draft; it
                        // must not silently ignore the new choice and repeat the old one.
                        if (question.Questions.Count == 1 && question.Questions[0] is { MultiSelect: false } single
                            && single.Options.Count > 0)
                        {
                            var replacement = DshSpokenInteraction.ParseAnswer(single, text);
                            if (replacement.Success)
                            {
                                voiceAnswers[single.Id] = replacement.Answer;
                                return InteractionReply(AnswerReview(question));
                            }
                            return InteractionReply("未改选。" + replacement.Error + "。" + AnswerReview(question));
                        }
                        return InteractionReply(AnswerReview(question));
                    }
                    if (normalized is "跳过此题" or "跳过这个问题" or "下一题")
                        return InteractionReply("这道题尚未回答，不能直接跳过。" + QuestionPrompt(question, next));
                    var parsed = DshSpokenInteraction.ParseAnswer(next, text);
                    if (!parsed.Success) return InteractionReply(parsed.Error + "。" + QuestionPrompt(question, next));
                    voiceAnswers[next.Id] = parsed.Answer;
                    voiceQuestionFocus = string.Empty;
                    var following = NextVoiceQuestion(question);
                    return InteractionReply((following is null ? "" : "已记录：" + parsed.Answer + "。") + QuestionPrompt(question, following));
                }
            }
        }
        finally { actions.Release(); }
        // The response method rechecks session, scope and the whole captured schema after queueing.
        await RespondAsync(submission, submission!.Type, outcome, answers, observed.SessionId, expectedScope, token);
        return new(true, outcome is null ? "本次问题的回答已提交，任务将继续。" : outcome == "allowed-once" ? "已批准本次请求。" : "已拒绝本次请求。");
    }

    private DshTaskVoiceResult InteractionReply(string text)
    {
        Publish(WithVoiceDraft(Current with { Detail = text }));
        return new(true, text) { ListenForReply = true };
    }

    private DshTaskSnapshot WithVoiceDraft(DshTaskSnapshot snapshot)
    {
        var active = snapshot.PendingInteractions.Any(p => p.Type == "question"
            && DshTaskInteractionIdentity.Matches(questionInteractionSchema, p));
        var id = active ? questionInteractionId : string.Empty;
        var answers = active ? new Dictionary<string, string>(voiceAnswers) : new Dictionary<string, string>();
        var changed = Current.VoiceInteractionId != id || Current.VoiceAnswers.Count != answers.Count
            || answers.Any(pair => !Current.VoiceAnswers.TryGetValue(pair.Key, out var value) || value != pair.Value);
        if (changed) voiceAnswerRevision++;
        return snapshot with { VoiceInteractionId = id, VoiceAnswers = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(answers),
            VoiceAnswerRevision = voiceAnswerRevision };
    }
}
