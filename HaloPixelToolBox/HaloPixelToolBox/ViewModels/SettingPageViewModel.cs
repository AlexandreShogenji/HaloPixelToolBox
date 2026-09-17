using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Services.Translation;
using HaloPixelToolBox.Core.Utilities;
using HaloPixelToolBox.Core.Utilities.Helpers;
using HaloPixelToolBox.Interface.Services;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Utilities;
using Microsoft.Win32;
using System.Reflection;
using Windows.System;
using XFEExtension.NetCore.FileExtension;
using XFEExtension.NetCore.WinUIHelper.Interface.Services;
using XFEExtension.NetCore.WinUIHelper.Utilities;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace HaloPixelToolBox.ViewModels;

public partial class SettingPageViewModel : ViewModelBase
{
    private const string TencentEndpoint = "tmt.tencentcloudapi.com";
    private const string TencentSecretIdVariable = "TENCENTCLOUD_SECRET_ID";
    private const string TencentSecretKeyVariable = "TENCENTCLOUD_SECRET_KEY";

    [ObservableProperty]
    private int closeButtonActionIndex = SystemProfile.MinimizeWhenClose ? 0 : 1;
    [ObservableProperty]
    bool isAutoStartEnable = SystemProfile.AutoStart;
    [ObservableProperty]
    private bool minimizeWhenOpen = SystemProfile.MinimizeWhenOpen;
    [ObservableProperty]
    string appCacheDirectory = AppPathHelper.AppCache;
    [ObservableProperty]
    string appCacheSize = FileHelper.GetDirectorySize(new(AppPathHelper.AppCache)).FileSize();
    [ObservableProperty]
    string asrModelCacheDirectory = HaloPixelCachePaths.BrowserSubtitleAsrModelRoot;
    [ObservableProperty]
    string asrModelCacheSize = FileHelper.GetDirectorySize(new(HaloPixelCachePaths.BrowserSubtitleAsrModelRoot)).FileSize();
    [ObservableProperty]
    string appDataDirectory = AppPathHelper.AppLocalData;
    [ObservableProperty]
    string appDataSize = FileHelper.GetDirectorySize(new(AppPathHelper.AppLocalData)).FileSize();
    [ObservableProperty]
    string appLogDirectory = AppPath.LogDictionary;
    [ObservableProperty]
    string appLogSize = FileHelper.GetDirectorySize(new(AppPath.LogDictionary)).FileSize();
    [ObservableProperty]
    private string currentVersion = Assembly.GetEntryAssembly()?.GetName().Version is Version version ? version.ToString(3) : "无法获取版本信息";
    [ObservableProperty]
    private string tencentCloudSecretId = string.Empty;
    [ObservableProperty]
    private string tencentCloudSecretKey = string.Empty;
    [ObservableProperty]
    private int selectedTencentRegionIndex;
    [ObservableProperty]
    private string tencentTranslationStatus = HasTencentCredentials()
        ? "已配置，展开可修改或测试"
        : "尚未配置，展开填写 SecretId 与 SecretKey";
    [ObservableProperty]
    private string tencentTranslationDetail = "配置会沿用浏览器字幕现有 Tencent TMT 调用链。";
    [ObservableProperty]
    private int quickActionSlotOneIndex = NormalizeQuickActionIndex(DisplayFeatureProfile.QuickActionSlotOneIndex);
    [ObservableProperty]
    private int quickActionSlotTwoIndex = NormalizeQuickActionIndex(DisplayFeatureProfile.QuickActionSlotTwoIndex);
    [ObservableProperty]
    private int quickActionSlotThreeIndex = NormalizeQuickActionIndex(DisplayFeatureProfile.QuickActionSlotThreeIndex);
    [ObservableProperty]
    private int quickActionSlotFourIndex = NormalizeQuickActionIndex(DisplayFeatureProfile.QuickActionSlotFourIndex);

