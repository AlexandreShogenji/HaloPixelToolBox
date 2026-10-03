using HaloPixelToolBox.Models;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

public sealed partial class DshTaskService
{
    private DshSessionSummary[] voiceSessionChoices = [];
    private string voiceSessionScope = string.Empty;
    private int voiceSessionPage;

    private async Task<DshTaskVoiceResult?> RouteTaskSelectionAsync(string text, CancellationToken token)
    {
        var normalized = DshTaskVoiceParser.Normalize(text);
        var selection = Regex.Match(text.Trim(), @"^(?:请)?(?:切换|选择|继续)(?:DSH\s*)?任务(?:到|为|：|:)?\s*(?<name>.+?)[。！!]*$", RegexOptions.IgnoreCase);
        var list = normalized is "任务列表" or "列出任务" or "有哪些任务" or "下一页任务" or "上一页任务";
        if (!list && !selection.Success) return null;
        var scope = scopeFactory();
        if (list)
        {
            if (normalized is "任务列表" or "列出任务" or "有哪些任务" || voiceSessionScope != scope)
            {
                if (!client.Current.IsConnected) await client.ConnectAsync(token);
                else await client.RefreshAsync(token);
                if (scope != scopeFactory()) return new(true, "DSH 配置已改变，请重新查询任务列表。");
                voiceSessionChoices = client.Current.Sessions.Where(s => !s.IsDeviceControl && !s.IsArchived)
                    .OrderByDescending(s => s.UpdatedAt).ToArray();
                voiceSessionScope = scope; voiceSessionPage = 0;
            }
            else if (normalized == "下一页任务") voiceSessionPage = Math.Min(voiceSessionPage + 1, Math.Max(0, (voiceSessionChoices.Length - 1) / 5));
            else if (normalized == "上一页任务") voiceSessionPage = Math.Max(0, voiceSessionPage - 1);
            if (voiceSessionChoices.Length == 0) return new(true, "没有可选的普通任务。可以说新建 DSH 任务。") { ListenForReply = true };
            var page = voiceSessionChoices.Skip(voiceSessionPage * 5).Take(5);
            return new(true, $"共{voiceSessionChoices.Length}个任务。" + string.Join("；", page.Select((s, i) => $"第{voiceSessionPage * 5 + i + 1}项：{s.Title}，{s.StatusText}"))
                + "。说切换任务到第几项或任务名称；也可以说下一页任务。") { ListenForReply = true };
        }
        var name = selection.Groups["name"].Value.Trim();
        var choices = voiceSessionScope == scope ? voiceSessionChoices : [];
        if (choices.Length == 0)
        {
            if (Regex.IsMatch(DshTaskVoiceParser.Normalize(name), @"^(?:第)?[\d一二两三四五六七八九十]+(?:个|项)?$"))
                return new(true, "请先说任务列表，听到对应编号后再选择。") { ListenForReply = true };
            choices = client.Current.Sessions.Where(s => !s.IsDeviceControl && !s.IsArchived).ToArray();
        }
        var question = new DshTaskQuestion("session", "任务", "要切换到哪个任务", choices.Select((s, i) => new DshTaskOption($"{s.Title}（任务{i + 1}）", "")).ToArray());
        var answer = DshSpokenInteraction.ParseAnswer(question, name);
        var index = question.Options.ToList().FindIndex(o => o.Label == answer.Answer);
        if (!answer.Success || index < 0) return new(true, "未确定任务。请说任务列表后按编号选择，或说完整任务名称。") { ListenForReply = true };
        var target = choices[index];
        if (scope != scopeFactory() || !client.Current.Sessions.Any(s => s.Id == target.Id && !s.IsArchived && !s.IsDeviceControl))
            return new(true, "任务列表已变化，请重新查询后选择。") { ListenForReply = true };
        await MonitorAsync(target, token);
        return new(true, "已切换到任务：" + target.Title + "。" + Current.StatusText + "。" + Current.Detail) { ListenForReply = Current.NeedsAttention || Current.State == "idle" };
    }
}
