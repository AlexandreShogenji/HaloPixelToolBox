namespace Microsoft.UI.Xaml
{
    // An explicit clock keeps the real service test deterministic and offline.
    public sealed class DispatcherTimer
    {
        public static List<DispatcherTimer> Instances { get; } = [];
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event EventHandler<object>? Tick;

        public DispatcherTimer() => Instances.Add(this);
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;

        public void AdvanceOneInterval()
        {
            if (IsEnabled)
                Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}

namespace HaloPixelToolBox.Core.Services.Device
{
    public sealed class HaloPixelDeviceConnectionMonitor
    {
        public static int EnumerationCount { get; private set; }
        public static bool Connected { get; set; }

        public bool IsConnected()
        {
            EnumerationCount++;
            return Connected;
        }
    }
}
