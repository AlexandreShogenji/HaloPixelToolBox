using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

internal enum DshVoiceIntent { Task, DeviceControl, InteractionReply, ClarifySceneIntent }

/// <summary>Choose the destination before a selected task can consume a voice message.</summary>
internal static class DshVoiceIntentRouter
{
    private const string AmbiguousScenePattern = @"^(?:画|划)(?:[一1]个|个)?(?<scene>(?:时钟|钟表|时间|自定义|默认|推荐|动画|音乐|像素)?(?:的)?场景)(?:吧|一下)?$";

    public static string SceneClarification(string text)
    {
        var match = Regex.Match(StripCourtesy(DshSpokenInteraction.NormalizeCommand(text)), AmbiguousScenePattern);
        var scene = match.Success ? match.Groups["scene"].Value.Replace("的", "") : "场景";
        return $"听到的是“画场景”。如果要切换音箱显示，请说“切换{scene}”；如果要绘制，请说“任务内容，画一个{scene}”。";
    }

    public static DshVoiceIntent Classify(string text, bool hasPendingInteraction = false, bool awaitingTaskContent = false)
    {
        // Explicit answer/task bodies remain data, even if they contain device commands.
        if (IsExplicitAnswer(text)) return hasPendingInteraction ? DshVoiceIntent.InteractionReply : DshVoiceIntent.Task;
        if (HasTaskContentPrefix(text) || DshTaskVoiceParser.IsCreate(text)
            || DshTaskVoiceParser.IsCreationCandidate(text) || DshTaskVoiceParser.IsNegatedCreation(text))
            return DshVoiceIntent.Task;
        var normalized = StripCourtesy(DshSpokenInteraction.NormalizeCommand(text));
        if (Regex.IsMatch(normalized, @"^(?:设备|音箱控制|控制音箱)")) return DshVoiceIntent.DeviceControl;
        if (hasPendingInteraction) return DshVoiceIntent.InteractionReply;
        // ASR can confuse 换/画/划. Do not silently turn a drawing request into a
        // hardware action, or post it to an unrelated task merely because one is selected.
        if (!awaitingTaskContent && Regex.IsMatch(normalized, AmbiguousScenePattern))
            return DshVoiceIntent.ClarifySceneIntent;
        return DshTaskVoiceParser.IsDeviceCommand(text) ? DshVoiceIntent.DeviceControl : DshVoiceIntent.Task;
    }

    internal static string StripCourtesy(string normalized)
        => Regex.Replace(normalized, @"^(?:呃|额|嗯|那个|请|帮我|麻烦|现在|先|能不能|能否|可以帮我|可以)*", "");

    internal static bool IsExplicitAnswer(string text) => Regex.IsMatch(text.Trim(),
        @"^(?:(?:我的)?(?:回答|答案|自由回答|自定义回答|自定義回答|自訂回答)\s*[:：]|我的答案是|回答是|答案是|自定义回答|自定義回答|自訂回答)");
    private static bool HasTaskContentPrefix(string text) => Regex.IsMatch(DshSpokenInteraction.NormalizeCommand(text),
        @"^(?:任务内容|任务指令|继续任务|在当前任务|在这个任务|给当前任务)");
}
