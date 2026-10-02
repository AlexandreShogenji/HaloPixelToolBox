using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Services.DeviceControl;
using HaloPixelToolBox.Core.Models.DeviceControl;
using System.Text;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

/// <summary>Hardware animation modes, separate from saved lighting color presets.</summary>
internal static class AmbientLightEffectResolver
{
    private static readonly AmbientLightEffectCatalogItem[] Items =
    [
        new(1, "breathing", "氛围呼吸"),
        new(2, "colorTide", "幻彩潮汐"),
        new(3, "static", "纯色静光"),
        new(4, "ripple", "炫彩涟漪"),
        new(5, "flow", "流光逐影"),
        new(6, "dynamic", "动态光影")
    ];

    private static readonly string[][] Aliases =
    [
        ["呼吸", "呼吸灯", "呼吸光", "breath"],
        ["潮汐", "幻彩", "彩色潮汐", "color tide", "colour tide"],
        ["纯色", "静光", "静态", "常亮", "固定", "静止", "solid", "steady"],
        ["涟漪", "炫彩", "波纹", "波浪"],
        ["流光", "逐影", "流动", "流水", "光影", "flowing"],
        ["动态", "光影", "动态光", "dynamic light"]
    ];

    private static readonly string[] StopWords = ["氛围灯效", "氛围灯", "灯效模式", "灯光效果", "灯效", "效果", "模式"];

    public static IReadOnlyList<AmbientLightEffectCatalogItem> Catalog { get; } = Array.AsReadOnly(Items);

    public static AmbientLightEffectCatalogItem GetItem(AmbientLightEffect effect)
        => Items[Math.Clamp((int)effect, 1, Items.Length) - 1];

    public static AmbientLightEffectSelection Parse(string mode, string? reference)
    {
        if (string.IsNullOrWhiteSpace(mode))
            throw new ArgumentException("mode 必须是 set、next、previous 或 random");
        var normalizedMode = mode.Trim().ToLowerInvariant();
        if (normalizedMode is not ("set" or "next" or "previous" or "random"))
            throw new ArgumentException("mode 必须是 set、next、previous 或 random");
        if (normalizedMode != "set")
        {
            if (reference is not null)
                throw new ArgumentException("只有 set 模式可以提供 effect；next、previous、random 不接受 effect");
            return new(normalizedMode, null);
        }
        if (string.IsNullOrWhiteSpace(reference))
            throw new ArgumentException("set 模式必须提供 effect（灯效名称、英文 key 或从 1 开始的序号）");

        // Name normalization removes punctuation. Check malformed numeric expressions first so
        // '-1' cannot become position 1 and '0.2' cannot become position 2.
        var numericReference = reference.Normalize(NormalizationForm.FormKC);
        if (Regex.IsMatch(numericReference,
                @"[+\-−]\s*[0-9零〇一二两兩三四五六七八九十百]+|[0-9零〇一二两兩三四五六七八九十百]+\s*[.,。/:]\s*[0-9零〇一二两兩三四五六七八九十百]+"))
            throw new ArgumentException("灯效序号必须是从 1 到 6 的正整数，不能使用负数、小数或数值表达式");

        // StrConv's simplified-Chinese conversion is not available in every Windows runtime.
        // Keep the hardware mode vocabulary reliable without changing the shared name resolver.
        var simplifiedReference = numericReference.Replace('圍', '围').Replace('純', '纯')
            .Replace('靜', '静').Replace('態', '态').Replace('漣', '涟');
        var normalizedReference = DeviceNameResolver.Normalize(simplifiedReference, StopWords);
        if (DeviceNameResolver.IsRandomReference(normalizedReference))
            throw new ArgumentException("随机灯效请使用 mode=random，不要在 set 模式中提供随机名称");
        if (DeviceNameResolver.TryResolvePositionReference(normalizedReference, Items.Length, out var ordinal))
        {
            if (!ordinal.IsResolved)
                throw new ArgumentException($"无法解析灯效序号：{ordinal.Error}");
            return new("set", (AmbientLightEffect)ordinal.Value);
        }

        var resolution = DeviceNameResolver.Resolve(
            normalizedReference,
            Items.Select((item, index) => new DeviceLookupCandidate<AmbientLightEffect>(
                (AmbientLightEffect)item.Position, item.Name, Aliases[index].Concat([item.Key]).ToArray())),
            StopWords);
        if (!resolution.IsResolved)
        {
            var candidates = resolution.Suggestions.Count > 0 ? string.Join("、", resolution.Suggestions) : string.Join("、", Items.Select(item => item.Name));
            throw new ArgumentException($"{(resolution.IsAmbiguous ? "灯效名称不明确" : "未找到灯效")}“{reference}”；可选：{candidates}");
        }
        return new("set", resolution.Value);
    }

    public static AmbientLightEffect Select(AmbientLightEffectSelection selection, AmbientLightEffect current)
    {
        var position = Math.Clamp((int)current, 1, Items.Length);
        return selection.Mode switch
        {
            "set" => selection.Effect!.Value,
            "next" => (AmbientLightEffect)(position % Items.Length + 1),
            "previous" => (AmbientLightEffect)((position + Items.Length - 2) % Items.Length + 1),
            // Choose one of the other five modes; random never immediately repeats the current effect.
            "random" => (AmbientLightEffect)((position - 1 + Random.Shared.Next(1, Items.Length)) % Items.Length + 1),
            _ => throw new ArgumentException("无效灯效模式")
        };
    }
}

internal sealed record AmbientLightEffectCatalogItem(int Position, string Key, string Name);

internal sealed record AmbientLightEffectSelection(string Mode, AmbientLightEffect? Effect);
