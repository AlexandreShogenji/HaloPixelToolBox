using HaloPixelToolBox.Core.Services.Device;

namespace HaloPixelToolBox.Services;

/// <summary>
/// One UI-thread device check shared by the shell and dashboard. This is only
/// a display status cache; actual device operations keep their own validation.
/// </summary>
public sealed class DeviceConnectionStatusService
{
    private readonly HaloPixelDeviceConnectionMonitor monitor = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public static DeviceConnectionStatusService Shared { get; } = new();
    public bool IsConnected { get; private set; }
    public event EventHandler<bool>? StatusRefreshed;

    private DeviceConnectionStatusService()
    {
        timer.Tick += (_, _) => Refresh();
        WindowActivityService.VisibilityChanged += WindowVisibilityChanged;
        if (WindowActivityService.IsVisible)
        {
            Refresh();
            timer.Start();
        }
    }

    public void Refresh()
    {
        IsConnected = monitor.IsConnected();
        StatusRefreshed?.Invoke(this, IsConnected);
    }

    private void WindowVisibilityChanged(object? sender, bool isVisible)
    {
        if (isVisible)
        {
            Refresh();
            timer.Start();
        }
        else
        {
            timer.Stop();
        }
    }
}
