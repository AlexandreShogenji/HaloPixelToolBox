using HaloPixelToolBox.Core.Models.DeviceControl;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Core.Services.DeviceControl;

/// <summary>Resolves cosmetic catalog selections, including random choices that avoid the current item.</summary>
public static class DeviceSettingReferenceResolver
{
    public static LookupResolution<T> Resolve<T>(
        string reference,
        IReadOnlyList<DeviceLookupCandidate<T>> candidates,
        Func<T, bool>? isCurrent = null,
        IEnumerable<string>? stopWords = null,
        Random? random = null)
    {
        if (candidates.Count == 0)
            return LookupResolution<T>.NotFound([], "没有可选项目");
        if (string.IsNullOrWhiteSpace(reference))
            return LookupResolution<T>.NotFound([], "名称或序号不能为空");

        // Preserve punctuation until malformed ordinal values have been checked: '-1' must
        // never turn into the first item merely because name normalization strips the sign.
        var numericReference = reference.Normalize(System.Text.NormalizationForm.FormKC);
        var rawExact = candidates.Where(item => string.Equals(item.DisplayName.Trim(), reference.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rawExact.Length == 1)
            return LookupResolution<T>.Resolved(rawExact[0].Value, rawExact[0].DisplayName, LookupMatchKind.Exact);
        foreach (var decoration in (stopWords ?? []).Concat(["配色", "颜色", "顏色", "方案", "预设", "預設", "场景", "場景"]).OrderByDescending(word => word.Length))
            numericReference = numericReference.Replace(decoration, string.Empty, StringComparison.OrdinalIgnoreCase);
        if (Regex.IsMatch(numericReference,
            @"^\s*(?:第\s*)?[+\-−]?\s*[0-9零〇一二两兩三四五六七八九十百]+(?:\s*[.,。/:]\s*[0-9零〇一二两兩三四五六七八九十百]+)?\s*(?:个|個|项|項|套|号|號|款)?\s*$")
            && Regex.IsMatch(numericReference, @"[+\-−.,。/:]"))
            return LookupResolution<T>.NotFound([], "序号必须是正整数");

        var nameResolution = DeviceNameResolver.Resolve(reference, candidates, stopWords);
        if (nameResolution.MatchKind is LookupMatchKind.Exact or LookupMatchKind.Alias)
            return nameResolution;
        var normalized = DeviceNameResolver.Normalize(reference, stopWords);
        if (IsRandomReference(normalized))
        {
            var alternatives = isCurrent is null ? candidates : candidates.Where(item => !isCurrent(item.Value)).ToArray();
            var pool = alternatives.Count > 0 ? alternatives : candidates;
            var selected = pool[(random ?? Random.Shared).Next(pool.Count)];
            return LookupResolution<T>.Resolved(selected.Value, selected.DisplayName, LookupMatchKind.Random);
        }

        // These classifiers are common for color schemes and options as well as scenes.
        var ordinalReference = normalized.Replace("項", "项", StringComparison.Ordinal)
            .Replace("项", string.Empty, StringComparison.Ordinal)
            .Replace("套", string.Empty, StringComparison.Ordinal);
        if (DeviceNameResolver.TryResolvePositionReference(ordinalReference, candidates.Count, out var ordinal))
        {
            // The shared legacy resolver recognizes any string containing 'random'. A named
            // target with that substring must not silently become a random cosmetic change.
            if (ordinal.MatchKind == LookupMatchKind.Random)
                return nameResolution;
            if (!ordinal.IsResolved)
                return LookupResolution<T>.NotFound([], ordinal.Error);
            var selected = candidates[ordinal.Value - 1];
            return LookupResolution<T>.Resolved(selected.Value, selected.DisplayName, LookupMatchKind.Ordinal);
        }

        return nameResolution;
    }

    public static bool IsRandomReference(string reference)
    {
        var normalized = DeviceNameResolver.Normalize(reference).Replace('機', '机').Replace('項', '项');
        return Regex.IsMatch(normalized, @"^(?:(?:随机|任意|随便|RANDOM|RAND|ANY)(?:[一1]?(?:个|项|套|款))?|来一个)$");
    }
}
