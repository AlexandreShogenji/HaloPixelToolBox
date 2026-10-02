namespace HaloPixelToolBox.Utilities.Helpers
{
    using Microsoft.UI.Windowing;
    using System.Runtime.InteropServices;
    using Windows.Graphics;

    public static class WindowHelper
    {
        public static IntPtr GetHwndForCurrentWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            return hwnd;
        }

        public static void ApplyInitialBounds(Window window)
        {
            var displayArea = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest)
                ?? DisplayArea.Primary;
            if (displayArea is null || displayArea.WorkArea.Width <= 0 || displayArea.WorkArea.Height <= 0)
                return;

            var workArea = displayArea.WorkArea;
            var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
            var bounds = InitialWindowBounds.Calculate(workArea.X, workArea.Y, workArea.Width, workArea.Height, dpi);
            // WorkArea offsets are display-relative. This overload keeps secondary
            // monitors and taskbars on any edge in the correct coordinate space.
            window.AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height), displayArea);
            var effectiveDpi = dpi == 0 ? 96u : dpi;
            Console.WriteLine($"主窗口初始尺寸：DPI={effectiveDpi}，逻辑={bounds.Width * 96d / effectiveDpi:0.##}×{bounds.Height * 96d / effectiveDpi:0.##}，像素={bounds.Width}×{bounds.Height}");
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
