namespace HaloPixelToolBox.Core.Models.DeviceControl;

public sealed record DeviceCommandResult(DeviceCommandStatus Status, string Message)
{
    public bool Success => Status == DeviceCommandStatus.Succeeded;

    /// <summary>
    /// Stable, transport-friendly result code. Unlike enum serialization this value does not
    /// depend on serializer settings and can safely be consumed by plugins and agents.
    /// </summary>
    public string Code => DeviceCommandCodes.FromStatus(Status);

    public static DeviceCommandResult Succeeded(string message) => new(DeviceCommandStatus.Succeeded, message);

    public static DeviceCommandResult Rejected(DeviceCommandStatus status, string message) => new(status, message);
}

public sealed record DeviceCommandResult<T>(DeviceCommandStatus Status, string Message, T? Data = default)
{
    public bool Success => Status == DeviceCommandStatus.Succeeded;

    public string Code => DeviceCommandCodes.FromStatus(Status);

    public static DeviceCommandResult<T> Succeeded(string message, T data)
        => new(DeviceCommandStatus.Succeeded, message, data);

    public static DeviceCommandResult<T> Rejected(DeviceCommandStatus status, string message)
        => new(status, message);
}

public static class DeviceCommandCodes
{
    public static string FromStatus(DeviceCommandStatus status) => status switch
    {
        DeviceCommandStatus.Succeeded => "succeeded",
        DeviceCommandStatus.DeviceOffline => "device_offline",
        DeviceCommandStatus.InvalidArgument => "invalid_argument",
        DeviceCommandStatus.NotFound => "not_found",
        DeviceCommandStatus.NotConfirmed => "not_confirmed",
        DeviceCommandStatus.NotSupported => "not_supported",
        DeviceCommandStatus.Conflict => "conflict",
        DeviceCommandStatus.Cancelled => "cancelled",
        _ => "failed"
    };
}
