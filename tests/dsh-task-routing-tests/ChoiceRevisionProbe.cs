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

        await test("misrecognized confirmation keeps the fifth audio choice and allows revision before short confirmation", async () =>
        {
            using var f = new TaskCase();
            var question = AudioQuestion();
            await WaitForQuestion(f, question);
            await f.Service.RouteVoiceAsync("第五项", f.Service.Current);
            var draftRevision = f.Service.Current.VoiceAnswerRevision;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var reply = await f.Service.RouteVoiceAsync("理想提交。", f.Service.Current);
                ProbeAssert.Check(reply.Handled && reply.ListenForReply && reply.Message.Contains("已保留第5项")
                    && reply.Message.Contains("尚未提交") && reply.Message.Contains("请说确认")
                    && !reply.Message.Contains("没有找到对应选项") && !reply.Message.Contains("未找到选项"),
                    "a failed confirmation was reported as a missing option instead of a retained draft");
                ProbeAssert.Check(f.Service.Current.VoiceAnswers["audio_next_step"] == question.Questions[0].Options[4].Label
                    && f.Service.Current.VoiceAnswerRevision == draftRevision && f.Client.Responses.Count == 0
                    && f.Client.Prompts.Count == 1, "a misrecognized confirmation mutated or submitted the pending draft");
            }
            var revised = await f.Service.RouteVoiceAsync("第三项", f.Service.Current);
            ProbeAssert.Check(revised.ListenForReply && revised.Message.Contains("已选第3项")
                && f.Service.Current.VoiceAnswers["audio_next_step"] == question.Questions[0].Options[2].Label,
                "another ordinal could not revise the retained draft");
            await f.Service.RouteVoiceAsync("确定。", f.Service.Current);
            foreach (var repeat in new[] { "确认", "确定", "确认提交", "提交答案" })
                await f.Service.RouteVoiceAsync(repeat);
            ProbeAssert.Check(f.Client.Responses.Count == 1 && f.Client.Prompts.Count == 1
                && f.Client.Responses[0].Type == "question" && f.Client.Responses[0].Outcome is null
                && f.Client.Responses[0].Answers!["audio_next_step"] == question.Questions[0].Options[2].Label,
                "short confirmation submitted the wrong choice, granted approval or repeated the task message");
        });

        await test("all explicit answer confirmations submit only complete bound drafts", async () =>
        {
            foreach (var confirmation in new[] { "确认", "确定", "确认提交", "提交答案", "确认答案" })
            {
                using var f = new TaskCase();
                var question = AudioQuestion();
                await WaitForQuestion(f, question);
                await f.Service.RouteVoiceAsync("第五项", f.Service.Current);
                await f.Service.RouteVoiceAsync(confirmation, f.Service.Current);
                ProbeAssert.Check(f.Client.Responses.Count == 1 && f.Client.Responses[0].Type == "question"
                    && f.Client.Responses[0].Outcome is null
                    && f.Client.Responses[0].Answers!["audio_next_step"] == question.Questions[0].Options[4].Label,
                    "explicit confirmation did not submit exactly the current complete draft: " + confirmation);
            }
        });

        await test("questions negation and ASR guesses never confirm a selected answer", async () =>
        {
            using var f = new TaskCase();
            var question = AudioQuestion();
            await WaitForQuestion(f, question);
            await f.Service.RouteVoiceAsync("第五项", f.Service.Current);
            var draft = f.Service.Current;
            foreach (var input in new[] { "确认？", "确定?", "确认提交？", "提交答案?", "确认答案？", "不要确认", "不要提交答案", "确认吗", "确定吗", "理想提交", "确认之后先别执行" })
            {
                var reply = await f.Service.RouteVoiceAsync(input, f.Service.Current);
                ProbeAssert.Check(reply.Handled && reply.ListenForReply && reply.Message.Contains("尚未提交")
                    && !reply.Message.Contains("没有找到对应选项")
                    && f.Client.Responses.Count == 0 && f.Client.Prompts.Count == 1
                    && f.Service.Current.VoiceAnswerRevision == draft.VoiceAnswerRevision
                    && f.Service.Current.VoiceAnswers["audio_next_step"] == draft.VoiceAnswers["audio_next_step"],
                    "an uncertain confirmation submitted, altered or obscured the retained answer: " + input);
            }
        });

        await test("short confirmation never submits an empty or incomplete choice set", async () =>
        {
            using var f = new TaskCase();
            var question = AudioQuestion() with { Questions = [
                new("first", "样式", "选择样式", [new("浅色", ""), new("深色", "")]),
                new("second", "位置", "选择位置", [new("左侧", ""), new("右侧", "")])] };
            await WaitForQuestion(f, question);
            foreach (var input in new[] { "确认", "确定", "确认提交", "提交答案" })
                await f.Service.RouteVoiceAsync(input, f.Service.Current);
            ProbeAssert.Check(f.Client.Responses.Count == 0 && f.Service.Current.VoiceAnswers.Count == 0,
                "empty choice set accepted confirmation as a selected option or submission");
            await f.Service.RouteVoiceAsync("第一项", f.Service.Current);
            foreach (var input in new[] { "确认", "确定", "确认提交", "提交答案" })
                await f.Service.RouteVoiceAsync(input, f.Service.Current);
            ProbeAssert.Check(f.Client.Responses.Count == 0 && f.Service.Current.VoiceAnswers.Count == 1
                && f.Service.Current.VoiceAnswers["first"] == "浅色",
                "incomplete choice set submitted or invented its missing answer");
            await f.Service.RouteVoiceAsync("第二项", f.Service.Current);
            await f.Service.RouteVoiceAsync("确认", f.Service.Current);
            ProbeAssert.Check(f.Client.Responses.Count == 1 && f.Client.Responses[0].Answers!["first"] == "浅色"
                && f.Client.Responses[0].Answers!["second"] == "右侧", "complete multi-question draft did not accept confirmation");
        });

        await test("short confirmation words remain answer data while a literal choice or free text is unanswered", async () =>
        {
            foreach (var freeText in new[] { false, true })
            foreach (var word in new[] { "确认", "确定" })
            {
                using var f = new TaskCase();
                var question = AudioQuestion() with { Questions = [
                    new("literal", "文案", "请指定按钮文字", freeText ? [] : [new("确认", ""), new("确定", "")])] };
                await WaitForQuestion(f, question);
                var answer = await f.Service.RouteVoiceAsync(word, f.Service.Current);
                ProbeAssert.Check(answer.ListenForReply && f.Client.Responses.Count == 0
                    && f.Service.Current.VoiceAnswers["literal"] == word,
                    "short confirmation swallowed an unanswered literal label or free-text value");
                await f.Service.RouteVoiceAsync("确认", f.Service.Current);
                ProbeAssert.Check(f.Client.Responses.Count == 1 && f.Client.Responses[0].Answers!["literal"] == word,
                    "a literal confirmation word could not be submitted after it became a completed draft");
            }
        });

        await test("short question confirmation cannot approve a tool permission request", async () =>
        {
            using var f = new TaskCase();
            await f.Start();
            f.Client.SetRemote("waitingApproval", "approval", [new("permission", "approval", "exec", "write file", [])]);
            await WaitUntil(() => f.Service.Current.NeedsAttention);
            foreach (var input in new[] { "确认", "确定", "确认提交", "提交答案", "理想提交" })
            {
                var reply = await f.Service.RouteVoiceAsync(input, f.Service.Current);
                ProbeAssert.Check(reply.ListenForReply && f.Client.Responses.Count == 0 && f.Client.Prompts.Count == 1,
                    "a question confirmation was converted to tool authorization: " + input);
            }
            await f.Service.RouteVoiceAsync("批准本次", f.Service.Current);
            ProbeAssert.Check(f.Client.Responses.Count == 1 && f.Client.Responses[0].Type == "approval"
                && f.Client.Responses[0].Outcome == "allowed-once", "explicit tool approval behavior changed");
        });

        await test("short confirmation from an old microphone snapshot cannot submit a revised draft", async () =>
        {
            using var f = new TaskCase();
            await WaitForQuestion(f, AudioQuestion());
            await f.Service.RouteVoiceAsync("第五项", f.Service.Current);
            var oldRecording = f.Service.Current;
            await f.Service.RouteVoiceAsync("第三项", f.Service.Current);
            foreach (var input in new[] { "确认", "确定" })
            {
                var reply = await f.Service.RouteVoiceAsync(input, oldRecording);
                ProbeAssert.Check(reply.ListenForReply && f.Client.Responses.Count == 0
                    && f.Service.Current.VoiceAnswers["audio_next_step"] == AudioQuestion().Questions[0].Options[2].Label,
                    "an old recording submitted a subsequently revised choice");
            }
            await f.Service.RouteVoiceAsync("确认", f.Service.Current);
            ProbeAssert.Check(f.Client.Responses.Count == 1
                && f.Client.Responses[0].Answers!["audio_next_step"] == AudioQuestion().Questions[0].Options[2].Label,
                "a fresh short confirmation did not submit the revised choice");
        });

        await test("unknown choice retries stay concise and do not replay the complete question", async () =>
        {
            using var f = new TaskCase();
            var question = AudioQuestion();
            await WaitForQuestion(f, question);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var reply = await f.Service.RouteVoiceAsync("我刚才那个选项", f.Service.Current);
                ProbeAssert.Check(reply.ListenForReply && reply.Message.Contains("重听")
                    && !reply.Message.Contains(question.Questions[0].Question)
                    && question.Questions[0].Options.All(option => !reply.Message.Contains(option.Label))
                    && f.Service.Current.VoiceAnswers.Count == 0 && f.Client.Responses.Count == 0,
                    "a failed choice repeated the entire question or invented a selected option");
            }
            var repeat = await f.Service.RouteVoiceAsync("重听", f.Service.Current);
            ProbeAssert.Check(repeat.Message.Contains("1：") && repeat.Message.Contains("5：")
                && f.Client.Responses.Count == 0, "explicit repeat no longer reads the available choices");
        });

        await test("short confirmations cannot become task creation content", async () =>
        {
            foreach (var creation in new[] { "新建DSH任务", "新建一个绘画进行DSH的任务" })
            {
                using var f = new TaskCase();
                await f.Service.RouteVoiceAsync(creation);
                var created = f.Client.Creates.Count;
                foreach (var input in new[] { "确认", "确定", "确认提交", "提交答案" })
                {
                    var reply = await f.Service.RouteVoiceAsync(input);
                    ProbeAssert.Check(reply.Handled && reply.ListenForReply && f.Client.Creates.Count == created
                        && f.Client.Prompts.Count == 0 && f.Client.Responses.Count == 0,
                        "an unbound confirmation became the new task body: " + creation + ": " + input);
                }
            }
        });
    }

    private static DshTaskInteraction AudioQuestion() => new("audio-question", "question", "ask_question", "", [
        new("audio_next_step", "授权方向", "请选择下一步", [
            new("切换默认输出到 PixelBar 扬声器（推荐）", ""),
            new("保持现状，先播放音频验证听感", ""), new("微调参数（低音/高音/音量倾向）", ""),
            new("恢复「平直原声」并关闭音效", ""), new("只授予本次测试结论，不再做任何改动", "")])]);

    private static async Task WaitForQuestion(TaskCase f, DshTaskInteraction question)
    {
        await f.Start();
        f.Client.SetRemote("waitingInput", "pending-question", [question]);
        await WaitUntil(() => f.Service.Current.NeedsAttention);
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.ElapsedMilliseconds > 2000) throw new TimeoutException();
            await Task.Delay(5);
        }
    }
}
