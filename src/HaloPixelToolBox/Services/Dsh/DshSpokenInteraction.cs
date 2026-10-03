using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HaloPixelToolBox.Models;
using Microsoft.VisualBasic;

namespace HaloPixelToolBox.Services;

internal sealed record DshSpokenAnswer(bool Success, string Answer, string Error);

/// <summary>Deterministic spoken answers; an uncertain choice never becomes free text implicitly.</summary>
internal static class DshSpokenInteraction
{
    private static readonly Regex RecommendedSuffix = new(@"\s*[\[(]\s*(?:recommended|推荐|建议)\s*[\])]\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex ChoicePrefix = new(@"^(?:请|麻烦)?(?:帮我)?(?:我想选择|我想选|我选择|我要选择|我要选|我选|选择|选中|选(?!项))\s*", RegexOptions.IgnoreCase);
    private static readonly Regex ChoiceSeparator = new(@"(?:(?:以及|还有|并且|加上|和|及|与|跟|、|，|,|；|;|\||\+|&|\band\b)\s*)+", RegexOptions.IgnoreCase);
    private static readonly Regex UnsafeChoice = new(@"不要|别选|不选|不想|不确定|不知道|不是|除外|除了|取消|没有|没想|不太|先不|不需要|暂不|不对|稍等|等一下|重说|重选|重复|再说|什么意思|为什么|怎么|什么|有哪些|哪一个|能不能|能否|还是|或者|或是|随便|随机|都行|建议|推荐|[?？]|(?:吗|呢)$|\b(?:no|not|except|or|don't)\b", RegexOptions.IgnoreCase);
    private const string Retry = "请说选项序号或名称；其他答案请以“我的答案是”开头。";

    public static DshSpokenAnswer ParseAnswer(DshTaskQuestion question, string text)
    {
        ArgumentNullException.ThrowIfNull(question);
        var original = (text ?? string.Empty).Trim();
        if (original.Length == 0) return Failure("没有听清答案。" + Retry);
        if (original.Length > 4096) return Failure("答案过长，请分次说明。");
        if (question.Options.Count == 0) return Success(original);

        // Preserve the body, including punctuation, casing and line breaks, for explicit custom answers.
        var custom = Regex.Match(original, @"^(?:我的)?(?:回答|答案|自由回答|自定义回答|自定義回答|自訂回答)\s*[:：]\s*(?<answer>[\s\S]*)$");
        if (!custom.Success)
            custom = Regex.Match(original, @"^(?:我的答案是|回答是|答案是|自定义回答|自定義回答|自訂回答)\s*[:：，,]?\s*(?<answer>[\s\S]*)$");
        if (custom.Success)
        {
            var answer = custom.Groups["answer"].Value.Trim();
            return answer.Length > 0 ? Success(answer) : Failure("请在“我的答案是”后说出答案。");
        }

        string spoken;
        string[] names;
        try
        {
            spoken = Simplify(original).Trim().TrimEnd('。', '.', '！', '!');
            names = question.Options.Select(option => NormalizeName(option.Label)).ToArray();
        }
        catch (ArgumentException)
        {
            return Failure("无法解析这段文字。" + Retry);
        }
        if (spoken.Contains('?') || spoken.Contains('？'))
            return Failure("这句话还不能确定你的选择。" + Retry);

        // A literal label such as “不保存” is a valid answer, not a negated selection command.
        var exact = ExactMatches(NormalizeName(spoken), names);
        if (exact.Length == 1) return Selected(question, exact);
        if (exact.Length > 1) return Failure("这个名称对应多个选项，请说序号。");
        spoken = ChoicePrefix.Replace(spoken, string.Empty).Trim();
        exact = ExactMatches(NormalizeName(spoken), names);
        if (exact.Length == 1) return Selected(question, exact);
        if (exact.Length > 1) return Failure("这个名称对应多个选项，请说序号。");
        if (UnsafeChoice.IsMatch(spoken)) return Failure("这句话还不能确定你的选择。" + Retry);

        if (spoken is "全选" or "全部" or "全部选择" or "都选" or "全都选")
            return question.MultiSelect ? Selected(question, Enumerable.Range(0, names.Length))
                : Failure("这一题只能选择一项，请说一个序号或名称。");

        var tokens = ChoiceSeparator.Split(spoken);
        var selected = new HashSet<int>();
        foreach (var part in tokens)
        {
            var token = ChoicePrefix.Replace(part.Trim(), string.Empty).Trim();
            if (token.Length == 0) return Failure("有一个选项没有听清。" + Retry);
            var name = NormalizeName(token);
            var matches = ExactMatches(name, names);
            if (matches.Length == 0 && TryOrdinal(token, out var ordinal))
            {
                if (ordinal < 1 || ordinal > names.Length) return Failure($"这一题只有 {names.Length} 个选项，请重新选择。");
                selected.Add(ordinal - 1);
                continue;
            }
            if (matches.Length == 0 && name.EnumerateRunes().Count() >= 2)
                matches = names.Select((value, index) => (value, index))
                    .Where(item => item.value.Contains(name, StringComparison.Ordinal)).Select(item => item.index).ToArray();
            if (matches.Length == 0) return Failure("没有找到对应选项。" + Retry);
            if (matches.Length > 1) return Failure("这个名称对应多个选项，请说更完整的名称或序号。");
            selected.Add(matches[0]);
        }

        if (!question.MultiSelect && selected.Count > 1) return Failure("这一题只能选择一项，请说一个序号或名称。");
        return Selected(question, selected.OrderBy(index => index));
    }

    /// <param name="index">Zero-based question index.</param>
    public static string BuildQuestionPrompt(DshTaskQuestion question, int index, int total)
    {
        ArgumentNullException.ThrowIfNull(question);
        var prompt = new StringBuilder($"第 {index + 1}/{total} 题：");
        var wording = (string.IsNullOrWhiteSpace(question.Question) ? question.Header : question.Question).Trim();
        prompt.Append(wording);
        if (wording.Length == 0 || !"。！？.!?".Contains(wording[^1])) prompt.Append('。');
        for (var option = 0; option < question.Options.Count; option++)
        {
            prompt.Append(option + 1).Append('：').Append(question.Options[option].Label.Trim()).Append('；');
        }
        if (question.Options.Count == 0) prompt.Append("请直接说出答案。");
        else
        {
            prompt.Append(question.MultiSelect && question.Options.Count > 1
                ? $"可多选，请说序号或名称，例如“一和{(question.Options.Count > 2 ? "三" : "二")}”。"
                : $"{(question.MultiSelect ? "可多选" : "单选")}，请说序号或名称，例如“第一项”。");
            prompt.Append("其他答案请说“我的答案是”加上内容。");
        }
        return prompt.ToString();
    }

    /// <summary>Split without truncating the tail or breaking a Unicode scalar's UTF-8 bytes.</summary>
    public static IReadOnlyList<string> BuildSubtitlePages(string text, int maxUtf8Bytes = 55)
    {
        if (maxUtf8Bytes < 4) throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes), "A page must fit any Unicode scalar.");
        if (string.IsNullOrEmpty(text)) return [];
        var pages = new List<string>();
        var page = new StringBuilder();
        var bytes = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (Encoding.UTF8.GetByteCount(element) <= maxUtf8Bytes) Append(element);
            else foreach (var rune in element.EnumerateRunes()) Append(rune.ToString());
        }
        if (page.Length > 0) pages.Add(page.ToString());
        return pages;

        void Append(string element)
        {
            var size = Encoding.UTF8.GetByteCount(element);
            if (bytes + size > maxUtf8Bytes)
            {
                pages.Add(page.ToString());
                page.Clear();
                bytes = 0;
            }
            page.Append(element);
            bytes += size;
        }
    }

    private static int[] ExactMatches(string value, string[] names) => value.Length == 0 ? [] : names
        .Select((name, index) => (name, index)).Where(item => item.name == value).Select(item => item.index).ToArray();

    private static DshSpokenAnswer Selected(DshTaskQuestion question, IEnumerable<int> indexes)
    {
        var answer = string.Join(" | ", indexes.Select(index => question.Options[index].Label.Trim()));
        return answer.Length is >= 1 and <= 4096 ? Success(answer) : Failure("选项答案过长或为空，请重新选择。");
    }

    private static DshSpokenAnswer Success(string answer) => new(true, answer, string.Empty);
    private static DshSpokenAnswer Failure(string error) => new(false, string.Empty, error);
    public static string NormalizeCommand(string text) => Regex.Replace(Simplify(text ?? string.Empty), @"[\s\p{P}]+", string.Empty).ToLowerInvariant();
    private static string NormalizeName(string value) => Regex.Replace(
        RecommendedSuffix.Replace(Simplify(value), string.Empty), @"[\s\p{P}]+", string.Empty)
        .Replace("搜寻", "搜索", StringComparison.Ordinal).ToLowerInvariant();

    private static bool TryOrdinal(string value, out int number)
    {
        var match = Regex.Match(value, @"^\s*(?:第|选项)?\s*(?<n>[0-9零〇一二两三四五六七八九十百]+)\s*(?:个?选项|项|个|号)?\s*$");
        number = 0;
        if (!match.Success) return false;
        var numeral = match.Groups["n"].Value;
        if (int.TryParse(numeral, NumberStyles.None, CultureInfo.InvariantCulture, out number)) return true;
        if (Regex.IsMatch(numeral, @"\d")) return false;
        if (numeral.Length == 1 && Digit(numeral[0]) is var digit && digit >= 0) { number = digit; return true; }
        // Chinese ordinals through 999, in canonical hundreds/tens form; “一三” is not silently 13.
        if (!Regex.IsMatch(numeral, @"^(?:[一二两三四五六七八九]百(?:零[一二三四五六七八九]|[一二三四五六七八九]十[一二三四五六七八九]?)?|[一二两三四五六七八九]?十[一二三四五六七八九]?)$")) return false;
        var pending = 0;
        number = 0;
        foreach (var character in numeral)
        {
            if (character is '百' or '十') { number += (pending == 0 ? 1 : pending) * (character == '百' ? 100 : 10); pending = 0; }
            else pending = Digit(character);
        }
        number += pending;
        return true;
    }

    private static int Digit(char character) => character == '两' ? 2 : character == '〇' ? 0 : "零一二三四五六七八九".IndexOf(character);

    private static string Simplify(string value)
    {
        value = value.Normalize(NormalizationForm.FormKC);
        if (OperatingSystem.IsWindows())
        {
            try { return Strings.StrConv(value, VbStrConv.SimplifiedChinese, 0x0804) ?? value; }
            catch (ArgumentException) { }
            catch (PlatformNotSupportedException) { }
        }
        // Keep core choice vocabulary usable when the platform lacks Chinese conversion data.
        const string traditional = "選擇項個號兩與還並請幫義訂薦議儲存顏標籤搜尋開關聲時歷錄淺紙張資料設會話畫為顯這來復雲動隨機後燈詞場圖簡繁體聽說內容刪備忘錄頂閉權限預設換鐘認現煩劃繪";
        const string simplified =  "选择项个号两与还并请帮义订荐议储存颜标签搜寻开关声时历录浅纸张资料设会话画为显这来复云动随机后灯词场图简繁体听说内容删备忘录顶闭权限预设换钟认现烦划绘";
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var index = traditional.IndexOf(character);
            result.Append(index >= 0 ? simplified[index] : character);
        }
        return result.ToString();
    }
}