    public List<string> TencentRegions { get; } =
    [
        "ap-shanghai",
        "ap-guangzhou",
        "ap-beijing",
        "ap-hongkong"
    ];
    public IReadOnlyList<string> QuickActionOptions { get; } =
    [
        "个性场景",
        "灯光控制",
        "歌词同步",
        "视频字幕",
        "浏览器字幕",
        "自定义字幕"
    ];
    public ISettingService SettingService { get; set; } = ServiceManager.GetService<ISettingService>();
    public IDialogService DialogService { get; set; } = ServiceManager.GetService<IDialogService>();

    public SettingPageViewModel()
    {
        var configuredEndpoint = DisplayFeatureProfile.TranslationApiEndpoint;
        var configuredRegion = configuredEndpoint
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .FirstOrDefault();
        var regionIndex = TencentRegions.IndexOf(configuredRegion ?? string.Empty);
        SelectedTencentRegionIndex = regionIndex >= 0 ? regionIndex : 0;
    }

    partial void OnCloseButtonActionIndexChanged(int value) => SystemProfile.MinimizeWhenClose = value == 0;

    partial void OnIsAutoStartEnableChanged(bool value)
    {
        SystemProfile.AutoStart = value;
        SetAutoStart(value);
    }

    partial void OnMinimizeWhenOpenChanged(bool value) => SystemProfile.MinimizeWhenOpen = value;

    partial void OnQuickActionSlotOneIndexChanged(int value) => DisplayFeatureProfile.QuickActionSlotOneIndex = NormalizeQuickActionIndex(value);

    partial void OnQuickActionSlotTwoIndexChanged(int value) => DisplayFeatureProfile.QuickActionSlotTwoIndex = NormalizeQuickActionIndex(value);

    partial void OnQuickActionSlotThreeIndexChanged(int value) => DisplayFeatureProfile.QuickActionSlotThreeIndex = NormalizeQuickActionIndex(value);

    partial void OnQuickActionSlotFourIndexChanged(int value) => DisplayFeatureProfile.QuickActionSlotFourIndex = NormalizeQuickActionIndex(value);

    private static int NormalizeQuickActionIndex(int value) => Math.Clamp(value, 0, 5);

    private static void SetAutoStart(bool enable) => SetAutoStart(enable, Assembly.GetExecutingAssembly().GetName().Name ?? "HaloPixelToolBox");

    private static void SetAutoStart(bool enable, string appName, string exePath = "")
    {
        string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
        if (enable)
        {
            if (exePath == string.Empty)
                exePath = Environment.ProcessPath ?? string.Empty;

            key?.SetValue(appName, $"\"{exePath}\"");
        }
        else
        {
            if (key?.GetValue(appName) != null)
            {
                key.DeleteValue(appName);
            }
        }
    }

    [RelayCommand]
    static void OpenPath(string originalPath) => Helper.OpenPath(originalPath);

    [RelayCommand]
    async Task ClearCache()
    {
        if (await DialogService.ShowDialog("cleanCacheContentDialog") == ContentDialogResult.Primary)
        {
            Directory.Delete(AppPathHelper.AppCache, true);
            AppCacheSize = FileHelper.GetDirectorySize(new(AppPathHelper.AppCache)).FileSize();
        }
    }

    [RelayCommand]
    static async Task LinkToGithubRepo() => await Launcher.LaunchUriAsync(new Uri("https://github.com/AlexandreShogenji/HaloPixelToolBox"));

    [RelayCommand]
    static async Task LinkToGithubIssue() => await Launcher.LaunchUriAsync(new Uri("https://github.com/AlexandreShogenji/HaloPixelToolBox/issues"));

    [RelayCommand]
    static async Task CheckUpgrade() => await ServiceManager.GetGlobalService<IUpgradeService>()!.CheckUpgrade();

