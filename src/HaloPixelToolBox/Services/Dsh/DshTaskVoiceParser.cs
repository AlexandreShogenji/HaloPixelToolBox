using System.Text;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

internal static class DshTaskVoiceParser
{
    private const string CreatePhrase = @"(?:新建|创建|建立|开启|启动|开始)\s*(?:[一1１]\s*(?:个|项)|个|项)?\s*(?:(?:DSH\s*(?:的\s*)?)?(?:会话\s*)?任务(?:会话)?|DSH\s*(?:的\s*)?会话)";
    // These are complete short device objects, not substrings. 场/长 are common
    // clipped or near-sounding 场景 transcripts; a trailing task clause must not match.
    internal const string ClockKindPattern = @"(?:(?:数字|數字|模拟|模擬|电子|電子|像素)?时钟|钟表)";
    internal const string SceneKindPattern = @"(?:" + ClockKindPattern + @"|时间|自定义|默认|推荐|动画|音乐|像素)";
    internal const string ShortSceneTargetPattern = @"(?:" + ClockKindPattern + @"(?:的)?(?:场景|场|长|長|模式|显示)?|(?:时间|自定义|默认|推荐|动画|音乐|像素)(?:的)?(?:场景|场|长|長))";
    internal const string SceneChoicePattern = @"(?:(?:另外|另|下)?[一1]个|个)?(?:新的?)?";
    internal const string SceneSwitchVerbPattern = @"(?:切换(?:为|到|成)?|切到|切回|换成|换到|更换(?:为|成)?|换|恢复|还原|设为|设置为)";
    private const string ShortSceneSwitchPattern = @"^" + SceneSwitchVerbPattern + @"(?:一下)?"
        + SceneChoicePattern + ShortSceneTargetPattern + @"(?:一下)?(?:吧|好吗|可以吗)?$";
    private const string ColorSchemeObjectPattern = @"(?:(?:颜色|配色)(?:方案|预设)|灯光方案)";
    private const string ColorSchemeChoicePattern = @"(?:(?:另外|另|下)?[一1](?:个|套)|个|套|下一套|第[一二三四五六七八九十百两0-9]+(?:个|套)?)?";
    private const string ColorSchemeSuffixPattern = @"(?:一下)?(?:吧|好吗|可以吗)?$";
    public static string Normalize(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"[\s，,。.!！?？：:；;“”\""']", "").ToLowerInvariant();
    public static bool IsCreate(string text) => !IsNonCreationRequest(text)
        && Regex.IsMatch(Normalize(Header(text)), CreatePhrase, RegexOptions.IgnoreCase);
    public static bool IsCreationCandidate(string text) => !IsNonCreationRequest(text)
        && Regex.IsMatch(Normalize(Header(text)), @"(?:新建|创建|建立|开启|启动|开始).{0,40}(?:dsh|任务|会话)");
    public static bool IsTaskRelated(string text) => Regex.IsMatch(Normalize(text),
        @"dsh|deepseek|任务|会话|项目|代码|程序|编写|文件|文档|开发|实现|绘画|画图|生成|整理|总结|分析|修复");
    public static bool IsApprovalReply(string text) => Normalize(text) is "同意" or "批准" or "允许本次" or "批准本次"
        or "同意本次" or "拒绝" or "不同意" or "拒绝本次" or "不允许";
    public static bool IsNegatedCreation(string text) => Regex.IsMatch(Normalize(Header(text)),
        @"^(?:呃|额|嗯|请|那个|帮我|麻烦|我|现在|先|暂时)*(?:不要|别|不用|不需要|不必)(?:再)?(?:新建|创建|建立|开启|启动|开始).{0,24}(?:任务|会话)");
    public static bool RequestsMultipleTasks(string text) => !IsNonCreationRequest(text) && Regex.IsMatch(Normalize(Header(text)),
        @"(?:新建|创建|建立|开启|启动|开始)(?:[二两三四五六七八九十百]|[2-9]|\d{2,})(?:个|项).{0,16}(?:任务|会话)");
    private static bool IsNonCreationRequest(string text)
    {
        var header = Normalize(Header(text));
        var verb = Regex.Match(header, @"新建|创建|建立|开启|启动|开始");
        return verb.Success && Regex.IsMatch(header[..verb.Index],
            @"不要|别|不用|不需要|不必|如何|怎么|怎样|总结|说明|介绍|讨论|教程|回顾|上次|已经|假如|如果|例如");
    }
    public static bool IsDeviceCommand(string text)
    {
        var normalized = DshVoiceIntentRouter.StripCourtesy(DshSpokenInteraction.NormalizeCommand(text));
        if (Regex.IsMatch(normalized, @"^(?:设备|音箱控制|控制音箱)")) return true;
        // “方案” also occurs in the app's device color presets. Admit complete
        // short controls before the generic task-word exclusion, without making
        // a web page's color-scheme request into a hardware operation.
        if (IsColorSchemeCommand(normalized)) return true;
        if (Regex.IsMatch(normalized, @"编写|开发|实现|代码|项目|文件|函数|分析|方案|设计|绘制|绘画|画图|修复|文档|bug")) return false;
        return Regex.IsMatch(normalized, ShortSceneSwitchPattern)
            || Regex.IsMatch(normalized, @"氛围灯|灯光|灯效|灯速|关灯|开灯|音量|歌词|字幕屏|像素屏|自定义场景|配色预设|均衡器|音效|低音|高音|前级增益|输出设备|左右平衡|eq曲线|人声预设")
            || Regex.IsMatch(normalized, @"^(?:呃|额|嗯|请|請|帮我|幫我|麻烦|麻煩|现在|現在|先)*(?:(?:恢复|恢復|还原|還原|回到|切回|切换(?:为|到)?|切換(?:為|到)?)(?:一下)?)?(?:默认|默認)(?:的)?(?:场景|場景)(?:吧|一下)?$")
            || Regex.IsMatch(normalized, @"^(?:查询|查看|读取|获取|看看)(?:当前)?(?:设备|音箱)(?:当前)?状态$")
            || Regex.IsMatch(normalized, @"^(?:显示(?:现在|当前)?(?:的)?时间|校准(?:设备)?时间|(?:切换|切到|切回|换|更换|恢复|还原|设为|设置为).{0,24}场景)");
    }
    private static bool IsColorSchemeCommand(string normalized)
    {
        if (Regex.IsMatch(normalized, @"任务|会话|编写|开发|实现|代码|项目|文件|函数|分析|设计|绘制|绘画|画图|修复|文档|网页|页面|界面|网站|前端|备忘录|按钮|组件|css|html|bug"))
            return false;
        var device = @"(?:(?:把|将)?(?:音箱|设备)(?:的)?)?";
        return Regex.IsMatch(normalized, @"^" + device + @"(?:随机)?" + SceneSwitchVerbPattern
                + @"(?:一下)?(?:随机)?" + ColorSchemeChoicePattern + ColorSchemeObjectPattern + ColorSchemeSuffixPattern)
            || Regex.IsMatch(normalized, @"^" + device + @"(?:把|将)?" + ColorSchemeObjectPattern
                + SceneSwitchVerbPattern + @"(?:一下)?[^吗呢?？]{1,32}" + ColorSchemeSuffixPattern)
            || Regex.IsMatch(normalized, @"^" + device + SceneSwitchVerbPattern + ColorSchemeObjectPattern
                + @"(?:为|到|成)[^吗呢?？]{1,32}" + ColorSchemeSuffixPattern);
    }
    public static string Header(string text)
    {
        var content = Regex.Match(text, @"任务内容|任务指令|执行(?:我)?(?:以下|下面)?(?:的)?(?:命令|指令)?[：:，,]");
        if (content.Success) return text[..content.Index];
        var start = Regex.Match(text, CreatePhrase + @"(?:目录)?[：:，,]", RegexOptions.IgnoreCase);
        if (start.Success && !Regex.IsMatch(text[(start.Index + start.Length)..].TrimStart(), @"^(?:任务名|目录|路径)"))
            return text[..(start.Index + start.Length - 1)];
        return text;
    }
    public static string? ReadDirectory(string text)
    {
        text = Header(text);
        if (Normalize(text).Contains("默认目录")) return null;
        var quoted = Regex.Match(text, "[\"“](?<path>[a-zA-Z]:[\\\\/][^\"”]+)[\"”]");
        if (quoted.Success) return quoted.Groups["path"].Value.Trim();
        var inDirectory = Regex.Match(text, @"在\s*(?<path>[a-zA-Z]:[\\/].+?)(?:目录)?(?:下|中|里)(?:新建|创建|开启|启动|开始)");
        if (inDirectory.Success) return inDirectory.Groups["path"].Value.Trim();
        var plain = Regex.Match(text, @"(?:目录|路径)[：:]?\s*(?<path>[a-zA-Z]:[\\/][^，,。；;\r\n]+)");
        return plain.Success ? plain.Groups["path"].Value.Trim().Trim('"', '“', '”') : null;
    }
    public static string? ReadPrompt(string text)
    {
        var match = Regex.Match(text, @"(?:执行(?:我)?(?:以下|下面)?(?:的)?(?:命令|指令)?|任务内容|任务指令|内容)[：:，,]\s*(?<prompt>.+)$", RegexOptions.Singleline);
        if (!match.Success) match = Regex.Match(text, @"(?:任务内容|任务指令)[：:，,\s]*(?<prompt>.+)$", RegexOptions.Singleline);
        if (!match.Success) match = Regex.Match(text, CreatePhrase + @"(?:目录)?[：:，,]\s*(?<prompt>.+)$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var prompt = match.Success ? match.Groups["prompt"].Value.Trim() : null;
        return prompt is not null && !Regex.IsMatch(prompt, @"^任务名(?:称)?[：:]?") ? prompt : null;
    }
    public static string ReadTitle(string text)
    {
        var match = Regex.Match(text, @"(?:任务名(?:称)?|名为)[：:]?\s*[“""']?(?<title>[^，,。；;“”""'\r\n]+)");
        return match.Success ? match.Groups["title"].Value.Trim() : "语音任务";
    }
}
