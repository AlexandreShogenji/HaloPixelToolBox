namespace HaloPixelToolBox.Views;

using HaloPixelToolBox.Core.Models.Scenes;
using HaloPixelToolBox.ViewModels;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

public sealed partial class PersonalSceneToolPage : Page
{
    private const double ScenePreviewMinimumWidth = 400;
    private const double ScenePreviewMaximumWidth = 600;
    private const double ScenePreviewSpacing = 12;
    private const double ScenePreviewColumnFitTolerance = 24;
    private const double ScenePreviewAspectRatio = 256.0 / 32.0;
    private const int ScenePreviewMaximumColumns = 3;
    private const int PixelCanvasWidth = 256;
    private const int PixelCanvasHeight = 32;
    private const int PixelCanvasHistoryLimit = 30;
    private readonly bool[] pixelCanvas = new bool[PixelCanvasWidth * PixelCanvasHeight];
    private readonly Stack<bool[]> pixelCanvasUndoHistory = new();
    private readonly Stack<bool[]> pixelCanvasRedoHistory = new();
    private readonly WriteableBitmap pixelCanvasBitmap = new(PixelCanvasWidth, PixelCanvasHeight);
    private bool isPixelCanvasDrawing;
    private bool isPixelCanvasEraseMode;
    private int pixelCanvasBrushSize = 1;
    private int lastPixelCanvasX = -1;
    private int lastPixelCanvasY = -1;

    public PersonalSceneToolPageViewModel ViewModel { get; } = new();

    public PersonalSceneToolPage()
    {
        InitializeComponent();
        PixelCanvasImage.Source = pixelCanvasBitmap;
        RenderPixelCanvas();
    }

    private void PixelCanvasDraw_Click(object sender, RoutedEventArgs e)
    {
        isPixelCanvasEraseMode = false;
        PixelCanvasDrawButton.IsChecked = true;
        PixelCanvasEraseButton.IsChecked = false;
    }

    private void PixelCanvasErase_Click(object sender, RoutedEventArgs e)
    {
        isPixelCanvasEraseMode = true;
        PixelCanvasDrawButton.IsChecked = false;
        PixelCanvasEraseButton.IsChecked = true;
    }

