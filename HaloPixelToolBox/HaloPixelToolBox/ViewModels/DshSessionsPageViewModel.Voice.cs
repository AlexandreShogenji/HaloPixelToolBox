using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Services;

namespace HaloPixelToolBox.ViewModels;

public partial class DshSessionsPageViewModel
{
    private CancellationTokenSource? voiceActionCancellation;
    private int voiceActionGeneration;
    private bool voiceActionIsStopping;
    private string voiceOperationStatus = string.Empty;
    private string voiceOperationDetail = string.Empty;
    [ObservableProperty] private VoiceAgentSnapshot voiceSnapshot = VoiceAgentSnapshot.Initial;
    [ObservableProperty] private bool isVoiceActionBusy;

    private bool IsVoiceTransitioning => VoiceSnapshot.Phase is VoiceAgentPhase.Starting
        or VoiceAgentPhase.LoadingModel or VoiceAgentPhase.Stopping;
    public bool CanToggleVoice => attached && pageCancellation is not null && !IsVoiceActionBusy && !IsVoiceTransitioning
        && App.VoiceAgent.Current.Phase is not (VoiceAgentPhase.Starting or VoiceAgentPhase.LoadingModel or VoiceAgentPhase.Stopping);
    public string VoiceActionText => IsVoiceActionBusy
        ? voiceActionIsStopping ? "停止中…" : "启动中…"
        : VoiceSnapshot.Phase is VoiceAgentPhase.Starting or VoiceAgentPhase.LoadingModel ? "启动中…"
        : VoiceSnapshot.Phase == VoiceAgentPhase.Stopping ? "停止中…"
        : VoiceSnapshot.IsRunning ? "停止监听" : "开始监听";
    public string VoiceStatusText => string.IsNullOrWhiteSpace(voiceOperationStatus) ? VoiceSnapshot.Status : voiceOperationStatus;
    public string VoiceDetail => string.Join("\n", new[]
    {
        string.IsNullOrWhiteSpace(voiceOperationDetail) ? VoiceSnapshot.Detail : voiceOperationDetail,
        string.IsNullOrWhiteSpace(VoiceSnapshot.LastTranscript) ? null : $"最近识别：{VoiceSnapshot.LastTranscript}",
        string.IsNullOrWhiteSpace(VoiceSnapshot.LastResponse) ? null : $"最近回复：{VoiceSnapshot.LastResponse}"
    }.Where(text => !string.IsNullOrWhiteSpace(text)));
    public string VoiceTooltip => string.Join("\n", new[]
    {
        VoiceStatusText,
        VoiceDetail,
        VoiceSnapshot.IsRunning ? "停止监听不会结束已创建的 DSH 任务。" : "使用设置中保存的麦克风与 DSH 配置启动监听。"
    }.SelectMany(text => text.Split('\n'))
        .Select(text => text.Trim()).Where(text => text.Length > 0).Distinct(StringComparer.Ordinal));
    public Visibility VoiceBusyVisibility => IsVoiceActionBusy || IsVoiceTransitioning ? Visibility.Visible : Visibility.Collapsed;

    private void AttachVoice()
    {
        voiceActionGeneration++;
        voiceOperationStatus = voiceOperationDetail = string.Empty;
        IsVoiceActionBusy = false;
        App.VoiceAgent.StatusChanged += Voice_StatusChanged;
        ApplyVoiceSnapshot(App.VoiceAgent.Current);
    }

    private void DetachVoice()
    {
        App.VoiceAgent.StatusChanged -= Voice_StatusChanged;
        voiceActionGeneration++;
        voiceActionCancellation?.Cancel();
        voiceActionCancellation = null;
        IsVoiceActionBusy = false;
        // Listening is an app service. Leaving this page only cancels a pending
        // page action; it never stops an already running microphone or DSH task.
        NotifyVoiceProperties();
    }

    private void Voice_StatusChanged(object? sender, VoiceAgentSnapshot snapshot)
    {
        var generation = viewGeneration;
        void Apply()
        {
            if (attached && generation == viewGeneration && ReferenceEquals(snapshot, App.VoiceAgent.Current))
                ApplyVoiceSnapshot(snapshot);
        }
        if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess) Apply();
        else dispatcherQueue.TryEnqueue(Apply);
    }

    private void ApplyVoiceSnapshot(VoiceAgentSnapshot snapshot)
    {
        voiceOperationStatus = voiceOperationDetail = string.Empty;
        VoiceSnapshot = snapshot;
        NotifyVoiceProperties();
    }

    partial void OnVoiceSnapshotChanged(VoiceAgentSnapshot value) => NotifyVoiceProperties();
    partial void OnIsVoiceActionBusyChanged(bool value) => NotifyVoiceProperties();

    private void NotifyVoiceProperties()
    {
        OnPropertyChanged(nameof(CanToggleVoice));
        OnPropertyChanged(nameof(VoiceActionText));
        OnPropertyChanged(nameof(VoiceStatusText));
        OnPropertyChanged(nameof(VoiceDetail));
        OnPropertyChanged(nameof(VoiceTooltip));
        OnPropertyChanged(nameof(VoiceBusyVisibility));
        ToggleVoiceListeningCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanToggleVoice))]
    private async Task ToggleVoiceListeningAsync()
    {
        if (!CanToggleVoice || pageCancellation is null) return;
        var generation = viewGeneration;
        var operation = ++voiceActionGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(pageCancellation.Token);
        voiceActionCancellation = cancellation;
        voiceActionIsStopping = App.VoiceAgent.Current.IsRunning;
        voiceOperationStatus = voiceOperationDetail = string.Empty;
        IsVoiceActionBusy = true;
        bool IsCurrentAction() => attached && generation == viewGeneration && operation == voiceActionGeneration;
        try
        {
            if (voiceActionIsStopping)
            {
                await App.VoiceAgent.StopAsync(cancellation.Token);
                if (IsCurrentAction()) ApplyVoiceSnapshot(App.VoiceAgent.Current);
            }
            else
            {
                var started = await App.VoiceAgent.StartFromProfileAsync(cancellation.Token);
                if (!IsCurrentAction()) return;
                ApplyVoiceSnapshot(App.VoiceAgent.Current);
                if (!started && VoiceSnapshot.Phase != VoiceAgentPhase.Error)
                {
                    voiceOperationStatus = "语音监听未启动";
                    voiceOperationDetail = "请查看设置中的麦克风与 DSH 配置后重试。";
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentAction())
            {
                ApplyVoiceSnapshot(App.VoiceAgent.Current);
                voiceOperationStatus = "语音操作已中断";
                voiceOperationDetail = "请根据当前监听状态决定是否重试。";
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentAction())
            {
                ApplyVoiceSnapshot(App.VoiceAgent.Current);
                voiceOperationStatus = voiceActionIsStopping ? "停止语音监听未完成" : "启动语音监听未完成";
                voiceOperationDetail = exception.Message;
            }
        }
        finally
        {
            if (IsCurrentAction())
            {
                voiceActionCancellation = null;
                IsVoiceActionBusy = false;
                NotifyVoiceProperties();
            }
        }
    }
}
