using HaloPixelToolBox.Core.Utilities;

namespace HaloPixelToolBox.Core.Services.Device;

public sealed class HaloPixelDeviceConnectionMonitor
{
    public bool IsConnected()
    {
        try
        {
            return HaloPixelDevice.GetPixelDevice().Any();
        }
        catch
        {
            return false;
        }
    }
}
