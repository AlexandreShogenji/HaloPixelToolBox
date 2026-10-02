using HaloPixelToolBox.Core.Models.Display;

namespace HaloPixelToolBox.Core.Models.DeviceControl;

public sealed record HaloPixelDeviceStatus(
    bool IsConnected,
    int? Volume,
    int? MaximumVolume,
    bool? AmbientLightEnabled,
    bool? PixelScreenEnabled,
    HaloPixelColor? PixelScreenColor,
    string? ActiveSceneName,
    DateTimeOffset ObservedAt);
