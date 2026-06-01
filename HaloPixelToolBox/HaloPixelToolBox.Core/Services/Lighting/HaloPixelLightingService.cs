using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Utilities;

namespace HaloPixelToolBox.Core.Services.Lighting;

public sealed class HaloPixelLightingService
{
    public HaloPixelDevice Device { get; }

    public HaloPixelLightingService() : this(new HaloPixelDevice())
    {
    }

    public HaloPixelLightingService(HaloPixelDevice device)
    {
        Device = device;
    }

    public bool EnsureDeviceReady() => Device.CurrentDevice is not null || Device.Initialize();

    public Task<bool> SetPixelScreenColorAsync(HaloPixelColor color, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!EnsureDeviceReady())
            return Task.FromResult(false);

        Device.SetPixelScreenColor(color);
        return Task.FromResult(true);
    }

    public Task<bool> SetAmbientLightAsync(AmbientLightOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!EnsureDeviceReady())
            return Task.FromResult(false);

        Device.SetAmbientLightEnabled(options.IsEnabled);
        if (options.IsEnabled)
            Device.SetAmbientLight(options);

        return Task.FromResult(true);
    }
}
