using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.DeviceControl;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Scenes;
using HaloPixelToolBox.Core.Services.Lighting;
using HaloPixelToolBox.Core.Services.Scenes;
using HaloPixelToolBox.Core.Utilities;
using System.Text;

namespace HaloPixelToolBox.Core.Services.DeviceControl;

/// <summary>
/// Stable, typed device capabilities for UI, voice and external agent adapters.
/// Raw HID packets and device handles deliberately stay behind this boundary.
/// </summary>
public sealed class HaloPixelDeviceControlService : IHaloPixelDeviceControlService
{
    private readonly HaloPixelDevice device;
    private readonly HaloPixelLightingService lightingService;
    private readonly HaloPixelDisplayService displayService;
    private readonly PersonalSceneDisplayController sceneController;
    private readonly PersonalSceneRestoreService sceneRestoreService = new();
    private readonly PersonalSceneResourceLoader sceneLoader = new();
    private readonly CustomSceneResourceGenerationService customSceneService = new();

    private static readonly string[] CategoryStopWords =
        ["场景", "类别", "分类", "类型", "风格", "效果", "类", "款"];

    private static readonly string[] SceneStopWords =
        ["场景", "画面", "图案", "效果", "款", "个"];

    private static readonly IReadOnlyDictionary<PersonalSceneCategory, string[]> CategoryAliases =
        new Dictionary<PersonalSceneCategory, string[]>
        {
            [PersonalSceneCategory.Clock] = ["时钟", "钟表", "时间", "clock"],
            [PersonalSceneCategory.Game] = ["游戏", "电竞", "game", "gaming"],
            [PersonalSceneCategory.Work] = ["打工", "工作", "办公", "work", "office"],
            [PersonalSceneCategory.Read] = ["读书", "阅读", "学习", "read", "study"],
            [PersonalSceneCategory.Cats] = ["猫咪", "猫", "喵", "cat", "cats"],
            [PersonalSceneCategory.Dogs] = ["狗狗", "狗", "汪", "dog", "dogs"],
            [PersonalSceneCategory.Memes] = ["热梗", "梗图", "搞笑", "meme", "memes"],
            [PersonalSceneCategory.Cyber] = ["赛博", "科技", "cyber", "cyberpunk"],
            [PersonalSceneCategory.Spectrum] = ["频谱", "音频频谱", "spectrum", "visualizer"],
            [PersonalSceneCategory.Custom] = ["自定义", "自订", "自訂", "自定", "个人", "我的", "custom"]
        };

    public HaloPixelDeviceControlService()
    {
        device = new HaloPixelDevice();
        lightingService = new HaloPixelLightingService(device);
        displayService = new HaloPixelDisplayService(device);
        sceneController = new PersonalSceneDisplayController(displayService);
    }

    public async Task<DeviceCommandResult<HaloPixelDeviceStatus>> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var connected = await IsDeviceConnectedAsync(cancellationToken);
            if (!connected)
            {
                return DeviceCommandResult<HaloPixelDeviceStatus>.Succeeded(
                    "PixelBar 未连接",
                    CreateStatus(false));
            }

