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
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// Display history recorded by this app process. Unlike volume and power,
    /// the current scene cannot be read back from the device. ActiveSceneName
    /// remains null whenever the last app send was another content kind.
    /// </summary>
    public HaloPixelDisplayState DisplayState { get; init; } = new(null, null);
}

public sealed record HaloPixelDisplayState(
    DisplayContentKind? LastSentContentKind,
    string? LastSentPersonalSceneName)
{
    public string Source => "application_last_successful_send";
    public bool SceneReadbackSupported => false;
}
