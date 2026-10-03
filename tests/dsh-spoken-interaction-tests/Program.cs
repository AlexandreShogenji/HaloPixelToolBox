using System.Text;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services;

var passed = 0;
var failed = 0;
var single = Question(false, "浅色纸张 (Recommended)", "深色夜间", "彩色标签");
var multiple = Question(true, "搜索", "置顶", "颜色标签");

foreach (var input in new[] { "第一项", "第一个", "第1项", "１", "一", "选一", "我选择第一项", "請選第一項", "选择第１個選項", "选项1", "第一项。" })
    Accepted(single, input, "浅色纸张 (Recommended)");
foreach (var input in new[] { "浅色纸张", "淺色紙張", "浅色", "纸张", "浅色纸张（推荐）", "浅色纸张（Recommended）", "选择浅色纸张", "帮我选择浅色", "我想选择浅色" })
    Accepted(single, input, "浅色纸张 (Recommended)");
foreach (var input in new[] { "第二项", "两", "第兩項", "二号", "2", "深色夜间", "深色" })
    Accepted(single, input, "深色夜间");
foreach (var input in new[] { "嗯，第一项。", "呃，我选第一项。", "额，第一项吧。", "嗯嗯，那就选第一项。", "就选第一项", "第一项就行", "嗯，第一项好了。" })
    Accepted(single, input, "浅色纸张 (Recommended)");
foreach (var input in new[] { "一和三", "第一项与第三项", "选择搜索和颜色标签", "搜尋和顏色標籤", "1,3", "１、３", "选第一项，加上第三项", "1 | 3", "1 & 3", "1 and 3", "第三项和第一项", "一和三和一" })
    Accepted(multiple, input, "搜索 | 颜色标签");
foreach (var input in new[] { "全部", "全选", "全部选择", "都选", "全都选" })
    Accepted(multiple, input, "搜索 | 置顶 | 颜色标签");
foreach (var input in new[] { "一和三", "第一项、第二项", "全选", "全部", "浅色和深色" }) Rejected(single, input);
foreach (var input in new[] { "", " ", "第四项", "0", "零", "-1", "9999999999999", "一三", "未知选项", "好", "好的", "不", "不要选第一项", "不是第一项", "不想选一", "不要搜索", "除了第一项都选", "一和不要三", "第一项还是第二项", "第一项或者第三项", "第一项吗", "一？", "搜索？", "搜索?", "推荐一个", "你帮我决定", "搜索然后关闭音箱", "我觉得第一项挺不错", "第一项和", "一、未知选项", "一和四", "第一项/第三项" })
    Rejected(multiple, input);

Accepted(single, "回答：暂时不想使用这些样式。", "暂时不想使用这些样式。");
Accepted(single, "自由回答：保留 ABC，\n并显示 emoji 🐈‍⬛。", "保留 ABC，\n并显示 emoji 🐈‍⬛。");
Accepted(single, "自定義回答：自定义样式", "自定义样式");
Accepted(single, "我的答案是 保留原来的设计。", "保留原来的设计。");
Accepted(single, "我的答案是：保留 ABC，\n继续 🐈‍⬛。", "保留 ABC，\n继续 🐈‍⬛。");
Accepted(single, "回答是任何已备份的目录", "任何已备份的目录");
Accepted(single, "答案是自定义主题", "自定义主题");
Accepted(single, "自定义回答不要修改外观", "不要修改外观");
Accepted(single, "自定義回答這是原文 🦄", "這是原文 🦄");
Accepted(single, "自訂回答，保留繁體內容", "保留繁體內容");
Rejected(single, "回答：");
Rejected(single, "回答：  ");
Rejected(single, "我的答案是");
Rejected(single, "回答是  ");
Rejected(single, "自定义回答");
Rejected(single, new string('a', 4097));
Accepted(Question(false), "第一项也行，不过请告诉我区别。", "第一项也行，不过请告诉我区别。");
Accepted(Question(false), "回答：这就是正文。", "回答：这就是正文。");
Accepted(Question(false), "No, keep the original.\n第二行 🧑🏽‍💻", "No, keep the original.\n第二行 🧑🏽‍💻");

var negativeLabels = Question(false, "保存", "不保存", "取消");
Accepted(negativeLabels, "不保存", "不保存");
Accepted(negativeLabels, "选择不保存", "不保存");
Accepted(negativeLabels, "取消", "取消");
Rejected(negativeLabels, "不要保存");
Rejected(negativeLabels, "不保存也不要取消");

