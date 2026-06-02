using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Scenes;

namespace HaloPixelToolBox.Core.Services.Scenes;

public sealed class PersonalSceneDisplayController
{
    private readonly HaloPixelDisplayService displayService;
    private readonly PersonalSceneRestoreService restoreService = new();

    public PersonalSceneDisplayController(HaloPixelDisplayService displayService)
    {
        this.displayService = displayService;
    }

    public async Task<bool> SendAsync(
        PersonalSceneDefinition scene,
        IProgress<PixelSceneUploadProgress>? uploadProgress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sent = false;

        if (scene.Category == PersonalSceneCategory.Clock && scene.ScreenSettingParameters is { Length: 4 } clockParameters)
        {
            // 时钟类使用 LiLyric 已验证的内置切换包，成功后记录为字幕结束时可恢复的场景。
            displayService.ShowScreenScene(clockParameters[0], clockParameters[1], clockParameters[2], clockParameters[3]);
            sent = true;
        }
        else if (scene.RequiresResourceUpload)
        {
            sent = await displayService.ShowPixelSceneAsync(scene, uploadProgress, cancellationToken);
        }
        else if (scene.ScreenSettingParameters is { Length: 4 } parameters)
        {
            displayService.ShowScreenScene(parameters[0], parameters[1], parameters[2], parameters[3]);
            sent = true;
        }
        else if (scene.BuiltInUiModel is not null)
        {
            displayService.ShowBuiltInUi(scene.BuiltInUiModel.Value, DisplayContentKind.Scene);
            sent = true;
        }

        if (sent)
            restoreService.Remember(scene);

        return sent;
    }
}
