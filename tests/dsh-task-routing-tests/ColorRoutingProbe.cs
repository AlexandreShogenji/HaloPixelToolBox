using HaloPixelToolBox.Services;

internal static class ColorRoutingProbe
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("short color scheme controls bypass the generic task-plan keyword", () =>
        {
            foreach (var command in new[]
            {
                "换个颜色方案。", "换一个配色方案", "请帮我换套颜色方案", "换下一套颜色方案",
                "更换颜色预设", "切换到第一个颜色方案", "随机换一个配色方案", "切换随机配色方案",
                "把颜色方案换成贺喜遥香", "配色方案切换到第三个", "切换颜色方案为深蓝"
            })
            {
                ProbeAssert.Check(DshTaskVoiceParser.IsDeviceCommand(command), "Color scheme rejected as task: " + command);
                ProbeAssert.Check(DshVoiceIntentRouter.Classify(command) == DshVoiceIntent.DeviceControl,
                    "Color scheme entered the selected task: " + command);
                ProbeAssert.Check(DshVoiceIntentRouter.Classify(command, awaitingTaskContent: true) == DshVoiceIntent.DeviceControl,
                    "Color scheme consumed the pending task body: " + command);
            }
            return Task.CompletedTask;
        });

        await test("traditional color scheme commands preserve their device destination", () =>
        {
            foreach (var command in new[] { "換個顏色方案", "請幫我換一套配色方案", "切換到第一個顏色預設", "把配色方案換成賀喜遙香" })
                ProbeAssert.Check(DshVoiceIntentRouter.Classify(command) == DshVoiceIntent.DeviceControl,
                    "Traditional color scheme became a task: " + command);
            return Task.CompletedTask;
        });

        await test("web page color schemes and task instruction bodies remain task work", () =>
        {
            foreach (var command in new[]
            {
                "给网页换个颜色方案", "把页面配色方案换成深色", "换一个颜色方案用于页面设计", "换个网页颜色预设",
                "把配色方案换成前端项目中的蓝色", "切换颜色方案为备忘录的深色主题", "为网站设计颜色方案",
                "请分析颜色方案", "不要换个颜色方案", "怎么换个颜色方案", "如果换个颜色方案会怎样",
                "任务内容，换个颜色方案", "任务指令，把颜色方案换成深蓝", "继续任务，换一个配色方案"
            })
                ProbeAssert.Check(DshVoiceIntentRouter.Classify(command) == DshVoiceIntent.Task,
                    "Task content incorrectly became a hardware command: " + command);
            return Task.CompletedTask;
        });

        await test("pending choices keep color words as answers unless speaker control is explicit", () =>
        {
            foreach (var answer in new[] { "换个颜色方案", "第一项", "第三项", "回答：换个配色方案", "把颜色方案换成深蓝" })
                ProbeAssert.Check(DshVoiceIntentRouter.Classify(answer, hasPendingInteraction: true) == DshVoiceIntent.InteractionReply,
                    "Pending answer was executed as device control: " + answer);
            foreach (var command in new[] { "音箱控制，换个颜色方案", "设备把配色方案换成深蓝" })
                ProbeAssert.Check(DshVoiceIntentRouter.Classify(command, hasPendingInteraction: true) == DshVoiceIntent.DeviceControl,
                    "Explicit speaker detour failed while a task waits: " + command);
            ProbeAssert.Check(DshVoiceIntentRouter.Classify("回答：换个颜色方案") == DshVoiceIntent.Task,
                "Explicit answer with no pending question became hardware control.");
            return Task.CompletedTask;
        });
    }
}