var ambiguous = Question(false, "浅色纸张", "浅色护眼", "深色夜间");
Rejected(ambiguous, "浅色");
Accepted(ambiguous, "纸张", "浅色纸张");
Rejected(Question(false, "搜索", "搜尋"), "搜索");
Accepted(Question(false, "搜索", "搜尋"), "第二项", "搜尋");
Accepted(Question(false, "搜索 (Recommended)", "搜索 (推荐)"), "二", "搜索 (推荐)");
Rejected(Question(false, "搜索 (Recommended)", "搜索 (推荐)"), "搜索");
Accepted(Question(false, "搜索和置顶", "不处理"), "搜索和置顶", "搜索和置顶");
Accepted(Question(true, "搜索和置顶", "标签"), "第一项和二", "搜索和置顶 | 标签");
Accepted(Question(false, "COLOR TAGS", "DARK MODE"), "color tags", "COLOR TAGS");
Accepted(Question(false, "🌈 彩色风格", "深色模式"), "彩色风格", "🌈 彩色风格");
Accepted(Question(false, "🔴", "🟢"), "🟢", "🟢");
Rejected(single, "\uD800");
Accepted(Question(false, "ＡＢＣ（推薦）", "ＤＥＦ"), "abc", "ＡＢＣ（推薦）");
Accepted(Question(false, "Café", "Tea"), "Cafe\u0301", "Café");

var hundred = Question(false, Enumerable.Range(1, 120).Select(index => $"方案 {index}").ToArray());
Accepted(hundred, "第十项", "方案 10");
Accepted(hundred, "十一", "方案 11");
Accepted(hundred, "二十", "方案 20");
Accepted(hundred, "二十一", "方案 21");
Accepted(hundred, "第九十九项", "方案 99");
Accepted(hundred, "一百", "方案 100");
Accepted(hundred, "一百零一", "方案 101");
Accepted(hundred, "一百一十", "方案 110");
Accepted(hundred, "一百二十", "方案 120");
Rejected(hundred, "一百一");
Rejected(hundred, "一百二十一");
Rejected(hundred, "十一十");
Rejected(hundred, "1一");

var colorscheme = new DshTaskQuestion("colorscheme", "确认目标", "你说的「换个颜色方案」是指哪一种？", [
    new("已保存的配色方案（推荐）", "列出了所有人物和配色的较长描述。" + new string('中', 161)),
    new("换一个氛围灯效", "更换动态效果"), new("随机挑一个配色", "随机选择")], false);
Accepted(colorscheme, "第一项", "已保存的配色方案（推荐）");
Accepted(colorscheme, "第三项", "随机挑一个配色");
Accepted(colorscheme, "第一項。", "已保存的配色方案（推荐）");
Accepted(colorscheme, "嗯，第三项吧。", "随机挑一个配色");
Accepted(colorscheme, "已保存的配色方案", "已保存的配色方案（推荐）");
foreach (var input in new[] { "嗯，不选第一项。", "第一项还是第三项", "第三项吧，不要执行", "第三项就行吗", "嗯，第三项吗", "额，第一项然后关闭音箱" })
    Rejected(colorscheme, input);

