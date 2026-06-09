using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Services;
using System.Text;

namespace HaloPixelToolBox.ViewModels;

public partial class CustomSubtitleToolPageViewModel : ViewModelBase
{
    private const int MaxTextDisplayUnits = 64;

    private readonly HaloPixelDisplayService displayService = new();

    private readonly HaloPixelTextLayout[] layoutMapping =
    [
        HaloPixelTextLayout.Left,
        HaloPixelTextLayout.Center,
        HaloPixelTextLayout.Right
    ];

    private readonly TextScrollDirection[] scrollMapping =
    [
        TextScrollDirection.None,
        TextScrollDirection.RightToLeft,
        TextScrollDirection.LeftToRight
    ];

    public List<string> LayoutNames { get; } = ["左对齐", "居中", "右对齐"];
    public List<string> ScrollDirectionNames { get; } = ["不滚动", "向左滚动", "向右滚动"];

    public int TextDisplayUnits => CountTextDisplayUnits(Text);
    public string TextLimitStatus => $"字数：{TextDisplayUnits}/{MaxTextDisplayUnits}（英文/数字 1，中文/日文等宽字符 2）";

    public bool IsLeftLayoutSelected => ResolveSelectedLayout() == HaloPixelTextLayout.Left;
    public bool IsCenterLayoutSelected => ResolveSelectedLayout() == HaloPixelTextLayout.Center;
    public bool IsRightLayoutSelected => ResolveSelectedLayout() == HaloPixelTextLayout.Right;

    public bool IsNoScrollSelected => ResolveSelectedScrollDirection() == TextScrollDirection.None;
    public bool IsScrollRightToLeftSelected => ResolveSelectedScrollDirection() == TextScrollDirection.RightToLeft;
    public bool IsScrollLeftToRightSelected => ResolveSelectedScrollDirection() == TextScrollDirection.LeftToRight;
    public bool IsLayoutSelectionEnabled => IsNoScrollSelected;

    [ObservableProperty]
    private string text = "Halo Pixel";

    [ObservableProperty]
    private int selectedLayoutIndex = 1;

    [ObservableProperty]
    private int selectedScrollDirectionIndex;

    [ObservableProperty]
    private bool enableTimedSend;

    [ObservableProperty]
    private int delaySeconds = 5;

    [ObservableProperty]
    private string statusMessage = "等待发送";

    partial void OnTextChanged(string value)
    {
        var normalized = value.ReplaceLineEndings(" ");
        var limited = LimitTextDisplayUnits(normalized, MaxTextDisplayUnits);
        if (!string.Equals(value, limited, StringComparison.Ordinal))
        {
            Text = limited;
            StatusMessage = "字幕内容已限制到 64 个英文/数字字符或 32 个中文/日文等宽字符";
            return;
        }

        NotifyTextLimitChanged();
    }

    partial void OnSelectedLayoutIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsLeftLayoutSelected));
        OnPropertyChanged(nameof(IsCenterLayoutSelected));
        OnPropertyChanged(nameof(IsRightLayoutSelected));
    }

    partial void OnSelectedScrollDirectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsNoScrollSelected));
        OnPropertyChanged(nameof(IsScrollRightToLeftSelected));
        OnPropertyChanged(nameof(IsScrollLeftToRightSelected));
        OnPropertyChanged(nameof(IsLayoutSelectionEnabled));
    }

    [RelayCommand]
    private void SelectLeftLayout() => SelectLayout(HaloPixelTextLayout.Left);

    [RelayCommand]
    private void SelectCenterLayout() => SelectLayout(HaloPixelTextLayout.Center);

    [RelayCommand]
    private void SelectRightLayout() => SelectLayout(HaloPixelTextLayout.Right);

    [RelayCommand]
    private void SelectNoScroll() => SelectScrollDirection(TextScrollDirection.None);

    [RelayCommand]
    private void SelectScrollRightToLeft() => SelectScrollDirection(TextScrollDirection.RightToLeft);

    [RelayCommand]
    private void SelectScrollLeftToRight() => SelectScrollDirection(TextScrollDirection.LeftToRight);

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            StatusMessage = "请输入字幕内容";
            return;
        }

        if (TextDisplayUnits > MaxTextDisplayUnits)
        {
            StatusMessage = "字幕内容过长，请控制在 64 个英文/数字字符或 32 个中文/日文等宽字符以内";
            return;
        }

        await displayService.SendTextAsync(BuildOptions());
        StatusMessage = EnableTimedSend ? $"字幕已加入定时发送：{DelaySeconds} 秒后" : "字幕已发送";
    }

    private DisplayTextOptions BuildOptions()
    {
        return new DisplayTextOptions
        {
            Text = Text,
            Layout = ResolveSelectedLayout(),
            ScrollDirection = ResolveSelectedScrollDirection(),
            SendAt = EnableTimedSend ? DateTimeOffset.Now.AddSeconds(Math.Max(0, DelaySeconds)) : null
        };
    }

    private void SelectLayout(HaloPixelTextLayout layout)
    {
        var index = Array.IndexOf(layoutMapping, layout);
        if (index >= 0)
            SelectedLayoutIndex = index;
    }

    private void SelectScrollDirection(TextScrollDirection direction)
    {
        var index = Array.IndexOf(scrollMapping, direction);
        if (index >= 0)
            SelectedScrollDirectionIndex = index;
    }

    private HaloPixelTextLayout ResolveSelectedLayout()
    {
        return layoutMapping[Math.Clamp(SelectedLayoutIndex, 0, layoutMapping.Length - 1)];
    }

    private TextScrollDirection ResolveSelectedScrollDirection()
    {
        return scrollMapping[Math.Clamp(SelectedScrollDirectionIndex, 0, scrollMapping.Length - 1)];
    }

    private void NotifyTextLimitChanged()
    {
        OnPropertyChanged(nameof(TextDisplayUnits));
        OnPropertyChanged(nameof(TextLimitStatus));
    }

    private static string LimitTextDisplayUnits(string value, int maxUnits)
    {
        var builder = new StringBuilder(value.Length);
        var units = 0;

        foreach (var character in value)
        {
            var weight = GetDisplayUnitWeight(character);
            if (units + weight > maxUnits)
                break;

            builder.Append(character);
            units += weight;
        }

        return builder.ToString();
    }

    private static int CountTextDisplayUnits(string value)
    {
        var units = 0;
        foreach (var character in value)
            units += GetDisplayUnitWeight(character);

        return units;
    }

    private static int GetDisplayUnitWeight(char character)
    {
        return IsWideCharacter(character) ? 2 : 1;
    }

    private static bool IsWideCharacter(char character)
    {
        return character is >= '\u2e80' and <= '\u9fff'
            or >= '\uf900' and <= '\ufaff'
            or >= '\uac00' and <= '\ud7af'
            or >= '\uff01' and <= '\uff60'
            or >= '\uffe0' and <= '\uffe6';
    }
}
