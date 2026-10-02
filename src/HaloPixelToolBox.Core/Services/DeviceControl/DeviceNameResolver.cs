using System.Globalization;
using System.Text;
using HaloPixelToolBox.Core.Models.DeviceControl;
using Microsoft.VisualBasic;

namespace HaloPixelToolBox.Core.Services.DeviceControl;

/// <summary>
/// Shared defensive resolver for UI, voice and agent supplied device names.
/// It deliberately keeps matching deterministic: exact names and aliases win, partial matches
/// must be unique, and fuzzy matches must have a unique best distance.
/// </summary>
public static class DeviceNameResolver
{
    private static readonly string[] RandomTokens =
        ["随机", "隨機", "任意", "随便", "隨便", "来一个", "來一個", "random", "rand", "any"];

    private static readonly string[] FirstTokens =
        ["第一", "第一个", "第一個", "首个", "首個", "开头", "開頭", "最前", "first", "start"];

    private static readonly string[] LastTokens =
        ["最后", "最後", "末个", "末個", "最末", "倒数第一", "倒數第一", "last", "end"];

    private static readonly IReadOnlyDictionary<char, char> TraditionalFallback = new Dictionary<char, char>
    {
        ['場'] = '场', ['景'] = '景', ['類'] = '类', ['時'] = '时', ['鐘'] = '钟',
        ['遊'] = '游', ['戲'] = '戏', ['電'] = '电', ['競'] = '竞', ['辦'] = '办',
        ['讀'] = '读', ['書'] = '书', ['閱'] = '阅', ['學'] = '学', ['習'] = '习', ['貓'] = '猫',
        ['熱'] = '热', ['梗'] = '梗', ['賽'] = '赛', ['頻'] = '频', ['譜'] = '谱',
        ['義'] = '义', ['訂'] = '订', ['預'] = '预', ['設'] = '设', ['顏'] = '颜',
        ['顯'] = '显', ['燈'] = '灯', ['詞'] = '词', ['滾'] = '滚', ['啟'] = '启', ['閉'] = '闭',
        ['動'] = '动', ['隨'] = '随', ['個'] = '个', ['後'] = '后', ['開'] = '开',
        ['關'] = '关', ['調'] = '调', ['間'] = '间', ['號'] = '号', ['種'] = '种',
        ['這'] = '这', ['來'] = '来', ['選'] = '选', ['擇'] = '择', ['與'] = '与',
        ['為'] = '为', ['畫'] = '画', ['圖'] = '图', ['樂'] = '乐', ['雲'] = '云', ['據'] = '据',
        ['聲'] = '声', ['賀'] = '贺', ['遙'] = '遥', ['螢'] = '萤'
    };