var prompt = DshSpokenInteraction.BuildQuestionPrompt(single, 0, 2);
Check(prompt.Contains("第 1/2 题") && prompt.Contains("选择界面风格") && prompt.Contains("1：浅色纸张") && prompt.Contains("2：深色夜间") && prompt.Contains("第几项"), "single prompt announces question index and numbered choices");
Check(!prompt.Contains("Recommended") && !prompt.Contains("我的答案是"), "ordinary prompt omits recommendation metadata and repeated custom-answer tutorial");
Check(DshSpokenInteraction.BuildQuestionPrompt(multiple, 1, 2).Contains("可多选") && DshSpokenInteraction.BuildQuestionPrompt(multiple, 1, 2).Contains("一和三"), "multi prompt states multiple choices");
Check(DshSpokenInteraction.BuildQuestionPrompt(Question(false), 0, 1).Contains("直接说答案"), "free-text prompt");
var colorPrompt = DshSpokenInteraction.BuildQuestionPrompt(colorscheme, 0, 1);
Check(colorPrompt == "你说的「换个颜色方案」是指哪一种？1：已保存的配色方案；2：换一个氛围灯效；3：随机挑一个配色。请说第几项。", "live colorscheme prompt speaks only the question, numbered labels and one instruction");
Check(!colorPrompt.Contains("第 1/1") && !colorPrompt.Contains("描述") && !colorPrompt.Contains("推荐") && colorPrompt.Length < 80, "single question omits unnecessary index and option descriptions");
var longQuestion = colorscheme with { Header = "选择配色", Question = new string('中', 500), Options = [new(new string('色', 100) + "😀", "private metadata"), new("深色", "")] };
var shortPrompt = DshSpokenInteraction.BuildQuestionPrompt(longQuestion, 0, 1);
Check(shortPrompt.StartsWith("选择配色。1：") && shortPrompt.Contains("…") && shortPrompt.Contains("详情见会话") && shortPrompt.Length < 80, "long question and labels are bounded without reading metadata");
Check(DshSpokenInteraction.SummarizeLabel("已保存的配色方案（推荐）") == "已保存的配色方案", "shared label summary removes recommendation decoration");
var emojiLabel = DshSpokenInteraction.SummarizeLabel(new string('a', 30) + "😀😀😀", 32);
Check(emojiLabel == new string('a', 30) + "😀…" && !emojiLabel.Contains('\uFFFD'), "spoken label limit preserves Unicode scalars");
Check(DshSpokenInteraction.NormalizeCommand(" 請，重新選擇！ ") == "请重新选择", "interaction commands normalize Chinese variants and punctuation");
Check(DshSpokenInteraction.NormalizeCommand("不要提交") == "不要提交", "command normalization never removes negation");
Check(DshSpokenInteraction.NormalizeCommand("ＳＵＢＭＩＴ") == "submit", "command normalization handles full-width Latin input");

foreach (var text in new[] { "", "正常的短句", new string('中', 200) + "最后一项绝不遗漏", new string('a', 55), new string('a', 56), "🧑🏽‍💻🐈‍⬛🇨🇳" + new string('中', 31) + "尾部🦄", string.Concat(Enumerable.Repeat("e\u0301", 80)), prompt, "\n  保留\t空格 和换行\r\n" })
{
    var pages = DshSpokenInteraction.BuildSubtitlePages(text);
    Check(string.Concat(pages) == text, "subtitle reassembly is lossless: " + text[..Math.Min(12, text.Length)]);
    Check(pages.All(page => Encoding.UTF8.GetByteCount(page) is > 0 and <= 55), "subtitle byte limit");
    Check(pages.All(page => !page.Contains('\uFFFD')), "subtitle Unicode not replaced");
}
var clusters = string.Concat(Enumerable.Repeat("a\u0301", 30));
Check(DshSpokenInteraction.BuildSubtitlePages(clusters, 4).All(page => page.EndsWith('\u0301')), "ordinary combining marks remain on the same page");
Check(string.Concat(DshSpokenInteraction.BuildSubtitlePages("a" + new string('\u0301', 90), 4)) == "a" + new string('\u0301', 90), "oversized cluster has lossless scalar fallback");
try { DshSpokenInteraction.BuildSubtitlePages("😀", 3); Check(false, "invalid subtitle budget rejected"); }
catch (ArgumentOutOfRangeException) { Check(true, "invalid subtitle budget rejected"); }

SubtitleSummaryProbe.Run(Check);
await SubtitleFeedbackProbe.RunAsync(Check);

Console.WriteLine($"Spoken interaction checks: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

DshTaskQuestion Question(bool multi, params string[] labels) => new("q", "界面风格", "选择界面风格", labels.Select(label => new DshTaskOption(label, "")).ToArray(), multi);
void Accepted(DshTaskQuestion question, string input, string expected)
{
    var result = DshSpokenInteraction.ParseAnswer(question, input);
    Check(result.Success && result.Answer == expected && result.Error.Length == 0, $"accept [{input}] expected [{expected}], got [{result.Answer}] {result.Error}");
}
void Rejected(DshTaskQuestion question, string input)
{
    var result = DshSpokenInteraction.ParseAnswer(question, input);
    Check(!result.Success && result.Answer.Length == 0 && result.Error.Length > 0, $"reject [{input}], got [{result.Answer}]");
}
void Check(bool success, string description)
{
    if (success) passed++;
    else { failed++; Console.Error.WriteLine("FAIL " + description); }
}
