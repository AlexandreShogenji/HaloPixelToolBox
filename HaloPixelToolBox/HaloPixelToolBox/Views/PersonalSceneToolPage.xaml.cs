namespace HaloPixelToolBox.Views;

using HaloPixelToolBox.Core.Models.Scenes;
using HaloPixelToolBox.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

public sealed partial class PersonalSceneToolPage : Page
{
    private const double ScenePreviewMinimumWidth = 400;
    private const double ScenePreviewMaximumWidth = 600;
    private const double ScenePreviewSpacing = 12;
    private const double ScenePreviewColumnFitTolerance = 24;
    private const double ScenePreviewAspectRatio = 256.0 / 32.0;
    private const int ScenePreviewMaximumColumns = 3;

    public PersonalSceneToolPageViewModel ViewModel { get; } = new();

    public PersonalSceneToolPage()
    {
        InitializeComponent();
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

        ScenePreviewGrid.Padding = new Thickness(horizontalInset, 0, horizontalInset, 0);
        itemsPanel.ItemWidth = itemSlotWidth;
        itemsPanel.ItemHeight = Math.Ceiling(previewHeight + ScenePreviewSpacing);
    }

    private static bool IsSupportedFrame(string extension)
        => extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
}
