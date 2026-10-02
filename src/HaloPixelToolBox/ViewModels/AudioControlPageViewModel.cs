using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services.Audio;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;

namespace HaloPixelToolBox.ViewModels;

public partial class AudioControlPageViewModel : ViewModelBase
{
    private readonly DispatcherQueue? dispatcher = DispatcherQueue.GetForCurrentThread();
    private AudioControlSnapshot snapshot = App.AudioControl.Current;
    private CancellationTokenSource? debounceCancellation;
    private bool attached;
    private bool loadingDraft;
    private bool hasPendingDraft;
    private long draftGeneration;
    private long viewGeneration;
    private long snapshotRevision = -1;

    public ObservableCollection<AudioEndpointInfo> Endpoints { get; } = [];
    public ObservableCollection<AudioControlPreset> Presets { get; } = [];
    public ObservableCollection<AudioBandViewModel> Bands { get; } = [];

    [ObservableProperty] private AudioEndpointInfo? selectedEndpoint;
    [ObservableProperty] private AudioControlPreset? selectedPreset;
    [ObservableProperty] private bool enabled;
    [ObservableProperty] private double preampDb;
    [ObservableProperty] private double balance;
    [ObservableProperty] private bool autoHeadroom = true;
    [ObservableProperty] private string presetName = "我的曲线";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string operationMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity operationSeverity = InfoBarSeverity.Informational;

    public bool IsNotBusy => !IsBusy;
    public bool HasOperationMessage => !string.IsNullOrWhiteSpace(OperationMessage);
    public bool CanSetDefaultOutput => !IsBusy && SelectedEndpoint is not null;
    public bool CanApplyPreset => !IsBusy && SelectedPreset is not null;
    public bool CanDeletePreset => !IsBusy && SelectedPreset is { Id: not "flat" };
    public bool CanSavePreset => !IsBusy && !string.IsNullOrWhiteSpace(PresetName);
    public bool CanInitializeBackend => !IsBusy && snapshot.Backend.IsInstalled;
    public Visibility DownloadVisibility => snapshot.Backend.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
    public Visibility InitializeVisibility => snapshot.Backend.IsInstalled && !snapshot.Backend.IsIncludeConnected
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConfiguratorVisibility => snapshot.Backend.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public string BackendTitle => snapshot.Backend.CanApply ? Enabled ? "音频组件已就绪" : "音频组件已就绪 · 音效关闭"
        : snapshot.Backend.IsInstalled ? "音频组件待配置" : "尚未安装音频组件";
    public string BackendMessage => snapshot.Backend.Message;
    public string BackendPathInfo => snapshot.Backend.IsInstalled
        ? $"安装目录：{snapshot.Backend.InstallDirectory}\n曲线文件：{snapshot.Backend.ManagedConfigPath}"
        : "未检测到 Equalizer APO。安装并配置后，才能处理系统播放声音。";
    public string EndpointStatus
    {
        get
        {
            if (Endpoints.Count == 0)
                return "未检测到可用播放设备。";
            var defaultOutput = Endpoints.FirstOrDefault(endpoint => endpoint.IsDefault);
            var communicationsOutput = Endpoints.FirstOrDefault(endpoint => endpoint.IsDefaultCommunications);
            var routeStatus = defaultOutput is null ? "Windows 播放输出：未知。"
                : $"Windows 播放输出：{defaultOutput.Name}{(SelectedEndpoint?.Id == defaultOutput.Id ? "（当前音效目标）" : string.Empty)}";
            if (communicationsOutput is not null && defaultOutput is not null
                && communicationsOutput.Id != defaultOutput.Id)
                routeStatus += $"\n通话输出：{communicationsOutput.Name}（与播放输出不同）";
            return SelectedEndpoint is null ? "请选择要编辑音效的设备。\n" + routeStatus : routeStatus;
        }
    }
    public string EffectStatus => hasPendingDraft ? Enabled ? "正在保存曲线…" : "音效已关闭，正在保存曲线…"
        : !snapshot.Backend.CanApply ? Enabled ? "曲线可以编辑和保存，音频组件配置完成后才能生效。"
            : "音效已关闭；曲线可以编辑和保存，音频组件配置完成后才能应用。"
        : !Enabled ? "音效已关闭，保留当前曲线设置。"
        : !snapshot.Backend.ConfigurationWritten ? "组件已就绪，当前参数尚未确认写入，尚不能确认生效。"
        : "音效参数已写入，播放音频可验证效果。";
    public string PreampLabel => $"前级增益：{PreampDb:+0.0;-0.0;0.0} dB";
    public string EffectivePreampLabel
    {
        get
        {
            var actual = AutoHeadroom ? Math.Min(PreampDb, -Math.Max(0, Bands.Select(band => band.GainDb).DefaultIfEmpty(0).Max())) : PreampDb;
            return $"{(AutoHeadroom ? "自动余量下实际前级" : "实际前级")}：{actual:+0.0;-0.0;0.0} dB";
        }
    }
    public string BalanceLabel => Balance == 0 ? "声道平衡：居中"
        : $"声道平衡：偏{(Balance < 0 ? "左" : "右")} {Math.Abs(Balance):0}%";

