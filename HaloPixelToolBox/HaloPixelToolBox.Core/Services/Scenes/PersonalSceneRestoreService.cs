using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Scenes;

namespace HaloPixelToolBox.Core.Services.Scenes;

/// <summary>
/// 记录当前用户最后一次成功使用的个性场景，供视频字幕等临时内容结束后恢复。
/// 没有用户选择记录时，才回退到时钟类第 10 个场景，避免固定默认场景挤占用户正在使用的场景。
/// </summary>
public sealed class PersonalSceneRestoreService
{
    private static readonly object SyncRoot = new();
    private static PersonalSceneDefinition currentScene = CreateFallbackClockScene();

    public void Remember(PersonalSceneDefinition scene)
    {
        lock (SyncRoot)
            currentScene = CloneScene(scene);
    }

    public async Task<bool> RestoreAsync(HaloPixelDisplayService displayService, CancellationToken cancellationToken = default)
    {
        PersonalSceneDefinition scene;
        lock (SyncRoot)
            scene = CloneScene(currentScene);

        cancellationToken.ThrowIfCancellationRequested();

        if (scene.RequiresResourceUpload)
            return await displayService.ShowPixelSceneAsync(scene, null, cancellationToken);

        if (scene.ScreenSettingParameters is { Length: 4 } parameters)
        {
            displayService.ShowScreenScene(parameters[0], parameters[1], parameters[2], parameters[3]);
            return true;
        }

        if (scene.BuiltInUiModel is not null)
        {
            displayService.ShowBuiltInUi(scene.BuiltInUiModel.Value, DisplayContentKind.Scene);
            return true;
        }

        return false;
    }

    private static PersonalSceneDefinition CloneScene(PersonalSceneDefinition scene)
        => new()
        {
            Id = scene.Id,
            Name = scene.Name,
            Category = scene.Category,
            Source = scene.Source,
            ResourcePath = scene.ResourcePath,
            ResourceRemoteUrl = scene.ResourceRemoteUrl,
            PreviewPath = scene.PreviewPath,
            PreviewRemoteUrl = scene.PreviewRemoteUrl,
            BuiltInUiModel = scene.BuiltInUiModel,
            ScreenSettingParameters = scene.ScreenSettingParameters is null ? null : scene.ScreenSettingParameters.ToArray(),
            CategoryIndex = scene.CategoryIndex,
            SceneIndex = scene.SceneIndex,
            UploadCategoryIndex = scene.UploadCategoryIndex,
            ContentKind = scene.ContentKind
        };

    private static PersonalSceneDefinition CreateFallbackClockScene()
        => new()
        {
            Id = "fallback-clock-scene-0-9",
            Name = "默认时钟场景",
            Category = PersonalSceneCategory.Clock,
            Source = "内置回退场景",
            CategoryIndex = 0,
            SceneIndex = 9,
            ScreenSettingParameters = [0x01, 0x00, 0x09, 0xff],
            ContentKind = DisplayContentKind.Scene
        };
}