    private void PixelCanvasBrushSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PixelCanvasBrushSizeBox.SelectedItem is ComboBoxItem { Tag: string value }
            && int.TryParse(value, out var size))
            pixelCanvasBrushSize = size;
    }

    private void PixelCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PushPixelCanvasUndoState();
        pixelCanvasRedoHistory.Clear();
        isPixelCanvasDrawing = true;
        PixelCanvasImage.CapturePointer(e.Pointer);

        var point = e.GetCurrentPoint(PixelCanvasImage);
        var (x, y) = GetPixelCanvasCoordinates(point.Position.X, point.Position.Y);
        lastPixelCanvasX = x;
        lastPixelCanvasY = y;
        DrawPixelCanvasLine(x, y, x, y, isPixelCanvasEraseMode || point.Properties.IsRightButtonPressed);
        RenderPixelCanvas();
        e.Handled = true;
    }

    private void PixelCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PixelCanvasImage);
        var (x, y) = GetPixelCanvasCoordinates(point.Position.X, point.Position.Y);
        PixelCanvasCoordinateText.Text = $"{x}, {y}";
        if (!isPixelCanvasDrawing)
            return;

        DrawPixelCanvasLine(lastPixelCanvasX, lastPixelCanvasY, x, y,
            isPixelCanvasEraseMode || point.Properties.IsRightButtonPressed);
        lastPixelCanvasX = x;
        lastPixelCanvasY = y;
        RenderPixelCanvas();
        e.Handled = true;
    }

    private void PixelCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        isPixelCanvasDrawing = false;
        lastPixelCanvasX = -1;
        lastPixelCanvasY = -1;
        PixelCanvasImage.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void PixelCanvasUndo_Click(object sender, RoutedEventArgs e)
    {
        if (pixelCanvasUndoHistory.Count == 0)
            return;

        pixelCanvasRedoHistory.Push((bool[])pixelCanvas.Clone());
        RestorePixelCanvas(pixelCanvasUndoHistory.Pop());
    }

    private void PixelCanvasRedo_Click(object sender, RoutedEventArgs e)
    {
        if (pixelCanvasRedoHistory.Count == 0)
            return;

        PushPixelCanvasUndoState();
        RestorePixelCanvas(pixelCanvasRedoHistory.Pop());
    }

    private void PixelCanvasClear_Click(object sender, RoutedEventArgs e)
    {
        if (!pixelCanvas.Any(pixel => pixel))
            return;

        PushPixelCanvasUndoState();
        pixelCanvasRedoHistory.Clear();
        Array.Clear(pixelCanvas);
        RenderPixelCanvas();
        ViewModel.CustomSceneGenerationStatus = "画板已清空";
    }

    private async void PixelCanvasImport_Click(object sender, RoutedEventArgs e)
    {
        var picker = CreateImagePicker();
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        try
        {
            PushPixelCanvasUndoState();
            pixelCanvasRedoHistory.Clear();
            await LoadImageIntoPixelCanvasAsync(file);
            RenderPixelCanvas();
            ViewModel.CustomSceneGenerationStatus = $"已导入画板：{file.Name}";
        }
        catch (Exception exception)
        {
            ViewModel.CustomSceneGenerationStatus = $"导入失败：{exception.Message}";
        }
    }

    private async void PixelCanvasSaveFrame_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SavePixelCanvasFrameAsync(CreatePixelCanvasBgra());

    private async void PixelCanvasGenerateAndSend_Click(object sender, RoutedEventArgs e)
        => await ViewModel.GenerateAndSendPixelCanvasAsync(CreatePixelCanvasBgra());

    private FileOpenPicker CreateImagePicker()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return picker;
    }

    private async Task LoadImageIntoPixelCanvasAsync(StorageFile file)
    {
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = PixelCanvasWidth,
            ScaledHeight = PixelCanvasHeight,
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        var pixels = pixelData.DetachPixelData();
        for (var index = 0; index < pixelCanvas.Length; index++)
        {
            var offset = index * 4;
            var alpha = pixels[offset + 3];
            var luminance = (pixels[offset] * 299 + pixels[offset + 1] * 587 + pixels[offset + 2] * 114) / 1000;
            pixelCanvas[index] = alpha >= 32 && luminance >= 96;
        }
    }

    private (int X, int Y) GetPixelCanvasCoordinates(double x, double y)
    {
        var pixelX = Math.Clamp((int)(x / Math.Max(1, PixelCanvasImage.ActualWidth) * PixelCanvasWidth), 0, PixelCanvasWidth - 1);
        var pixelY = Math.Clamp((int)(y / Math.Max(1, PixelCanvasImage.ActualHeight) * PixelCanvasHeight), 0, PixelCanvasHeight - 1);
        return (pixelX, pixelY);
    }

    private void DrawPixelCanvasLine(int startX, int startY, int endX, int endY, bool erase)
    {
        var dx = Math.Abs(endX - startX);
        var sx = startX < endX ? 1 : -1;
        var dy = -Math.Abs(endY - startY);
        var sy = startY < endY ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            ApplyPixelCanvasBrush(startX, startY, erase);
            if (startX == endX && startY == endY)
                break;

            var doubledError = 2 * error;
            if (doubledError >= dy)
            {
                error += dy;
                startX += sx;
            }
            if (doubledError <= dx)
            {
                error += dx;
                startY += sy;
            }
        }
    }

    private void ApplyPixelCanvasBrush(int centerX, int centerY, bool erase)
    {
        var startOffset = -(pixelCanvasBrushSize / 2);
        for (var offsetY = startOffset; offsetY < startOffset + pixelCanvasBrushSize; offsetY++)
        {
            for (var offsetX = startOffset; offsetX < startOffset + pixelCanvasBrushSize; offsetX++)
            {
                var x = centerX + offsetX;
                var y = centerY + offsetY;
                if (x >= 0 && x < PixelCanvasWidth && y >= 0 && y < PixelCanvasHeight)
                    pixelCanvas[y * PixelCanvasWidth + x] = !erase;
            }
        }
    }

    private void PushPixelCanvasUndoState()
    {
        pixelCanvasUndoHistory.Push((bool[])pixelCanvas.Clone());
        while (pixelCanvasUndoHistory.Count > PixelCanvasHistoryLimit)
        {
            var keptStates = pixelCanvasUndoHistory.Take(PixelCanvasHistoryLimit).Reverse().ToArray();
            pixelCanvasUndoHistory.Clear();
            foreach (var state in keptStates)
                pixelCanvasUndoHistory.Push(state);
        }
    }

    private void RestorePixelCanvas(bool[] state)
    {
        Array.Copy(state, pixelCanvas, pixelCanvas.Length);
        RenderPixelCanvas();
    }

    private byte[] CreatePixelCanvasBgra()
    {
        var pixels = new byte[PixelCanvasWidth * PixelCanvasHeight * 4];
        for (var index = 0; index < pixelCanvas.Length; index++)
        {
            var value = pixelCanvas[index] ? (byte)255 : (byte)0;
            var offset = index * 4;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private void RenderPixelCanvas()
    {
        var pixels = CreatePixelCanvasBgra();
        using var stream = pixelCanvasBitmap.PixelBuffer.AsStream();
        stream.Position = 0;
        stream.Write(pixels, 0, pixels.Length);
        pixelCanvasBitmap.Invalidate();
    }

    private async void SceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Core.Models.Scenes.PersonalSceneDefinition scene })
            await ViewModel.SendSceneAsync(scene);
    }

    private void CategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PersonalSceneCategoryGroup category })
            ViewModel.SelectCategory(category);
    }

    private void CustomFrame_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void CustomFrame_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CustomSceneFrameSlot slot })
            return;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        if (items.FirstOrDefault(item => item is StorageFile file && IsSupportedFrame(file.FileType)) is StorageFile image)
            await ViewModel.SetCustomFrameAsync(slot, image.Path);
    }

    private async void CustomFrameChoose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CustomSceneFrameSlot slot })
            return;

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await ViewModel.SetCustomFrameAsync(slot, file.Path);
    }

    private void CustomFrameClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CustomSceneFrameSlot slot })
            slot.ImagePath = null;
    }

    private async void GeneratedCustomScene_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SendGeneratedCustomSceneAsync();

    private void SceneDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PersonalSceneDefinition scene })
            ViewModel.DeleteScene(scene);
    }

    private void ScenePreviewGrid_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateScenePreviewLayout(ScenePreviewGrid.ActualWidth);
        DispatcherQueue.TryEnqueue(() => UpdateScenePreviewLayout(ScenePreviewGrid.ActualWidth));
    }

    private void ScenePreviewItemsPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ItemsWrapGrid itemsPanel)
            UpdateScenePreviewLayout(ScenePreviewGrid.ActualWidth, itemsPanel);
    }

    private void ScenePreviewGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateScenePreviewLayout(e.NewSize.Width);

    private void UpdateScenePreviewLayout(double availableWidth, ItemsWrapGrid? itemsPanel = null)
    {
        itemsPanel ??= ScenePreviewGrid.ItemsPanelRoot as ItemsWrapGrid;
        if (availableWidth <= 0 || itemsPanel is null)
            return;

        var columnCount = Math.Clamp(
            (int)Math.Floor(
                (availableWidth + ScenePreviewSpacing + ScenePreviewColumnFitTolerance) /
                (ScenePreviewMinimumWidth + ScenePreviewSpacing)),
            1,
            ScenePreviewMaximumColumns);
        var itemSlotWidth = Math.Min(
            ScenePreviewMaximumWidth + ScenePreviewSpacing,
            Math.Floor(availableWidth / columnCount));
        var previewWidth = Math.Max(0, itemSlotWidth - ScenePreviewSpacing);
        var previewHeight = previewWidth / ScenePreviewAspectRatio;
        var horizontalInset = Math.Max(0, Math.Floor((availableWidth - itemSlotWidth * columnCount) / 2));

        // Center only the cards; padding the GridView would also squeeze its
        // category header and the custom-scene editor footer.
        itemsPanel.Margin = new Thickness(horizontalInset, 0, horizontalInset, 0);
        itemsPanel.ItemWidth = itemSlotWidth;
        itemsPanel.ItemHeight = Math.Ceiling(previewHeight + ScenePreviewSpacing);
    }

    private static bool IsSupportedFrame(string extension)
        => extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
}
