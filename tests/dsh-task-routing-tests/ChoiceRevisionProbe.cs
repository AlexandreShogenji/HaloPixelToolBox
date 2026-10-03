using System.Diagnostics;
using HaloPixelToolBox.Models;

internal static class ChoiceRevisionProbe
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("spoken color option can be revised before one explicit submission", async () =>
        {
            using var f = new TaskCase();
            await f.Start();
            var unbound = await f.Service.RouteVoiceAsync("第一项");
            ProbeAssert.Check(unbound.Handled && f.Client.Prompts.Count == 1,
                "a choice without a live question leaked into the ordinary task");
            var question = new DshTaskInteraction("color-question", "question", "ask_question", "", [
                new("colorscheme", "确认目标", "你说的「换个颜色方案」是指哪一种？", [
                    new("已保存的配色方案（推荐）", "应用已保存的双色配色。"),
                    new("换一个氛围灯效", "只更换动画。"), new("随机挑一个配色", "随机选择预设。")])]);
            f.Client.SetRemote("waitingInput", "colors", [question]);
            var timer = Stopwatch.StartNew();
            while (!f.Service.Current.NeedsAttention)
            {
                if (timer.ElapsedMilliseconds > 2000) throw new TimeoutException();
                await Task.Delay(5);
            }
            var first = await f.Service.RouteVoiceAsync("第一项。", f.Service.Current);
            ProbeAssert.Check(first.ListenForReply && first.Message.Contains("已选第1项")
                && f.Service.Current.VoiceAnswers["colorscheme"] == question.Questions[0].Options[0].Label,
                "first ordinal was not recorded in the bound question");
            var firstRevision = f.Service.Current.VoiceAnswerRevision;
            var revised = await f.Service.RouteVoiceAsync("第三项。", f.Service.Current);
            ProbeAssert.Check(revised.ListenForReply && revised.Message.Contains("已选第3项")
                && f.Service.Current.VoiceAnswers["colorscheme"] == "随机挑一个配色"
                && f.Service.Current.VoiceAnswerRevision > firstRevision,
                "another ordinal repeated the old review instead of revising it");
            ProbeAssert.Check(f.Client.Responses.Count == 0 && f.Client.Prompts.Count == 1,
                "a draft choice performed work without confirmation");
            var repeat = await f.Service.RouteVoiceAsync("有哪些选项", f.Service.Current);
            ProbeAssert.Check(repeat.Message.Contains("1：已保存的配色方案") && repeat.Message.Contains("3：随机挑一个配色")
                && f.Service.Current.VoiceAnswers["colorscheme"] == "随机挑一个配色",
                "repeating options after a choice discarded the draft or read only the review");
            var details = await f.Service.RouteVoiceAsync("选项详情", f.Service.Current);
            ProbeAssert.Check(details.Message.Contains("应用已保存的双色配色")
                && f.Service.Current.VoiceAnswers["colorscheme"] == "随机挑一个配色" && f.Client.Responses.Count == 0,
                "explicit details did not retain full wording and the unsubmitted choice");
            await f.Service.RouteVoiceAsync("不要第一项", f.Service.Current);
            ProbeAssert.Check(f.Service.Current.VoiceAnswers["colorscheme"] == "随机挑一个配色"
                && f.Client.Responses.Count == 0, "uncertain revision overwrote the selected answer");
            await f.Service.RouteVoiceAsync("确认提交", f.Service.Current);
            ProbeAssert.Check(f.Client.Responses.Count == 1
                && f.Client.Responses[0].Interaction == "color-question"
                && f.Client.Responses[0].Answers!["colorscheme"] == "随机挑一个配色",
                "confirmation did not submit the last selected exact label to its question");
        });
    }
}
