using HaloPixelToolBox.Core.Models.DeviceControl;

namespace HaloPixelToolBox.Core.Services.DeviceControl;

public interface IHaloPixelDeviceControlService
{
    Task<DeviceCommandResult<HaloPixelDeviceStatus>> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<DeviceCommandResult> SetAmbientLightAsync(bool enabled, CancellationToken cancellationToken = default);

    Task<DeviceCommandResult> SetPixelScreenAsync(bool enabled, CancellationToken cancellationToken = default);

    Task<DeviceCommandResult> SetVolumeAsync(int volume, CancellationToken cancellationToken = default);

    Task<DeviceCommandResult> ShowTimeAsync(CancellationToken cancellationToken = default);

    Task<DeviceCommandResult> RestoreDefaultSceneAsync(CancellationToken cancellationToken = default);

    Task<DeviceCommandResult> ActivateSceneAsync(int categoryIndex, int sceneIndex, CancellationToken cancellationToken = default);

    Task<DeviceCommandResult<IReadOnlyList<HaloPixelSceneInfo>>> ListScenesAsync(
        string? category = null,
        CancellationToken cancellationToken = default);

    Task<DeviceCommandResult> ActivateSceneByPositionAsync(
        string category,
        int position,
        CancellationToken cancellationToken = default);

    Task<DeviceCommandResult> ActivateSceneByReferenceAsync(
        string category,
        string sceneReference,
        CancellationToken cancellationToken = default);

    Task<DeviceCommandResult<SubtitleControlStatus>> ShowSubtitleAsync(
        SubtitleControlRequest request,
        CancellationToken cancellationToken = default);
}
