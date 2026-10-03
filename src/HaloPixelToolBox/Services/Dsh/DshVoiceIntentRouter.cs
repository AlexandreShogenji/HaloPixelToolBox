using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

internal enum DshVoiceIntent { Task, DeviceControl, InteractionReply, ClarifySceneIntent }

/// <summary>Choose the destination before a selected task can consume a voice message.</summary>
internal static class DshVoiceIntentRouter
{
    private const string AmbiguousScenePattern = @"^(?:画|划)" + DshTaskVoiceParser.SceneChoicePattern
        + @"(?<scene>" + DshTaskVoiceParser.ShortSceneTargetPattern + @"|场景)(?:吧|一下)?$";
    private const string SceneStatementPattern = @"^(?:我|你|刚才|刚刚|剛才|剛剛|已经|已經|好像)*(?:换了|更换了|切换了|切到了|切回了|换成了|换到了)"
        + DshTaskVoiceParser.SceneChoicePattern + @"(?<scene>" + DshTaskVoiceParser.ShortSceneTargetPattern + @"|场景)(?:了|啊|呀|呢)?$";

    public static string SceneClarification(string text)
    {
        var normalized = StripCourtesy(DshSpokenInteraction.NormalizeCommand(text));
        var drawing = Regex.Match(normalized, AmbiguousScenePattern);
        var match = drawing.Success ? drawing : Regex.Match(normalized, SceneStatementPattern);
        var scene = match.Success ? match.Groups["scene"].Value.Replace("的", "") : "场景";
        if (!scene.EndsWith("场景", StringComparison.Ordinal))
            scene = Regex.Replace(scene, @"(?:场|长|長|模式|显示)$", "") + "场景";
        return drawing.Success
            ? $"听到的是“画场景”。如果要切换音箱显示，请说“切换{scene}”；如果要绘制，请说“任务内容，画一个{scene}”。"
            : $"这句像是在描述音箱场景。要切换显示，请说“切换{scene}”；要继续开发任务，请说“任务内容”再说明要求。";
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
        // A short device-domain correction/statement is not permission to switch
        // hardware, and must not silently become another development-task prompt.
        if (Regex.IsMatch(normalized, SceneStatementPattern)) return DshVoiceIntent.ClarifySceneIntent;
        // ASR can confuse 换/画/划. Do not silently turn a drawing request into a
        // hardware action, or post it to an unrelated task merely because one is selected.
        if (!awaitingTaskContent && Regex.IsMatch(normalized, AmbiguousScenePattern))
            return DshVoiceIntent.ClarifySceneIntent;
        return DshTaskVoiceParser.IsDeviceCommand(text) ? DshVoiceIntent.DeviceControl : DshVoiceIntent.Task;
    }

    // The caller supplies only a previously successful device command and must
    // clear that context after task/clarification turns or before answering a task.
    public static bool TryResolveDeviceFollowUp(string text, string previousDeviceCommand, TimeSpan elapsed, out string resolved)
    {
        resolved = string.Empty;
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(90)
            || Classify(previousDeviceCommand) != DshVoiceIntent.DeviceControl) return false;
        var followUp = StripCourtesy(DshSpokenInteraction.NormalizeCommand(text));
        if (!Regex.IsMatch(followUp, @"^(?:换(?:[一1]个|个)(?:别的|其他的|其它的)?|换别的|(?:下|另)[一1]个)(?:吧|一下)?$"))
            return false;
        var previous = StripCourtesy(DshSpokenInteraction.NormalizeCommand(previousDeviceCommand));
        previous = StripCourtesy(Regex.Replace(previous, @"^(?:设备|音箱控制|控制音箱)", string.Empty));
        var match = Regex.Match(previous, @"^" + DshTaskVoiceParser.SceneSwitchVerbPattern + @"(?:一下)?"
            + DshTaskVoiceParser.SceneChoicePattern + @"(?<kind>" + DshTaskVoiceParser.SceneKindPattern
            + @")(?:的)?(?:第?[一二三四五六七八九十百两0-9]+(?:个|项)?)?(?:场景|场|长|長|模式|显示)?(?:一下)?(?:吧|好吗|可以吗)?$");
        // The default display is a single destination, so “another” has no safe
        // category to inherit from it. The user must identify the next category.
        if (!match.Success || match.Groups["kind"].Value == "默认") return false;
        var verb = followUp.StartsWith("下", StringComparison.Ordinal) ? "切换到下一个" : "换一个";
        resolved = verb + match.Groups["kind"].Value + "场景";
        return true;
    }

    internal static string StripCourtesy(string normalized)
        => Regex.Replace(normalized, @"^(?:呃|额|嗯|那个|请|帮我|麻烦|现在|先|能不能|能否|可以帮我|可以|直接|其实|其實|还是|再)*", "");

    internal static bool IsExplicitAnswer(string text) => Regex.IsMatch(text.Trim(),
        @"^(?:(?:我的)?(?:回答|答案|自由回答|自定义回答|自定義回答|自訂回答)\s*[:：]|我的答案是|回答是|答案是|自定义回答|自定義回答|自訂回答)");
    private static bool HasTaskContentPrefix(string text) => Regex.IsMatch(DshSpokenInteraction.NormalizeCommand(text),
        @"^(?:任务内容|任务指令|继续任务|在当前任务|在这个任务|给当前任务)");
}
