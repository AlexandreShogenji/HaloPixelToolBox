using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HaloPixelToolBox.Core.Models.Scenes;

namespace HaloPixelToolBox.Core.Services.Scenes;

/// <summary>
/// 个性场景资源加载器。
/// 优先读取 TempoHub 缓存在用户文档目录中的 pixel_result_cache 配置，并把官方 pickle 缓存中的预览图解包成 WinUI 可加载图片。
/// 时钟类先使用 LiLyric 已验证的 11 个内置场景参数，避免官方资源 URL 协议未完整还原时出现分类错位。
/// 后续若完整还原官方资源上传协议，可继续使用 ResourceRemoteUrl 下载 .bin 场景数据并在发送控制器中组包下发。
/// </summary>
public sealed class PersonalSceneResourceLoader
{
    public static string DefaultOfficialInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "EDIFIER TempoHub");

    public static string DefaultLiLyricInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "LiLyric");

    private static readonly Regex PreviewUrlRegex = new(
        @"https://edifier-provider-oss\.edifier\.com/pixel_screen_image/[A-Za-z0-9_./-]+\.jpg",
        RegexOptions.Compiled);

    private static readonly Regex ResourceUrlRegex = new(
        @"https://edifier-provider-oss\.edifier\.com/pixel_screen_file/[A-Za-z0-9_./-]+\.bin",
        RegexOptions.Compiled);

    private static readonly IReadOnlyList<SceneCategoryPlan> CategoryPlans =
    [
        new(PersonalSceneCategory.Clock, "时钟类", 0, 11),
        new(PersonalSceneCategory.Game, "游戏类", 1, 19),
        new(PersonalSceneCategory.Work, "打工类", 2, 35),
        new(PersonalSceneCategory.Read, "读书类", 3, 16),
        new(PersonalSceneCategory.Cats, "猫咪类", 4, 18),
        new(PersonalSceneCategory.Dogs, "狗狗类", 5, 13),
        new(PersonalSceneCategory.Memes, "热梗类", 6, 46),
        new(PersonalSceneCategory.Cyber, "赛博类", 7, 16),
        new(PersonalSceneCategory.Spectrum, "频谱类", 8, 4)
    ];

    public IReadOnlyList<PersonalSceneDefinition> LoadScenes(
        string? officialInstallPath = null,
        string? liLyricInstallPath = null)
    {
        officialInstallPath ??= DefaultOfficialInstallPath;
        liLyricInstallPath ??= DefaultLiLyricInstallPath;

        var cachedScenes = LoadTempoHubCachedScenes().ToList();
        if (cachedScenes.Count > 0)
        {
            ReplaceClockScenes(cachedScenes, LoadLiLyricClockScenes(liLyricInstallPath));
            return SortScenes(cachedScenes);
        }

        var fallbackScenes = LoadFallbackScenes(officialInstallPath, liLyricInstallPath).ToList();
        ReplaceClockScenes(fallbackScenes, LoadLiLyricClockScenes(liLyricInstallPath));
        return SortScenes(fallbackScenes);
    }

    public IReadOnlyList<SceneCategoryPlan> LoadCategoryPlans() => CategoryPlans;

    private static IReadOnlyList<PersonalSceneDefinition> LoadTempoHubCachedScenes()
    {
        var configText = ReadPixelResultCacheText();
        if (string.IsNullOrWhiteSpace(configText))
            return [];

        var previewCache = LoadPreviewCacheMap();
        var scenes = new List<PersonalSceneDefinition>();

        for (var categoryPosition = 0; categoryPosition < CategoryPlans.Count; categoryPosition++)
        {
            var plan = CategoryPlans[categoryPosition];
            var section = GetCategorySection(configText, plan.DisplayName, categoryPosition);
            if (string.IsNullOrEmpty(section))
                continue;

            var previewMatches = PreviewUrlRegex.Matches(section)
                .Take(plan.Count)
                .ToList();

            for (var index = 0; index < previewMatches.Count; index++)
            {
                var previewUrl = previewMatches[index].Value;
                var resourceUrl = FindResourceUrlForPreview(section, previewMatches, index);
                previewCache.TryGetValue(previewUrl, out var previewPath);
                scenes.Add(CreateSceneDefinition(
                    plan,
                    index,
                    "EDIFIER TempoHub 缓存",
                    previewPath,
                    previewUrl,
                    ResolveOfficialPixelResourcePath(plan.CategoryIndex, index),
                    resourceUrl));
            }
        }

        return scenes;
    }

    private static string? FindResourceUrlForPreview(string section, IReadOnlyList<Match> previewMatches, int index)
    {
        var start = previewMatches[index].Index;
        var end = index + 1 < previewMatches.Count ? previewMatches[index + 1].Index : section.Length;
        if (end <= start)
            return null;

        var itemBlock = section[start..end];
        return ResourceUrlRegex.Match(itemBlock) is { Success: true } match ? match.Value : null;
    }

    private static IReadOnlyList<PersonalSceneDefinition> LoadFallbackScenes(
        string officialInstallPath,
        string liLyricInstallPath)
    {
        var scenes = new List<PersonalSceneDefinition>();
        foreach (var plan in CategoryPlans)
        {
            for (var index = 0; index < plan.Count; index++)
            {
                scenes.Add(CreateSceneDefinition(
                    plan,
                    index,
                    "EDIFIER TempoHub 参数表",
                    ResolveFallbackPreviewPath(plan.Category, index, officialInstallPath, liLyricInstallPath),
                    null,
                    ResolveOfficialPixelResourcePath(plan.CategoryIndex, index),
                    null));
            }
        }

        return scenes;
    }

    private static IReadOnlyList<PersonalSceneDefinition> LoadLiLyricClockScenes(string liLyricInstallPath)
    {
        var plan = CategoryPlans.First(item => item.Category == PersonalSceneCategory.Clock);
        var scenes = new List<PersonalSceneDefinition>(plan.Count);

        for (var index = 0; index < plan.Count; index++)
        {
            var previewPath = ResolveLiLyricClockPreviewPath(liLyricInstallPath, index);
            scenes.Add(CreateSceneDefinition(
                plan,
                index,
                "LiLyric 时钟资源",
                previewPath,
                null,
                null,
                null));
        }

        return scenes;
    }

    private static PersonalSceneDefinition CreateSceneDefinition(
        SceneCategoryPlan plan,
        int index,
        string source,
        string? previewPath,
        string? previewRemoteUrl,
        string? resourcePath,
        string? resourceRemoteUrl)
    {
        var isLiLyricClock = plan.Category == PersonalSceneCategory.Clock;
        var bundledPreviewPath = ResolveBundledPreviewPath(plan.Category, index);
        var bundledResourcePath = isLiLyricClock
            ? null
            : ResolveBundledPixelResourcePath(plan.CategoryIndex, index);

        return new PersonalSceneDefinition
        {
            Id = $"official-scene-{plan.CategoryIndex}-{index}",
            Name = $"{plan.DisplayName} {index + 1:D2}",
            Category = plan.Category,
            Source = source,
            CategoryIndex = plan.CategoryIndex,
            SceneIndex = index,
            // 时钟类切换包来自 LiLyric 的 pixel_image 逻辑：2E AA EC EF 00 09 01 F0 B4 C8 00 01 00 XX FF checksum。
            // 后续若要支持非时钟类逐项切换，需要继续还原官方 set_menu_item_used / 资源下载协议。
            ScreenSettingParameters = [0x01, (byte)plan.CategoryIndex, (byte)index, 0xff],
            PreviewPath = bundledPreviewPath ?? previewPath,
            PreviewRemoteUrl = previewRemoteUrl,
            ResourcePath = isLiLyricClock ? null : bundledResourcePath ?? resourcePath,
            ResourceRemoteUrl = isLiLyricClock ? null : resourceRemoteUrl
        };
    }

    private static string? ResolveBundledPreviewPath(PersonalSceneCategory category, int index)
    {
        // 发布版本优先使用随程序打包的预览图，TempoHub / LiLyric 目录只作为开发期补充来源。
        var categoryIndex = CategoryPlans.FirstOrDefault(plan => plan.Category == category)?.CategoryIndex;
        if (categoryIndex is null)
            return null;

        var directory = category == PersonalSceneCategory.Clock
            ? Path.Combine(AppContext.BaseDirectory, "Assets", "PersonalScenes", "ClockPreviews")
            : Path.Combine(AppContext.BaseDirectory, "Assets", "PersonalScenes", "Previews");

        foreach (var extension in new[] { ".jpg", ".png", ".gif" })
        {
            var path = Path.Combine(directory, $"{categoryIndex}_{index}{extension}");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static string? ResolveBundledPixelResourcePath(int categoryIndex, int sceneIndex)
    {
        // 官方个性场景资源固定按 分类_序号.bin 打包，避免发布后依赖用户本机 TempoHub 缓存。
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "PersonalScenes",
            "Resources",
            $"{categoryIndex}_{sceneIndex}.bin");

        return File.Exists(path) ? path : null;
    }

    private static string? ResolveOfficialPixelResourcePath(int categoryIndex, int sceneIndex)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EDIFIER TempoHub",
            "resource",
            "pixel",
            $"{categoryIndex}_{sceneIndex}.bin");

        return File.Exists(path) ? path : null;
    }

    private static void ReplaceClockScenes(List<PersonalSceneDefinition> scenes, IReadOnlyList<PersonalSceneDefinition> clockScenes)
    {
        if (clockScenes.Count == 0)
            return;

        scenes.RemoveAll(scene => scene.Category == PersonalSceneCategory.Clock);
        scenes.AddRange(clockScenes);
    }

    private static IReadOnlyList<PersonalSceneDefinition> SortScenes(IEnumerable<PersonalSceneDefinition> scenes)
    {
        var categoryOrder = CategoryPlans
            .Select((plan, index) => new { plan.Category, Index = index })
            .ToDictionary(item => item.Category, item => item.Index);

        return scenes
            .OrderBy(scene => categoryOrder.TryGetValue(scene.Category, out var order) ? order : int.MaxValue)
            .ThenBy(scene => scene.SceneIndex)
            .ToList();
    }

    private static string? ReadPixelResultCacheText()
    {
        var configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EDIFIER TempoHub",
            "cache",
            "configs");

        if (!Directory.Exists(configDirectory))
            return null;

        foreach (var file in Directory.EnumerateFiles(configDirectory, "*.pkl").OrderByDescending(file => new FileInfo(file).Length))
        {
            var bytes = File.ReadAllBytes(file);
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("pixel_result_cache_", StringComparison.Ordinal))
                return text;
        }

        return null;
    }

    private static string GetCategorySection(string configText, string categoryName, int categoryPosition)
    {
        var start = configText.IndexOf(categoryName, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var end = configText.Length;
        for (var next = categoryPosition + 1; next < CategoryPlans.Count; next++)
        {
            var nextStart = configText.IndexOf(CategoryPlans[next].DisplayName, start + categoryName.Length, StringComparison.Ordinal);
            if (nextStart > start)
            {
                end = nextStart;
                break;
            }
        }

        return configText[start..end];
    }

    private static Dictionary<string, string> LoadPreviewCacheMap()
    {
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EDIFIER TempoHub",
            "cache",
            "resources");
        var recordFile = Path.Combine(cacheRoot, "resource_cache_record_file");
        if (!File.Exists(recordFile))
            return [];

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(recordFile))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var key = root.GetProperty("key").GetString();
                if (string.IsNullOrWhiteSpace(key) || !key.Contains("/pixel_screen_image/", StringComparison.OrdinalIgnoreCase))
                    continue;

                var hashKey = root.TryGetProperty("hash_key", out var hashElement)
                    ? hashElement.GetString()
                    : Path.GetFileNameWithoutExtension(key);
                var binPath = root.GetProperty("bin_path").GetString();
                var previewPath = TryExtractCachedPreview(binPath, hashKey);
                if (!string.IsNullOrWhiteSpace(previewPath))
                    result[key] = previewPath;
            }
            catch
            {
                // 官方缓存可能正在写入，跳过损坏行；下次重新扫描即可恢复。
            }
        }

        return result;
    }

    private static string? TryExtractCachedPreview(string? binPath, string? hashKey)
    {
        if (string.IsNullOrWhiteSpace(binPath) || string.IsNullOrWhiteSpace(hashKey))
            return null;

        var normalizedPath = binPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (!File.Exists(normalizedPath))
            return null;

        var rawBytes = File.ReadAllBytes(normalizedPath);
        var imageBytes = TryExtractPickleBytePayload(rawBytes) ?? rawBytes;
        var extension = GetImageExtension(imageBytes);
        if (extension is null)
            return null;

        var outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloPixelToolBox",
            "PersonalScenePreviews");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, $"{hashKey}{extension}");
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length != imageBytes.Length)
            File.WriteAllBytes(outputPath, imageBytes);

        return outputPath;
    }

    private static byte[]? TryExtractPickleBytePayload(byte[] bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            switch (bytes[index])
            {
                case 0x42 when index + 5 <= bytes.Length:
                {
                    var length = BitConverter.ToInt32(bytes, index + 1);
                    if (length > 0 && index + 5 + length <= bytes.Length)
                        return bytes[(index + 5)..(index + 5 + length)];
                    break;
                }
                case 0x43 when index + 2 <= bytes.Length:
                {
                    var length = bytes[index + 1];
                    if (length > 0 && index + 2 + length <= bytes.Length)
                        return bytes[(index + 2)..(index + 2 + length)];
                    break;
                }
                case 0x8e when index + 9 <= bytes.Length:
                {
                    var length = BitConverter.ToInt64(bytes, index + 1);
                    if (length is > 0 and <= int.MaxValue && index + 9 + length <= bytes.Length)
                        return bytes[(index + 9)..(int)(index + 9 + length)];
                    break;
                }
            }
        }

        return null;
    }

    private static string? GetImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            return ".jpg";

        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4e
            && bytes[3] == 0x47)
            return ".png";

        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            return ".gif";

        return null;
    }

    private static string? ResolveFallbackPreviewPath(
        PersonalSceneCategory category,
        int index,
        string officialInstallPath,
        string liLyricInstallPath)
    {
        if (category == PersonalSceneCategory.Clock)
            return ResolveLiLyricClockPreviewPath(liLyricInstallPath, index);

        if (category == PersonalSceneCategory.Spectrum)
        {
            var spectrumPreview = Path.Combine(officialInstallPath, "_internal", "resource", "image", $"pixel_spectrum_{index}.png");
            if (File.Exists(spectrumPreview))
                return spectrumPreview;
        }

        return null;
    }

    private static string? ResolveLiLyricClockPreviewPath(string liLyricInstallPath, int index)
    {
        var liLyricPreview = Path.Combine(liLyricInstallPath, "view", "img", "clock", $"{index + 1}.png");
        return File.Exists(liLyricPreview) ? liLyricPreview : null;
    }
}

public sealed record SceneCategoryPlan(
    PersonalSceneCategory Category,
    string DisplayName,
    int CategoryIndex,
    int Count);
