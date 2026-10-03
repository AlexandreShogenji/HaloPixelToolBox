using System.Text.RegularExpressions;
using HaloPixelToolBox.Models;

namespace HaloPixelToolBox.Services;

/// <summary>Keep a short choice reply with the device conversation that asked it.</summary>
internal static class DshDeviceReplyRouting
{
    public static bool CanContinue(DshDeviceCommandResult result)
    {
        if (!result.Success || !result.Completed || result.ReplyTarget is not { } target
            || !Guid.TryParse(target.SessionId, out _) || target.SessionId != result.SessionId
            || string.IsNullOrWhiteSpace(target.Home) || string.IsNullOrWhiteSpace(result.RequestId))
            return false;
        var text = DshSpokenInteraction.NormalizeCommand(result.FinalText);
        if (Regex.IsMatch(text, @"(?:不用|不需要|无需|不必|不要).{0,4}(?:选择|选项|序号)")) return false;
        // Read-only tools such as get_catalog do not prove a choice is pending.
        // Require an actual invitation to choose; a generic question mark or a
        // result mentioning “选项” cannot capture the next unrelated utterance.
        return Regex.IsMatch(text, @"(?:请|可以|你可以)(?:直接)?(?:说|回复|回答)(?:出)?(?:选项|序号|编号|第几项)")
            || Regex.IsMatch(text, @"(?:请|需要你|请你)(?:选择|选出|选一个|选一项)")
            || Regex.IsMatch(text, @"(?:你|您|想|要|希望|需要|选择|换成|切换到|是指).{0,8}(?:哪种|哪一种|哪个|哪一个|哪项|哪一项)");
    }

    public static bool TryResolve(string text, DshDeviceCommandResult previous, TimeSpan elapsed,
        out string routedCommand, out DshDeviceReplyTarget? target)
    {
        routedCommand = string.Empty;
        target = null;
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(90) || !CanContinue(previous))
            return false;
        if (!IsOrdinalReply(text)) return false;
        target = previous.ReplyTarget;
        routedCommand = "音箱控制，" + text.Trim();
        return true;
    }

    public static bool IsOrdinalReply(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('?') || text.Contains('？')) return false;
        var normalized = DshSpokenInteraction.NormalizeCommand(text);
        // Only a standalone ordinal is eligible. Names and arbitrary follow-ups
        // remain with the regular router; this never invents an option-to-preset
        // mapping, nor treats “确认提交” or approval words as device choices.
        const string number = @"(?:[1-9][0-9]{0,2}|[一二两三四五六七八九]|[一二两三四五六七八九]?十[一二三四五六七八九]?)";
        return Regex.IsMatch(normalized, @"^(?:嗯|呃|额)?(?:我选|我要选|我选择|选择|选)?(?:第|选项)?"
            + number + @"(?:个选项|选项|项|个|号)?(?:吧|啊|就行)?$");
    }
}
