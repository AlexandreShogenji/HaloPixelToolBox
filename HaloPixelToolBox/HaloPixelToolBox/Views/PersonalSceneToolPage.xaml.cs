namespace HaloPixelToolBox.Views;

using HaloPixelToolBox.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

public sealed partial class PersonalSceneToolPage : Page
{
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
        if (items.FirstOrDefault(item => item is StorageFile file && file.FileType.Equals(".png", StringComparison.OrdinalIgnoreCase)) is StorageFile png)
            await ViewModel.SetCustomFrameAsync(slot, png.Path);
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
}
