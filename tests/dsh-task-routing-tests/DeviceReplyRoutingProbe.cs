using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services;

internal static class DeviceReplyRoutingProbe
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        var target = new DshDeviceReplyTarget(Guid.NewGuid().ToString("D"), "C:/fixture/dsh", "fixture-profile");
        DshDeviceCommandResult Choice(string text) => new(true, text, text, target.SessionId, [], [])
        { Accepted = true, Completed = true, RequestId = Guid.NewGuid().ToString("D"), ReplyTarget = target };
        await test("device clarification binds a short ordinal to the exact conversation without mapping it", () =>
        {
            foreach (var prompt in new[] { "请选择：1，配色；2，灯效。", "想换哪个灯光预设？", "你说的换个颜色方案是指哪一种？",
                "请说选项序号。", "請選擇：一、天空，二、火焰。" })
            {
                var result = Choice(prompt);
                ProbeAssert.Check(DshDeviceReplyRouting.CanContinue(result), "Explicit choice was not detected: " + prompt);
                foreach (var reply in new[] { "第一项。", "第三项", "我选第二个", "選擇第三項", "嗯，第3个选项吧", "1", "三" })
                    ProbeAssert.Check(DshDeviceReplyRouting.TryResolve(reply, result, TimeSpan.FromSeconds(20), out var routed, out var owner)
                        && routed == "音箱控制，" + reply && owner == target,
                        "Ordinal was lost, rewritten as a guessed preset, or rebound: " + reply);
            }
            return Task.CompletedTask;
        });
        await test("catalog calls and completed status do not create an implicit device choice", () =>
        {
            foreach (var response in new[] { "已切换到天空。", "已获取3个选项。", "好了，还有需要吗？", "不需要你选择，已经应用。", "这是一个颜色方案。" })
                ProbeAssert.Check(!DshDeviceReplyRouting.CanContinue(Choice(response) with { CalledTools = ["get_pixelbar_catalog"] }),
                    "A read-only tool/result created an unasked choice: " + response);
            return Task.CompletedTask;
        });
        await test("device reply context rejects failures stale scopes and nonordinal user content", () =>
        {
            var result = Choice("请选择配色方案：1天空，2火焰。");
            foreach (var invalid in new[] { result with { Success = false }, result with { Completed = false },
                result with { ReplyTarget = null }, result with { SessionId = Guid.NewGuid().ToString("D") }, result with { RequestId = "" } })
                ProbeAssert.Check(!DshDeviceReplyRouting.TryResolve("第一项", invalid, TimeSpan.Zero, out _, out _),
                    "Unconfirmed or mismatched result retained a choice owner.");
            foreach (var elapsed in new[] { TimeSpan.FromMilliseconds(-1), TimeSpan.FromSeconds(91) })
                ProbeAssert.Check(!DshDeviceReplyRouting.TryResolve("第一项", result, elapsed, out _, out _), "Expired device choice remained usable.");
            foreach (var text in new[] { "第一项和第三项", "不要第一项", "第一项吗", "第一项？", "第三项用于网页", "任务内容第一项",
                "确认提交", "批准本次", "回答：第三项", "天空", "再换一个", "零项" })
                ProbeAssert.Check(!DshDeviceReplyRouting.TryResolve(text, result, TimeSpan.Zero, out _, out _),
                    "Nonordinal content was consumed as a device answer: " + text);
            return Task.CompletedTask;
        });
    }
}