    public AudioControlPageViewModel()
    {
        foreach (var frequency in AudioControlProfile.FrequenciesHz)
        {
            var band = new AudioBandViewModel(frequency);
            band.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AudioBandViewModel.GainDb))
                    DraftChanged();
            };
            Bands.Add(band);
        }
        ApplySnapshot(snapshot, replaceDraft: true);
    }

    public void Attach()
    {
        if (attached)
            return;
        attached = true;
        viewGeneration++;
        App.AudioControl.Changed += Service_Changed;
        ApplySnapshot(App.AudioControl.Current, replaceDraft: !hasPendingDraft);
        _ = RefreshAsync();
    }

    public void Detach()
    {
        if (!attached)
            return;
        attached = false;
        viewGeneration++;
        App.AudioControl.Changed -= Service_Changed;
        CancelDebounce();
        if (hasPendingDraft)
            _ = PersistDraftAsync(BuildProfile(), draftGeneration);
    }

    private void Service_Changed(AudioControlSnapshot next)
    {
        var generation = viewGeneration;
        Dispatch(() =>
        {
            if (attached && generation == viewGeneration)
                ApplySnapshot(next, replaceDraft: !hasPendingDraft);
        });
    }

    private void ApplySnapshot(AudioControlSnapshot next, bool replaceDraft)
    {
        if (next.Revision < snapshotRevision)
            return;
        snapshot = next;
        snapshotRevision = next.Revision;
        loadingDraft = true;
        try
        {
            var selectedEndpointId = replaceDraft ? next.Profile.EndpointId : SelectedEndpoint?.Id;
            var selectedPresetId = SelectedPreset?.Id;
            ReplaceItems(Endpoints, next.Endpoints);
            SelectedEndpoint = Endpoints.FirstOrDefault(endpoint => endpoint.Id == selectedEndpointId);
            ReplaceItems(Presets, next.Presets);
            SelectedPreset = Presets.FirstOrDefault(preset => preset.Id == selectedPresetId)
                ?? Presets.FirstOrDefault();
            if (replaceDraft)
            {
                Enabled = next.Profile.Enabled;
                PreampDb = next.Profile.PreampDb;
                Balance = next.Profile.Balance;
                AutoHeadroom = next.Profile.AutoHeadroom;
                for (var index = 0; index < Bands.Count; index++)
                    Bands[index].GainDb = next.Profile.BandGainsDb[index];
            }
            OperationMessage = next.Message == next.Backend.Message ? string.Empty : next.Message;
            OperationSeverity = next.OperationSucceeded ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        }
        finally { loadingDraft = false; }
        NotifyStatus();
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source))
            return;
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    partial void OnSelectedEndpointChanged(AudioEndpointInfo? value)
    {
        OnPropertyChanged(nameof(EndpointStatus));
        OnPropertyChanged(nameof(CanSetDefaultOutput));
        DraftChanged();
    }
    partial void OnSelectedPresetChanged(AudioControlPreset? value)
    {
        OnPropertyChanged(nameof(CanApplyPreset));
        OnPropertyChanged(nameof(CanDeletePreset));
    }
    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(BackendTitle));
        DraftChanged();
    }
    partial void OnAutoHeadroomChanged(bool value) => DraftChanged();
    partial void OnPreampDbChanged(double value)
    {
        if (!double.IsFinite(value) || value is < -30 or > 12)
        {
            PreampDb = double.IsFinite(value) ? Math.Clamp(value, -30, 12) : 0;
            return;
        }
        OnPropertyChanged(nameof(PreampLabel));
        DraftChanged();
    }
    partial void OnBalanceChanged(double value)
    {
        if (!double.IsFinite(value) || value is < -100 or > 100)
        {
            Balance = double.IsFinite(value) ? Math.Clamp(value, -100, 100) : 0;
            return;
        }
        OnPropertyChanged(nameof(BalanceLabel));
        DraftChanged();
    }
    partial void OnPresetNameChanged(string value) => OnPropertyChanged(nameof(CanSavePreset));
    partial void OnOperationMessageChanged(string value) => OnPropertyChanged(nameof(HasOperationMessage));
    partial void OnIsBusyChanged(bool value) => NotifyStatus();

    private void DraftChanged()
    {
        if (loadingDraft)
            return;
        hasPendingDraft = true;
        draftGeneration++;
        OnPropertyChanged(nameof(EffectStatus));
        OnPropertyChanged(nameof(EffectivePreampLabel));
        ScheduleDraft();
    }

    private void ScheduleDraft()
    {
        CancelDebounce();
        if (!attached || IsBusy)
            return;
        debounceCancellation = new CancellationTokenSource();
        _ = DebounceAsync(debounceCancellation.Token, draftGeneration);
    }

    private async Task DebounceAsync(CancellationToken token, long generation)
    {
        try
        {
            await Task.Delay(250, token);
            if (!token.IsCancellationRequested && generation == draftGeneration && attached && !IsBusy)
                await PersistDraftAsync(BuildProfile(), generation);
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> PersistDraftAsync(AudioControlProfile profile, long generation)
    {
        try
        {
            var next = await App.AudioControl.UpdateProfileAsync(profile);
            if (generation != draftGeneration)
                return false;
            var saved = ProfilesEqual(next.Profile, profile);
            hasPendingDraft = !saved;
            if (attached)
                ApplySnapshot(next, replaceDraft: saved);
            return saved;
        }
        catch (Exception exception)
        {
            if (attached && generation == draftGeneration)
            {
                OperationMessage = $"曲线保存未完成：{exception.Message}";
                OperationSeverity = InfoBarSeverity.Error;
                OnPropertyChanged(nameof(EffectStatus));
            }
            return false;
        }
    }

    private static bool ProfilesEqual(AudioControlProfile left, AudioControlProfile right)
        => left.Enabled == right.Enabled && left.EndpointId == right.EndpointId && left.PreampDb == right.PreampDb
        && left.Balance == right.Balance && left.AutoHeadroom == right.AutoHeadroom
        && left.BandGainsDb.SequenceEqual(right.BandGainsDb);

    private AudioControlProfile BuildProfile() => new()
    {
        Enabled = Enabled,
        EndpointId = SelectedEndpoint?.Id ?? snapshot.Profile.EndpointId,
        PreampDb = PreampDb,
        Balance = Balance,
        AutoHeadroom = AutoHeadroom,
        BandGainsDb = Bands.Select(band => band.GainDb).ToArray()
    };

    private void CancelDebounce()
    {
        debounceCancellation?.Cancel();
        debounceCancellation?.Dispose();
        debounceCancellation = null;
    }

    private async Task RunOperationAsync(Func<Task<AudioControlSnapshot>> action, bool flushDraft = true)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        CancelDebounce();
        try
        {
            if (flushDraft && hasPendingDraft)
            {
                if (!await PersistDraftAsync(BuildProfile(), draftGeneration))
                    return;
            }
            var next = await action();
            ApplySnapshot(next, replaceDraft: !hasPendingDraft);
        }
        catch (Exception exception)
        {
            OperationMessage = $"操作未完成：{exception.Message}";
            OperationSeverity = InfoBarSeverity.Error;
        }
        finally
        {
            IsBusy = false;
            if (hasPendingDraft)
                ScheduleDraft();
        }
    }

    [RelayCommand] private Task RefreshAsync() => RunOperationAsync(() => App.AudioControl.RefreshAsync());
    [RelayCommand] private Task InitializeBackendAsync() => RunOperationAsync(() => App.AudioControl.InitializeBackendAsync());
    [RelayCommand] private Task SetDefaultOutputAsync()
    {
        var id = SelectedEndpoint?.Id;
        return id is null ? Task.CompletedTask : RunOperationAsync(() => App.AudioControl.SetDefaultOutputAsync(id));
    }
    [RelayCommand] private Task ApplyPresetAsync()
    {
        var id = SelectedPreset?.Id;
        return id is null ? Task.CompletedTask : RunOperationAsync(() => App.AudioControl.ApplyPresetAsync(id));
    }
    [RelayCommand] private async Task SavePresetAsync()
    {
        var name = PresetName.Trim();
        if (name.Length == 0)
            return;
        await RunOperationAsync(() => App.AudioControl.SavePresetAsync(name));
        SelectedPreset = Presets.FirstOrDefault(preset => preset.Name == name) ?? SelectedPreset;
    }
    [RelayCommand] private Task DeletePresetAsync()
    {
        var id = SelectedPreset?.Id;
        return id is null or "flat" ? Task.CompletedTask : RunOperationAsync(() => App.AudioControl.DeletePresetAsync(id));
    }
    [RelayCommand] private void CenterBalance() => Balance = 0;
    [RelayCommand] private void OpenConfigurator() => RunLaunch(App.AudioControl.OpenConfigurator);
    [RelayCommand] private void OpenOfficialDownload() => RunLaunch(AudioControlService.OpenOfficialDownload);
    [RelayCommand] private void OpenSoundSettings() => RunLaunch(AudioControlService.OpenSoundSettings);

    private void RunLaunch(Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            OperationMessage = $"无法打开：{exception.Message}";
            OperationSeverity = InfoBarSeverity.Error;
        }
    }

    private void NotifyStatus()
    {
        foreach (var name in new[] { nameof(IsNotBusy), nameof(CanSetDefaultOutput), nameof(CanApplyPreset),
            nameof(CanDeletePreset), nameof(CanSavePreset), nameof(CanInitializeBackend), nameof(DownloadVisibility),
            nameof(InitializeVisibility), nameof(ConfiguratorVisibility), nameof(BackendTitle), nameof(BackendMessage),
            nameof(BackendPathInfo), nameof(EndpointStatus), nameof(EffectStatus), nameof(PreampLabel), nameof(EffectivePreampLabel), nameof(BalanceLabel) })
            OnPropertyChanged(name);
    }

    private void Dispatch(Action action)
    {
        if (dispatcher is null || dispatcher.HasThreadAccess)
            action();
        else
            dispatcher.TryEnqueue(() => action());
    }
}

public partial class AudioBandViewModel : ViewModelBase
{
    public string FrequencyLabel { get; }
    [ObservableProperty] private double gainDb;

    public AudioBandViewModel(double frequencyHz)
    {
        FrequencyLabel = frequencyHz < 1000 ? $"{frequencyHz:0.#} Hz" : $"{frequencyHz / 1000:0.#} kHz";
    }

    partial void OnGainDbChanged(double value)
    {
        if (!double.IsFinite(value) || value is < -12 or > 12)
            GainDb = double.IsFinite(value) ? Math.Clamp(value, -12, 12) : 0;
    }
}
