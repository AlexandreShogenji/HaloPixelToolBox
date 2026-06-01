using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Scenes;

namespace HaloPixelToolBox.Core.Services.Scenes;

public sealed class PersonalSceneDisplayController
{
    private readonly HaloPixelDisplayService displayService;

    public PersonalSceneDisplayController(HaloPixelDisplayService displayService)
    {
        this.displayService = displayService;
    }

    public async Task<bool> SendAsync(PersonalSceneDefinition scene, IProgress<PixelSceneUploadProgress>? uploadProgress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (scene.Category == PersonalSceneCategory.Clock && scene.ScreenSettingParameters is { Length: 4 } clockParameters)
        {
            // 时钟类使用 LiLyric 已验证的内置切换包，避免官方资源上传协议未完成时串到默认资源。
            displayService.ShowScreenScene(clockParameters[0], clockParameters[1], clockParameters[2], clockParameters[3]);
            return true;
        }

        if (scene.RequiresResourceUpload)
            return await displayService.ShowPixelSceneAsync(scene, uploadProgress, cancellationToken);

        if (scene.BuiltInUiModel is null)
        {
            if (scene.ScreenSettingParameters is not { Length: 4 } parameters)
            {
                // 扩展点：后续若拿到真实图像/动画帧协议，可在这里转换为 HID 数据包发送。
                return false;
            }

            displayService.ShowScreenScene(parameters[0], parameters[1], parameters[2], parameters[3]);
            return true;
        }

        displayService.ShowBuiltInUi(scene.BuiltInUiModel.Value, DisplayContentKind.Scene);
        return true;
    }
}