    public static LookupResolution<T> Resolve<T>(
        string? reference,
        IEnumerable<DeviceLookupCandidate<T>> candidates,
        IEnumerable<string>? domainStopWords = null)
    {
        var items = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.DisplayName))
            .Select(candidate => new NormalizedCandidate<T>(
                candidate,
                Normalize(candidate.DisplayName, domainStopWords),
                (candidate.Aliases ?? [])
                    .Select(alias => Normalize(alias, domainStopWords))
                    .Where(alias => alias.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()))
            .ToList();

        var suggestions = items.Select(item => item.Candidate.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        var query = Normalize(reference ?? string.Empty, domainStopWords);
        if (query.Length == 0)
            return LookupResolution<T>.NotFound(suggestions, "名称不能为空");

        var exactNames = items.Where(item => item.Name == query).ToList();
        if (exactNames.Count == 1)
            return Resolved(exactNames[0], LookupMatchKind.Exact);
        if (exactNames.Count > 1)
            return Ambiguous(exactNames, "名称对应多个项目");

        var exactAliases = items.Where(item => item.Aliases.Contains(query, StringComparer.Ordinal)).ToList();
        if (exactAliases.Count == 1)
            return Resolved(exactAliases[0], LookupMatchKind.Alias);
        if (exactAliases.Count > 1)
            return Ambiguous(exactAliases, "别名对应多个项目");

        var contains = items.Where(item => EnumerateNames(item)
            .Any(name => name.Length > 0 && (name.Contains(query, StringComparison.Ordinal)
                || query.Contains(name, StringComparison.Ordinal))))
            .ToList();
        if (contains.Count == 1)
            return Resolved(contains[0], LookupMatchKind.Contains);
        if (contains.Count > 1)
            return Ambiguous(contains, "名称片段对应多个项目");

        var ranked = items
            .Select(item => new
            {
                Item = item,
                Distance = EnumerateNames(item)
                    .Where(name => name.Length > 0)
                    .Select(name => LevenshteinDistance(query, name))
                    .DefaultIfEmpty(int.MaxValue)
                    .Min()
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Item.Candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ranked.Count > 0 && ranked[0].Distance <= GetMaximumEditDistance(query.Length))
        {
            var best = ranked.Where(item => item.Distance == ranked[0].Distance).ToList();
            if (best.Count == 1)
                return Resolved(best[0].Item, LookupMatchKind.Fuzzy);

            return LookupResolution<T>.Ambiguous(
                best.Select(item => item.Item.Candidate.DisplayName).Distinct().Take(5).ToArray(),
                "模糊名称对应多个项目");
        }

        return LookupResolution<T>.NotFound(
            ranked.Take(5).Select(item => item.Item.Candidate.DisplayName).Distinct().ToArray(),
            "未找到匹配项目");
    }

    /// <summary>
    /// Resolves a user-facing one-based position. Returns false only when the reference is not
    /// an ordinal expression; a recognized but out-of-range expression returns true with an
    /// unresolved result so callers can report the precise validation error.
    /// </summary>
    public static bool TryResolvePositionReference(
        string? reference,
        int count,
        out LookupResolution<int> resolution,
        Random? random = null)
    {
        var normalized = Normalize(reference ?? string.Empty);
        if (normalized.Length == 0)
        {
            resolution = LookupResolution<int>.NotFound([], "位置不能为空");
            return false;
        }

        var ordinal = StripOrdinalDecorations(normalized);
        if (MatchesAny(normalized, RandomTokens)
            || MatchesAny(ordinal, RandomTokens)
            || normalized.Contains(BaseNormalize("随机"), StringComparison.Ordinal)
            || normalized.Contains("RANDOM", StringComparison.Ordinal))
        {
            if (count < 1)
            {
                resolution = LookupResolution<int>.NotFound([], "当前分类没有可选项目");
                return true;
            }

            var randomPosition = (random ?? Random.Shared).Next(1, count + 1);
            resolution = LookupResolution<int>.Resolved(
                randomPosition,
                $"第 {randomPosition} 个",
                LookupMatchKind.Random);
            return true;
        }

        if (MatchesAny(normalized, LastTokens)
            || MatchesAny(ordinal, LastTokens)
            || normalized.StartsWith(BaseNormalize("最后"), StringComparison.Ordinal)
            || normalized.StartsWith("LAST", StringComparison.Ordinal))
        {
            resolution = count > 0
                ? LookupResolution<int>.Resolved(count, $"第 {count} 个", LookupMatchKind.Ordinal)
                : LookupResolution<int>.NotFound([], "当前分类没有可选项目");
            return true;
        }

        if (MatchesAny(normalized, FirstTokens) || MatchesAny(ordinal, FirstTokens))
        {
            resolution = count > 0
                ? LookupResolution<int>.Resolved(1, "第 1 个", LookupMatchKind.Ordinal)
                : LookupResolution<int>.NotFound([], "当前分类没有可选项目");
            return true;
        }

        int position;
        if (!int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out position)
            && !TryParseChineseNumber(ordinal, out position))
        {
            resolution = LookupResolution<int>.NotFound([], "不是可识别的序号");
            return false;
        }

        if (position < 1 || position > count)
        {
            resolution = LookupResolution<int>.NotFound(
                count > 0 ? [$"1-{count}"] : [],
                count > 0 ? $"序号必须在 1 到 {count} 之间" : "当前分类没有可选项目");
            return true;
        }

        resolution = LookupResolution<int>.Resolved(position, $"第 {position} 个", LookupMatchKind.Ordinal);
        return true;
    }

    public static bool IsRandomReference(string? reference)
    {
        var normalized = Normalize(reference ?? string.Empty);
        return MatchesAny(normalized, RandomTokens)
            || MatchesAny(StripOrdinalDecorations(normalized), RandomTokens)
            || normalized.Contains(BaseNormalize("随机"), StringComparison.Ordinal)
            || normalized.Contains("RANDOM", StringComparison.Ordinal);
    }

    public static string Normalize(string value, IEnumerable<string>? domainStopWords = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = ToSimplifiedChinese(value.Normalize(NormalizationForm.FormKC));
        normalized = string.Concat(normalized.Where(char.IsLetterOrDigit))
            .ToUpper(CultureInfo.InvariantCulture);

        if (domainStopWords is null)
            return normalized;

        var stopWords = domainStopWords
            .Select(word => BaseNormalize(word))
            .Where(word => word.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(word => word.Length);
        foreach (var stopWord in stopWords)
            normalized = normalized.Replace(stopWord, string.Empty, StringComparison.Ordinal);

        return normalized;
    }

    private static string BaseNormalize(string value)
    {
        var normalized = ToSimplifiedChinese(value.Normalize(NormalizationForm.FormKC));
        return string.Concat(normalized.Where(char.IsLetterOrDigit))
            .ToUpper(CultureInfo.InvariantCulture);
    }

    private static string ToSimplifiedChinese(string value)
    {
        try
        {
            return Strings.StrConv(value, VbStrConv.SimplifiedChinese, 0x0804) ?? value;
        }
        catch
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
                builder.Append(TraditionalFallback.TryGetValue(character, out var simplified) ? simplified : character);
            return builder.ToString();
        }
    }

    private static bool MatchesAny(string normalized, IEnumerable<string> values)
        => values.Select(BaseNormalize).Any(value => normalized == value);

    private static string StripOrdinalDecorations(string value)
    {
        string[] decorations = ["场景", "分类", "类别", "序号", "位置", "SCENE", "CATEGORY", "第", "个", "号", "款", "类"];
        foreach (var decoration in decorations.OrderByDescending(item => item.Length))
            value = value.Replace(BaseNormalize(decoration), string.Empty, StringComparison.Ordinal);
        return value;
    }

    private static bool TryParseChineseNumber(string value, out int number)
    {
        number = 0;
        if (value.Length == 0)
            return false;

        static int Digit(char character) => character switch
        {
            '零' or '〇' => 0,
            '一' => 1,
            '二' or '两' or '兩' => 2,
            '三' => 3,
            '四' => 4,
            '五' => 5,
            '六' => 6,
            '七' => 7,
            '八' => 8,
            '九' => 9,
            _ => -1
        };

        var total = 0;
        var current = 0;
        var recognized = false;
        foreach (var character in value)
        {
            var digit = Digit(character);
            if (digit >= 0)
            {
                current = digit;
                recognized = true;
                continue;
            }

            var unit = character switch
            {
                '十' => 10,
                '百' => 100,
                _ => 0
            };
            if (unit == 0)
                return false;

            recognized = true;
            total += (current == 0 ? 1 : current) * unit;
            current = 0;
        }

        number = total + current;
        return recognized;
    }

    private static int GetMaximumEditDistance(int queryLength) => queryLength switch
    {
        <= 2 => 0,
        <= 4 => 1,
        <= 7 => 2,
        <= 11 => 3,
        _ => Math.Min(5, Math.Max(3, queryLength / 4))
    };

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
            previous[column] = column;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1);
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static IEnumerable<string> EnumerateNames<T>(NormalizedCandidate<T> item)
    {
        yield return item.Name;
        foreach (var alias in item.Aliases)
            yield return alias;
    }

    private static LookupResolution<T> Resolved<T>(NormalizedCandidate<T> item, LookupMatchKind matchKind)
        => LookupResolution<T>.Resolved(item.Candidate.Value, item.Candidate.DisplayName, matchKind);

    private static LookupResolution<T> Ambiguous<T>(IEnumerable<NormalizedCandidate<T>> items, string error)
        => LookupResolution<T>.Ambiguous(
            items.Select(item => item.Candidate.DisplayName).Distinct().Take(5).ToArray(),
            error);

    private sealed record NormalizedCandidate<T>(
        DeviceLookupCandidate<T> Candidate,
        string Name,
        IReadOnlyList<string> Aliases);
}