            var volume = await displayService.GetDeviceVolumeAsync(cancellationToken);
            var ambient = await lightingService.GetAmbientLightEnabledAsync(cancellationToken);
            var pixelScreen = await lightingService.GetPixelScreenStateAsync(cancellationToken);
            var status = CreateStatus(
                true,
                volume.Success ? volume.Current : null,
                volume.Success ? volume.Maximum : null,
                ambient.Success ? ambient.Enabled : null,
                pixelScreen.Success ? pixelScreen.Enabled : null,
                pixelScreen.Success ? pixelScreen.Color : null);
            var message = volume.Success && ambient.Success && pixelScreen.Success
                ? "已读取 PixelBar 状态"
                : "设备在线；部分状态未获得回读确认";
            return DeviceCommandResult<HaloPixelDeviceStatus>.Succeeded(message, status);
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult<HaloPixelDeviceStatus>.Rejected(DeviceCommandStatus.Cancelled, "状态查询已取消");
        }
        catch (Exception exception)
        {
            return DeviceCommandResult<HaloPixelDeviceStatus>.Rejected(
                DeviceCommandStatus.Failed,
                $"状态查询失败：{exception.Message}");
        }
    }

    public Task<DeviceCommandResult> SetAmbientLightAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
        => ExecuteConfirmedCommandAsync(
            () => lightingService.SetAmbientLightEnabledAsync(enabled, cancellationToken),
            enabled ? "氛围灯已打开" : "氛围灯已关闭",
            "氛围灯写入未通过设备回读确认",
            cancellationToken);

    public Task<DeviceCommandResult> SetPixelScreenAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
        => ExecuteConfirmedCommandAsync(
            () => lightingService.SetPixelScreenEnabledAsync(
                HaloPixelLightingService.PixelScreenPreviewColor,
                enabled,
                cancellationToken),
            enabled ? "像素屏已打开" : "像素屏已关闭",
            "像素屏写入未通过设备回读确认",
            cancellationToken);

    public async Task<DeviceCommandResult> SetVolumeAsync(
        int volume,
        CancellationToken cancellationToken = default)
    {
        if (volume is < 0 or > 16)
        {
            return DeviceCommandResult.Rejected(
                DeviceCommandStatus.InvalidArgument,
                "音量必须是 0 到 16 之间的整数");
        }

        return await ExecuteConfirmedCommandAsync(
            () => displayService.SetDeviceVolumeAsync(volume, cancellationToken),
            $"音量已设置为 {volume}/16",
            "音量写入未通过设备回读确认",
            cancellationToken);
    }

    public async Task<DeviceCommandResult> ShowTimeAsync(CancellationToken cancellationToken = default)
    {
        var calibrated = await ExecuteConfirmedCommandAsync(
            () => displayService.CalibrateDeviceTimeAsync(DateTime.Now, cancellationToken),
            "设备时间已校准",
            "设备时间校准未通过回读确认",
            cancellationToken);
        if (!calibrated.Success)
            return calibrated;

        try
        {
            return await displayService.ShowBuiltInUiAsync(
                HaloPixelUIModel.Clock,
                DisplayContentKind.Clock,
                cancellationToken: cancellationToken)
                ? DeviceCommandResult.Succeeded("设备时间已校准并切换到时钟显示")
                : DeviceCommandResult.Rejected(DeviceCommandStatus.NotConfirmed, "时钟显示切换未确认");
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult.Rejected(DeviceCommandStatus.Cancelled, "切换时钟显示已取消");
        }
        catch (Exception exception)
        {
            return DeviceCommandResult.Rejected(
                DeviceCommandStatus.Failed,
                $"切换时钟显示失败：{exception.Message}");
        }
    }

    /// <summary>
    /// Matches the UI restore action: return to the last successfully displayed personal
    /// scene, or its clock fallback. This does not select an ambient-light effect.
    /// </summary>
    public Task<DeviceCommandResult> RestoreDefaultSceneAsync(CancellationToken cancellationToken = default)
        => ExecuteConfirmedCommandAsync(
            () => sceneRestoreService.RestoreAsync(displayService, cancellationToken),
            "已恢复当前默认个性场景",
            "默认个性场景恢复未确认，请检查设备连接或场景资源",
            cancellationToken);

    public async Task<DeviceCommandResult> ActivateSceneAsync(
        int categoryIndex,
        int sceneIndex,
        CancellationToken cancellationToken = default)
    {
        if (categoryIndex is < 0 or > byte.MaxValue || sceneIndex is < 0 or > byte.MaxValue)
        {
            return DeviceCommandResult.Rejected(
                DeviceCommandStatus.InvalidArgument,
                "场景分类和序号必须是 0 到 255 之间的整数");
        }

        var scene = LoadScenes().FirstOrDefault(item =>
            item.CategoryIndex == categoryIndex && item.SceneIndex == sceneIndex);
        if (scene is null)
        {
            return DeviceCommandResult.Rejected(
                DeviceCommandStatus.NotFound,
                $"未找到场景 {categoryIndex}-{sceneIndex}");
        }

        try
        {
            if (!await IsDeviceConnectedAsync(cancellationToken))
                return DeviceCommandResult.Rejected(DeviceCommandStatus.DeviceOffline, "PixelBar 未连接");

            var sent = await sceneController.SendAsync(scene, cancellationToken: cancellationToken);
            return sent
                ? DeviceCommandResult.Succeeded($"已切换场景：{scene.Name}（{categoryIndex}-{sceneIndex}）")
                : DeviceCommandResult.Rejected(DeviceCommandStatus.NotConfirmed, $"场景切换未确认：{scene.Name}");
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult.Rejected(DeviceCommandStatus.Cancelled, "场景切换已取消");
        }
        catch (Exception exception)
        {
            return DeviceCommandResult.Rejected(
                DeviceCommandStatus.Failed,
                $"场景切换失败：{exception.Message}");
        }
    }

    public Task<DeviceCommandResult<IReadOnlyList<HaloPixelSceneInfo>>> ListScenesAsync(
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scenes = LoadScenes();
        if (!string.IsNullOrWhiteSpace(category))
        {
            var resolvedCategory = ResolveCategory(category);
            if (!resolvedCategory.IsResolved)
            {
                return Task.FromResult(DeviceCommandResult<IReadOnlyList<HaloPixelSceneInfo>>.Rejected(
                    resolvedCategory.IsAmbiguous ? DeviceCommandStatus.Conflict : DeviceCommandStatus.NotFound,
                    FormatLookupError($"场景分类“{category}”", resolvedCategory)));
            }

            scenes = scenes.Where(scene => scene.Category == resolvedCategory.Value!.Category).ToList();
        }

        var plans = sceneLoader.LoadCategoryPlans().ToDictionary(plan => plan.Category);
        var result = scenes
            .GroupBy(scene => scene.Category)
            .SelectMany(group => group
                .OrderBy(scene => scene.SceneIndex)
                .Select((scene, index) => new HaloPixelSceneInfo(
                    scene.Name,
                    plans.TryGetValue(scene.Category, out var plan) ? plan.DisplayName : scene.Category.ToString(),
                    scene.CategoryIndex,
                    index + 1,
                    scene.SceneIndex,
                    scene.RequiresResourceUpload)))
            .ToList();
        return Task.FromResult(DeviceCommandResult<IReadOnlyList<HaloPixelSceneInfo>>.Succeeded(
            string.IsNullOrWhiteSpace(category)
                ? $"已读取 {result.Count} 个场景"
                : $"已读取“{category}”分类的 {result.Count} 个场景",
            result));
    }

    public async Task<DeviceCommandResult> ActivateSceneByPositionAsync(
        string category,
        int position,
        CancellationToken cancellationToken = default)
    {
        if (position < 1)
            return DeviceCommandResult.Rejected(DeviceCommandStatus.InvalidArgument, "场景位置从 1 开始");

        return await ActivateSceneByReferenceAsync(
            category,
            position.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public async Task<DeviceCommandResult> ActivateSceneByReferenceAsync(
        string category,
        string sceneReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(category))
            return DeviceCommandResult.Rejected(DeviceCommandStatus.InvalidArgument, "场景分类不能为空");
        if (string.IsNullOrWhiteSpace(sceneReference))
            return DeviceCommandResult.Rejected(DeviceCommandStatus.InvalidArgument, "场景名称或序号不能为空");

        var categoryResolution = ResolveCategory(category);
        if (!categoryResolution.IsResolved)
        {
            return DeviceCommandResult.Rejected(
                categoryResolution.IsAmbiguous ? DeviceCommandStatus.Conflict : DeviceCommandStatus.NotFound,
                FormatLookupError($"场景分类“{category}”", categoryResolution));
        }

        var plan = categoryResolution.Value!;
        var scenes = LoadScenes()
            .Where(item => item.Category == plan.Category)
            .OrderBy(item => item.SceneIndex)
            .ToList();
        if (scenes.Count == 0)
            return DeviceCommandResult.Rejected(DeviceCommandStatus.NotFound, $"“{plan.DisplayName}”没有可用场景");

        PersonalSceneDefinition scene;
        int position;
        if (DeviceNameResolver.TryResolvePositionReference(sceneReference, scenes.Count, out var positionResolution))
        {
            if (!positionResolution.IsResolved)
            {
                return DeviceCommandResult.Rejected(
                    DeviceCommandStatus.InvalidArgument,
                    $"无法解析“{sceneReference}”：{positionResolution.Error}。可选位置：1-{scenes.Count}");
            }

            position = positionResolution.Value;
            scene = scenes[position - 1];
        }
        else
        {
            var sceneCandidates = scenes.Select((item, index) => new DeviceLookupCandidate<PersonalSceneDefinition>(
                item,
                item.Name,
                string.IsNullOrWhiteSpace(item.Id)
                    ? [$"第{index + 1}个", (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)]
                    : [item.Id, $"第{index + 1}个", (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)]));
            var stopWords = SceneStopWords.Concat([plan.DisplayName, plan.Category.ToString()]);
            var sceneResolution = DeviceNameResolver.Resolve(sceneReference, sceneCandidates, stopWords);
            if (!sceneResolution.IsResolved)
            {
                return DeviceCommandResult.Rejected(
                    sceneResolution.IsAmbiguous ? DeviceCommandStatus.Conflict : DeviceCommandStatus.NotFound,
                    FormatLookupError($"“{plan.DisplayName}”中的场景“{sceneReference}”", sceneResolution));
            }

            scene = sceneResolution.Value!;
            position = scenes.IndexOf(scene) + 1;
        }

        var activated = await ActivateSceneAsync(scene.CategoryIndex, scene.SceneIndex, cancellationToken);
        return activated.Success
            ? DeviceCommandResult.Succeeded(
                $"已切换场景：{plan.DisplayName} / {scene.Name}（第 {position} 个，设备索引 {scene.CategoryIndex}-{scene.SceneIndex}）")
            : activated;
    }

    public async Task<DeviceCommandResult<SubtitleControlStatus>> ShowSubtitleAsync(
        SubtitleControlRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
        {
            return DeviceCommandResult<SubtitleControlStatus>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                "字幕内容不能为空");
        }

        if (!Enum.IsDefined(request.Layout) || !Enum.IsDefined(request.ScrollDirection))
        {
            return DeviceCommandResult<SubtitleControlStatus>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                "字幕对齐方式或滚动方向无效");
        }

        var utf8ByteCount = Encoding.UTF8.GetByteCount(request.Text);
        if (utf8ByteCount > 55)
        {
            return DeviceCommandResult<SubtitleControlStatus>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                $"字幕 UTF-8 长度为 {utf8ByteCount} 字节，最多支持 55 字节");
        }

        var effectiveScroll = request.ScrollDirection switch
        {
            TextScrollDirection.None when request.Layout == HaloPixelTextLayout.ScrollLeftToRight
                => TextScrollDirection.LeftToRight,
            TextScrollDirection.None when request.Layout == HaloPixelTextLayout.ScrollRightToLeft
                => TextScrollDirection.RightToLeft,
            _ => request.ScrollDirection
        };
        var effectiveLayout = effectiveScroll switch
        {
            TextScrollDirection.LeftToRight => HaloPixelTextLayout.ScrollLeftToRight,
            TextScrollDirection.RightToLeft => HaloPixelTextLayout.ScrollRightToLeft,
            _ => request.Layout
        };
        var status = new SubtitleControlStatus(
            request.Text,
            utf8ByteCount,
            effectiveLayout,
            effectiveScroll);

        try
        {
            if (!await IsDeviceConnectedAsync(cancellationToken))
            {
                return DeviceCommandResult<SubtitleControlStatus>.Rejected(
                    DeviceCommandStatus.DeviceOffline,
                    "PixelBar 未连接");
            }

            var sent = await displayService.SendTextAsync(new DisplayTextOptions
            {
                Text = request.Text,
                Source = DisplayContentKind.Custom,
                Layout = request.Layout,
                ScrollDirection = request.ScrollDirection
            }, cancellationToken);
            return sent
                ? DeviceCommandResult<SubtitleControlStatus>.Succeeded("字幕已发送到 PixelBar", status)
                : DeviceCommandResult<SubtitleControlStatus>.Rejected(
                    DeviceCommandStatus.NotConfirmed,
                    "字幕发送未通过设备确认");
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult<SubtitleControlStatus>.Rejected(
                DeviceCommandStatus.Cancelled,
                "字幕发送已取消");
        }
        catch (Exception exception)
        {
            return DeviceCommandResult<SubtitleControlStatus>.Rejected(
                DeviceCommandStatus.Failed,
                $"字幕发送失败：{exception.Message}");
        }
    }

    private async Task<DeviceCommandResult> ExecuteConfirmedCommandAsync(
        Func<Task<bool>> command,
        string successMessage,
        string unconfirmedMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await IsDeviceConnectedAsync(cancellationToken))
                return DeviceCommandResult.Rejected(DeviceCommandStatus.DeviceOffline, "PixelBar 未连接");

            return await command()
                ? DeviceCommandResult.Succeeded(successMessage)
                : DeviceCommandResult.Rejected(DeviceCommandStatus.NotConfirmed, unconfirmedMessage);
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult.Rejected(DeviceCommandStatus.Cancelled, "设备操作已取消");
        }
        catch (Exception exception)
        {
            return DeviceCommandResult.Rejected(
                DeviceCommandStatus.Failed,
                $"设备操作失败：{exception.Message}");
        }
    }

    private Task<bool> IsDeviceConnectedAsync(CancellationToken cancellationToken)
        => HaloPixelDeviceOperationQueue.RunAsync(
            () => device.Initialize(),
            cancellationToken);

    private IReadOnlyList<PersonalSceneDefinition> LoadScenes()
    {
        var generated = customSceneService.LoadGeneratedScene();
        return (generated is null ? [] : new[] { generated })
            .Concat(customSceneService.LoadPinnedScenes())
            .Concat(sceneLoader.LoadScenes())
            .GroupBy(scene => (scene.CategoryIndex, scene.SceneIndex))
            .Select(group => group.First())
            .ToList();
    }

    private LookupResolution<SceneCategoryPlan> ResolveCategory(string category)
    {
        var plans = sceneLoader.LoadCategoryPlans();
        if (DeviceNameResolver.TryResolvePositionReference(category, plans.Count, out var positionResolution))
        {
            if (!positionResolution.IsResolved)
            {
                return LookupResolution<SceneCategoryPlan>.NotFound(
                    plans.Select(plan => plan.DisplayName).ToArray(),
                    positionResolution.Error);
            }

            var plan = plans[positionResolution.Value - 1];
            return LookupResolution<SceneCategoryPlan>.Resolved(
                plan,
                plan.DisplayName,
                positionResolution.MatchKind);
        }

        var candidates = plans.Select(plan => new DeviceLookupCandidate<SceneCategoryPlan>(
            plan,
            plan.DisplayName,
            CategoryAliases.TryGetValue(plan.Category, out var aliases)
                ? aliases.Concat([plan.Category.ToString()]).ToArray()
                : [plan.Category.ToString()]));
        return DeviceNameResolver.Resolve(category, candidates, CategoryStopWords);
    }

    private static string FormatLookupError<T>(string subject, LookupResolution<T> resolution)
    {
        var reason = resolution.IsAmbiguous ? "存在多个匹配" : resolution.Error ?? "未找到匹配";
        var suggestions = resolution.Suggestions.Count > 0
            ? $"。候选：{string.Join("、", resolution.Suggestions)}"
            : string.Empty;
        return $"无法解析{subject}：{reason}{suggestions}";
    }

    private static HaloPixelDeviceStatus CreateStatus(
        bool connected,
        int? volume = null,
        int? maximumVolume = null,
        bool? ambientLightEnabled = null,
        bool? pixelScreenEnabled = null,
        HaloPixelColor? pixelScreenColor = null)
    {
        var lastContent = HaloPixelDisplayService.LastContentSent;
        return new HaloPixelDeviceStatus(
            connected,
            volume,
            maximumVolume,
            ambientLightEnabled,
            pixelScreenEnabled,
            pixelScreenColor,
            lastContent?.ContentKind == DisplayContentKind.Scene ? lastContent.SceneName : null,
            DateTimeOffset.Now)
        {
            DisplayState = new HaloPixelDisplayState(
                lastContent?.ContentKind,
                new PersonalSceneRestoreService().GetLastRememberedScene()?.Name)
        };
    }
}
