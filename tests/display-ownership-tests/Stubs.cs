using System.Collections.Concurrent;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Scenes;

namespace HaloPixelToolBox.Core.Utilities
{
    // Only the physical HID boundary is faked. The display service and its
    // serialization/cancellation queue are the real production source.
    public sealed class HaloPixelDevice
    {
        public object? CurrentDevice { get; set; } = new();
        public bool InitializeResult { get; set; } = true;
        public int InitializeCalls { get; private set; }
        public bool ThrowOnText { get; set; }
        public bool UploadResult { get; set; } = true;
        public Action? BeforeScreenSceneWrite { get; set; }
        public ConcurrentQueue<string> Writes { get; } = new();

        public bool Initialize()
        {
            InitializeCalls++;
            if (InitializeResult)
                CurrentDevice = new();
            return InitializeResult;
        }

        public void SetTextLayout(HaloPixelTextLayout layout) => Writes.Enqueue($"layout:{layout}");
        public void ShowText(string text)
        {
            if (ThrowOnText)
                throw new IOException("Simulated HID write failure.");
            Writes.Enqueue($"text:{text}");
        }
        public void SetUIModel(HaloPixelUIModel model) => Writes.Enqueue($"ui:{model}");
        public void SetScreenScene(byte group, byte category, byte index, byte option)
        {
            BeforeScreenSceneWrite?.Invoke();
            Writes.Enqueue($"scene:{group}-{category}-{index}-{option}");
        }
        public void SetPersonalScene(byte category, byte index, string? resourceUrl)
            => Writes.Enqueue($"personal:{category}-{index}");
        public bool SetPixelSceneResource(byte category, byte index, byte[] resource,
            HaloPixelColor? backgroundColor = null, IProgress<PixelSceneUploadProgress>? uploadProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (UploadResult)
                Writes.Enqueue($"pixel:{category}-{index}");
            return UploadResult;
        }
        public bool SetDeviceVolume(int volume) => true;
        public bool TryGetDeviceVolume(out int maximum, out int current)
        {
            maximum = 16;
            current = 10;
            return true;
        }
        public bool CalibrateTime(DateTime value) => true;
    }
}
