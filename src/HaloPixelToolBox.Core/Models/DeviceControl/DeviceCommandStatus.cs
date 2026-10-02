namespace HaloPixelToolBox.Core.Models.DeviceControl;

public enum DeviceCommandStatus
{
    Succeeded,
    DeviceOffline,
    InvalidArgument,
    NotFound,
    NotConfirmed,
    Cancelled,
    Failed,
    NotSupported,
    Conflict
}
