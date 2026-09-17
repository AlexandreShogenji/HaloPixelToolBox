using HaloPixelToolBox.Models;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace HaloPixelToolBox.Views;

public sealed partial class LightingToolPage : Page
{
    private bool isInitializing = true;

    public LightingToolPageViewModel ViewModel { get; } = new();

    public LightingToolPage()
    {
        InitializeComponent();
        isInitializing = false;
    }

    private void AmbientColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!isInitializing)
            ViewModel.SetAmbientColor(args.NewColor);
    }

    private void PixelColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!isInitializing)
            ViewModel.SetPixelColor(args.NewColor);
    }

    private async void SaveCurrentColorPreset_Click(object sender, RoutedEventArgs e)
    {
        var nameInput = new TextBox
        {
            Header = "方案名称",
            PlaceholderText = "例如：夜间紫蓝",
            MaxLength = 32
        };
        var dialog = CreateDialog("保存当前色彩方案", nameInput);
        dialog.PrimaryButtonText = "保存";
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Primary;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (!ViewModel.SaveCurrentColorPreset(nameInput.Text, out var error))
            await ShowErrorDialogAsync(error);
    }

    private async void ApplyColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LightingColorPreset preset })
            await ViewModel.ApplyColorPresetAsync(preset);
    }

    private void ManageColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button anchorButton
            || anchorButton.Tag is not LightingColorPreset preset)
            return;

        ViewModel.SelectColorPreset(preset);

        var nameInput = new TextBox
        {
            Header = "方案名称",
            Text = ViewModel.SelectedColorPresetName,
            MaxLength = 32
        };
        var saveNameButton = new Button
        {
            Content = "保存名称",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var overwriteButton = new Button
        {
            Content = "用当前颜色覆盖",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var deleteButton = new Button
        {
            Content = "删除此方案",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var validationMessage = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var flyoutContent = new StackPanel
        {
            Spacing = 10,
            Width = Math.Clamp(anchorButton.XamlRoot.Size.Width - 48, 220, 320)
        };
        flyoutContent.Children.Add(new TextBlock
        {
            Text = "管理色彩方案",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        flyoutContent.Children.Add(new TextBlock
        {
            Text = "可单独保存名称，或用当前两块主色板的颜色覆盖此方案。",
            TextWrapping = TextWrapping.Wrap
        });
        flyoutContent.Children.Add(nameInput);
        flyoutContent.Children.Add(validationMessage);
        flyoutContent.Children.Add(saveNameButton);
        flyoutContent.Children.Add(overwriteButton);
        flyoutContent.Children.Add(deleteButton);

        var flyout = new Flyout
        {
            Content = flyoutContent,
            Placement = FlyoutPlacementMode.Bottom
        };

        var deleteRequested = false;
        nameInput.TextChanged += (_, _) => validationMessage.Visibility = Visibility.Collapsed;
        saveNameButton.Click += (_, _) =>
        {
            ViewModel.SelectedColorPresetName = nameInput.Text;
            if (ViewModel.RenameSelectedColorPreset(out var renameError))
            {
                flyout.Hide();
                return;
            }

            validationMessage.Text = renameError;
            validationMessage.Visibility = Visibility.Visible;
        };
        overwriteButton.Click += (_, _) =>
        {
            if (ViewModel.OverwriteSelectedColorPreset(out var overwriteError))
            {
                flyout.Hide();
                return;
            }

            validationMessage.Text = overwriteError;
            validationMessage.Visibility = Visibility.Visible;
        };
        deleteButton.Click += (_, _) =>
        {
            deleteRequested = true;
            flyout.Hide();
        };
        flyout.Closed += async (_, _) =>
        {
            if (ViewModel.SelectedColorPreset is { } selectedPreset)
                ViewModel.SelectedColorPresetName = selectedPreset.Name;

            if (deleteRequested)
                await ConfirmDeleteColorPresetAsync(preset);
        };

        flyout.ShowAt(anchorButton);
    }

    private async Task ConfirmDeleteColorPresetAsync(LightingColorPreset preset)
    {
        ViewModel.SelectColorPreset(preset);
        if (ViewModel.SelectedColorPreset is not { } selectedPreset)
            return;

        var dialog = CreateDialog(
            "删除色彩方案",
            $"确定删除“{selectedPreset.Name}”吗？此操作无法撤销。");
        dialog.PrimaryButtonText = "删除";
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Close;

        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && !ViewModel.DeleteSelectedColorPreset())
        {
            await ShowErrorDialogAsync(ViewModel.StatusMessage);
        }
    }

    private ContentDialog CreateDialog(string title, object content)
        => new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content
        };

    private async Task ShowErrorDialogAsync(string error)
    {
        var dialog = CreateDialog(
            "无法完成操作",
            string.IsNullOrWhiteSpace(error) ? "请检查输入后重试。" : error);
        dialog.CloseButtonText = "确定";
        await dialog.ShowAsync();
    }
}