    [RelayCommand]
    private void SaveTencentTranslation()
    {
        var secretId = TencentCloudSecretId.Trim();
        var secretKey = TencentCloudSecretKey.Trim();
        var hasNewSecretId = !string.IsNullOrWhiteSpace(secretId);
        var hasNewSecretKey = !string.IsNullOrWhiteSpace(secretKey);

        if (hasNewSecretId != hasNewSecretKey)
        {
            TencentTranslationStatus = "配置未保存";
            TencentTranslationDetail = "SecretId 与 SecretKey 必须同时填写。";
            return;
        }

        if (!hasNewSecretId && !HasTencentCredentials())
        {
            TencentTranslationStatus = "配置未保存";
            TencentTranslationDetail = "请先填写 SecretId 与 SecretKey。";
            return;
        }

        try
        {
            if (hasNewSecretId)
            {
                SetTencentEnvironmentVariable(TencentSecretIdVariable, secretId);
                SetTencentEnvironmentVariable(TencentSecretKeyVariable, secretKey);
            }

            DisplayFeatureProfile.TranslationApiEndpoint = BuildTencentEndpoint();
            DisplayFeatureProfile.TranslationApiKey = string.Empty;
            TencentCloudSecretId = string.Empty;
            TencentCloudSecretKey = string.Empty;
            TencentTranslationStatus = "已保存，等待连接测试";
            TencentTranslationDetail = $"Endpoint：{TencentEndpoint} · Region：{GetSelectedTencentRegion()}";
        }
        catch (Exception ex)
        {
            TencentTranslationStatus = "保存失败";
            TencentTranslationDetail = ex.Message;
        }
    }

    [RelayCommand]
    private async Task TestTencentTranslationAsync()
    {
        var secretId = TencentCloudSecretId.Trim();
        var secretKey = TencentCloudSecretKey.Trim();
        if ((!string.IsNullOrWhiteSpace(secretId) || !string.IsNullOrWhiteSpace(secretKey))
            && (string.IsNullOrWhiteSpace(secretId) || string.IsNullOrWhiteSpace(secretKey)))
        {
            TencentTranslationStatus = "测试未开始";
            TencentTranslationDetail = "SecretId 与 SecretKey 必须同时填写。";
            return;
        }

        TencentTranslationStatus = "正在测试连接";
        TencentTranslationDetail = "正在测试腾讯云翻译服务…";
        try
        {
            var apiKey = string.IsNullOrWhiteSpace(secretId) ? null : $"{secretId}:{secretKey}";
            var service = new TencentMachineTranslationService(BuildTencentEndpoint(), apiKey);
            var startedAt = DateTimeOffset.Now;
            var translated = await service.TranslateAsync("こんにちは", "ja", "zh-CN");
            var elapsed = DateTimeOffset.Now - startedAt;
            TencentTranslationStatus = "连接测试成功";
            TencentTranslationDetail = $"こんにちは → {translated} · {elapsed.TotalMilliseconds:F0} ms · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            TencentTranslationStatus = "连接测试失败";
            TencentTranslationDetail = ex.Message;
        }
    }

    [RelayCommand]
    private void ClearTencentTranslation()
    {
        try
        {
            SetTencentEnvironmentVariable(TencentSecretIdVariable, null);
            SetTencentEnvironmentVariable(TencentSecretKeyVariable, null);
            DisplayFeatureProfile.TranslationApiKey = string.Empty;
            TencentCloudSecretId = string.Empty;
            TencentCloudSecretKey = string.Empty;
            TencentTranslationStatus = "尚未配置";
            TencentTranslationDetail = "腾讯云翻译凭据已从本机配置中移除。";
        }
        catch (Exception ex)
        {
            TencentTranslationStatus = "清除失败";
            TencentTranslationDetail = ex.Message;
        }
    }

    private string BuildTencentEndpoint() => $"{TencentEndpoint}|{GetSelectedTencentRegion()}";

    private string GetSelectedTencentRegion()
        => TencentRegions[Math.Clamp(SelectedTencentRegionIndex, 0, TencentRegions.Count - 1)];

    private static bool HasTencentCredentials()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TencentSecretIdVariable))
           && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TencentSecretKeyVariable));

    private static void SetTencentEnvironmentVariable(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
    }
}
