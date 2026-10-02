using HaloPixelToolBox.Core.Models.DeviceControl;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Services.DeviceControl;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Services.Audio;
using System.IO.Pipes;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloPixelToolBox.Services;

public sealed partial class DeviceControlPipeServer : IDisposable
{
    public const string PipeName = "HaloPixelToolBox.DeviceControl.v1";
    private const int MaximumRequestBytes = 64 * 1024;
    private const int RequestReadBufferSize = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly IHaloPixelDeviceControlService deviceControlService;
    private readonly LightingColorPresetStore lightingPresetStore = new();
    private readonly LightingControlCoordinator lightingControl;
    private readonly LyricsSubtitleControlService lyricsControl;
    private readonly LightingAutomationService lightingAutomation;
    private readonly AudioControlService audioControl;
    private readonly CancellationTokenSource shutdown = new();
    private Task? serverTask;
    private int hasStarted;

    public DeviceControlPipeServer()
        : this(
            new HaloPixelDeviceControlService(),
            new LightingControlCoordinator(),
            new LyricsSubtitleControlService(),
            new LightingAutomationService())
    {
    }

    internal DeviceControlPipeServer(
        IHaloPixelDeviceControlService deviceControlService,
        LightingControlCoordinator? lightingControl = null,
        LyricsSubtitleControlService? lyricsControl = null,
        LightingAutomationService? lightingAutomation = null,
        AudioControlService? audioControl = null)
    {
        this.deviceControlService = deviceControlService;
        this.lightingControl = lightingControl ?? new LightingControlCoordinator();
        this.lyricsControl = lyricsControl ?? new LyricsSubtitleControlService();
        this.lightingAutomation = lightingAutomation ?? new LightingAutomationService();
        this.audioControl = audioControl ?? new AudioControlService();
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref hasStarted, 1) == 1)
            return;

        serverTask = Task.Run(() => RunAsync(shutdown.Token));
        Console.WriteLine($"设备控制管道已启动：{PipeName}");
    }

    public void Dispose()
    {
        shutdown.Cancel();
        shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                _ = HandleConnectedClientAsync(pipe, cancellationToken);
                pipe = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[WARN]设备控制管道异常：{exception.Message}");
            }
            finally
            {
                if (pipe is not null)
                    await pipe.DisposeAsync();
            }
        }
    }

    private async Task HandleConnectedClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                await HandleClientAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException exception)
            {
                Console.WriteLine($"[WARN]设备控制客户端已断开：{exception.Message}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[WARN]设备控制请求异常：{exception.Message}");
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var request = await ReadRequestAsync(pipe, cancellationToken);
        if (request.Status == PipeRequestReadStatus.Disconnected)
            return;

        if (request.Status == PipeRequestReadStatus.TooLarge)
        {
            await TryWriteResponseAsync(
                pipe,
                PipeResponse.FromError(null, "request_too_large", "请求超过 64 KiB 限制"),
                cancellationToken);
            return;
        }

        if (request.Status == PipeRequestReadStatus.InvalidUtf8)
        {
            await TryWriteResponseAsync(
                pipe,
                PipeResponse.FromError(null, "invalid_encoding", "请求必须使用有效的 UTF-8 编码"),
                cancellationToken);
            return;
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var disconnectMonitor = MonitorClientDisconnectAsync(
            pipe,
            requestCancellation,
            monitorCancellation.Token);

        var response = await ProcessRequestAsync(request.Json!, requestCancellation.Token);

        monitorCancellation.Cancel();
        var clientDisconnected = await disconnectMonitor;
        if (clientDisconnected || cancellationToken.IsCancellationRequested || !pipe.IsConnected)
            return;

        await TryWriteResponseAsync(pipe, response, cancellationToken);
    }

    private static async Task<PipeRequestReadResult> ReadRequestAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        var requestBytes = new byte[MaximumRequestBytes];
        var readBuffer = new byte[RequestReadBufferSize];
        var requestLength = 0;

        while (true)
        {
            var bytesRead = await pipe.ReadAsync(readBuffer, cancellationToken);
            if (bytesRead == 0)
                return new(PipeRequestReadStatus.Disconnected, null);

            for (var index = 0; index < bytesRead; index++)
            {
                var value = readBuffer[index];
                if (value == (byte)'\n')
                {
                    if (requestLength > 0 && requestBytes[requestLength - 1] == (byte)'\r')
                        requestLength--;

                    try
                    {
                        return new(
                            PipeRequestReadStatus.Succeeded,
                            StrictUtf8.GetString(requestBytes, 0, requestLength));
                    }
                    catch (DecoderFallbackException)
                    {
                        return new(PipeRequestReadStatus.InvalidUtf8, null);
                    }
                }

                if (requestLength == MaximumRequestBytes)
                    return new(PipeRequestReadStatus.TooLarge, null);

                requestBytes[requestLength++] = value;
            }
        }
    }

    private static async Task<bool> MonitorClientDisconnectAsync(
        NamedPipeServerStream pipe,
        CancellationTokenSource requestCancellation,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        try
        {
            while (await pipe.ReadAsync(buffer, cancellationToken) > 0)
            {
                // A connection carries exactly one request. Ignore any trailing bytes while
                // retaining the pending read that detects an interrupted or timed-out caller.
            }

            requestCancellation.Cancel();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            requestCancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            requestCancellation.Cancel();
            return true;
        }
    }

    private static async Task WriteResponseAsync(
        NamedPipeServerStream pipe,
        PipeResponse response,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, SerializerOptions);
        var payload = Encoding.UTF8.GetBytes(json + "\n");
        await pipe.WriteAsync(payload, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }

    private static async Task TryWriteResponseAsync(
        NamedPipeServerStream pipe,
        PipeResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteResponseAsync(pipe, response, cancellationToken);
        }
        catch (IOException)
        {
            // The caller can disappear between the EOF monitor being cancelled and this write.
        }
        catch (ObjectDisposedException)
        {
            // The server can be disposed concurrently during application shutdown.
        }
    }

    private async Task<PipeResponse> ProcessRequestAsync(string json, CancellationToken cancellationToken)
    {
        PipeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PipeRequest>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return PipeResponse.FromError(null, "invalid_json", exception.Message);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Method))
            return PipeResponse.FromError(request?.Id, "invalid_request", "method 不能为空");

        try
        {
            object result = request.Method switch
            {
                "get_status" => await deviceControlService.GetStatusAsync(cancellationToken),
                "get_audio_status" => await GetAudioStatusAsync(cancellationToken),
                "configure_audio" => await ConfigureAudioAsync(request.Parameters, cancellationToken),
                "set_audio_output" => await SetAudioOutputAsync(request.Parameters, cancellationToken),
                "set_ambient_light" => await lightingControl.SetAmbientLightEnabledAsync(
                    GetRequiredBoolean(request.Parameters, "enabled"), cancellationToken),
                "set_pixel_screen" => await lightingControl.SetPixelScreenEnabledAsync(
                    GetRequiredBoolean(request.Parameters, "enabled"), cancellationToken),
                "set_volume" => await deviceControlService.SetVolumeAsync(
                    GetRequiredInteger(request.Parameters, "volume"), cancellationToken),
                "set_ambient_light_speed" => await SetAmbientLightSpeedAsync(
                    GetRequiredString(request.Parameters, "mode"),
                    GetRequiredInteger(request.Parameters, "value"),
                    cancellationToken),
                "set_ambient_light_effect" => await SetAmbientLightEffectAsync(
                    request.Parameters,
                    cancellationToken),
                "show_time" => await deviceControlService.ShowTimeAsync(cancellationToken),
                "restore_default_scene" => await deviceControlService.RestoreDefaultSceneAsync(cancellationToken),
                "activate_scene" => await deviceControlService.ActivateSceneAsync(
                    GetRequiredInteger(request.Parameters, "categoryIndex"),
                    GetRequiredInteger(request.Parameters, "sceneIndex"),
                    cancellationToken),
                "get_catalog" => await GetCatalogAsync(
                    GetOptionalString(request.Parameters, "category"), cancellationToken),
                "apply_lighting_preset" => await ApplyLightingPresetAsync(
                    GetRequiredString(request.Parameters, "name"), cancellationToken),
                "activate_scene_by_position" => await deviceControlService.ActivateSceneByPositionAsync(
                    GetRequiredString(request.Parameters, "category"),
                    GetRequiredInteger(request.Parameters, "position"),
                    cancellationToken),
                "activate_scene_by_reference" => await deviceControlService.ActivateSceneByReferenceAsync(
                    GetRequiredString(request.Parameters, "category"),
                    GetRequiredString(request.Parameters, "scene"),
                    cancellationToken),
                "start_spotify_lyrics" => await StartSpotifyLyricsAsync(request.Parameters, cancellationToken),
                "get_lyrics_status" => DeviceCommandResult<LyricsSubtitleSessionStatus>.Succeeded(
                    "已读取歌词字幕状态",
                    lyricsControl.CurrentStatus),
                "set_lyrics_offset" => await lyricsControl.SetOffsetAsync(
                    GetRequiredInteger(request.Parameters, "offsetMs"),
                    cancellationToken),
                "stop_lyrics" => await lyricsControl.StopAsync(
                    GetOptionalBoolean(request.Parameters, "restoreScene") ?? true,
                    cancellationToken),
                "configure_lyrics" => await ConfigureLyricsAsync(request.Parameters, cancellationToken),
                "show_subtitle" => await ShowSubtitleAsync(request.Parameters, cancellationToken),
                "get_lighting_automation" => DeviceCommandResult<LightingAutomationState>.Succeeded(
                    "已读取自动关灯配置与运行状态",
                    lightingAutomation.CurrentState),
                "configure_lighting_automation" => await ConfigureLightingAutomationAsync(
                    request.Parameters,
                    cancellationToken),
                "configure_device" => await ConfigureDeviceAsync(request.Parameters, cancellationToken),
                _ => throw new UnknownMethodException(request.Method)
            };
            return PipeResponse.FromResult(request.Id, result);
        }
        catch (UnknownMethodException exception)
        {
            return PipeResponse.FromError(request.Id, "method_not_found", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return PipeResponse.FromError(request.Id, "invalid_params", exception.Message);
        }
        catch (OperationCanceledException)
        {
            return PipeResponse.FromResult(
                request.Id,
                DeviceCommandResult.Rejected(DeviceCommandStatus.Cancelled, "设备操作已取消"));
        }
        catch (Exception exception)
        {
            return PipeResponse.FromError(request.Id, "internal_error", exception.Message);
        }
    }

    private static bool GetRequiredBoolean(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"参数 {name} 必须是布尔值");
        }

        return value.GetBoolean();
    }

    private static int GetRequiredInteger(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw new ArgumentException($"参数 {name} 必须是整数");
        }

        return result;
    }

    private static int? GetOptionalInteger(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new ArgumentException($"参数 {name} 必须是整数");
        return result;
    }

    private static bool? GetOptionalBoolean(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"参数 {name} 必须是布尔值");
        return value.GetBoolean();
    }

    private static string GetRequiredString(JsonElement parameters, string name)
    {
        var result = GetOptionalString(parameters, name);
        if (string.IsNullOrWhiteSpace(result))
            throw new ArgumentException($"参数 {name} 必须是非空字符串");
        return result;
    }

    private static string? GetOptionalString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"参数 {name} 必须是字符串");
        return value.GetString()?.Trim();
    }

    private async Task<DeviceCommandResult<PixelBarCatalog>> GetCatalogAsync(
        string? category,
        CancellationToken cancellationToken)
    {
        var scenes = await deviceControlService.ListScenesAsync(category, cancellationToken);
        if (!scenes.Success || scenes.Data is null)
            return DeviceCommandResult<PixelBarCatalog>.Rejected(scenes.Status, scenes.Message);

        var presetNames = lightingPresetStore.Load().Select(preset => preset.Name).ToList();
        var categories = scenes.Data
            .GroupBy(scene => scene.Category)
            .Select(group => new SceneCategorySummary(
                group.Key,
                group.Count(),
                group.Count(scene => scene.RequiresResourceUpload)))
            .ToList();
        return DeviceCommandResult<PixelBarCatalog>.Succeeded(
            string.IsNullOrWhiteSpace(category) ? "已读取 PixelBar 控制目录" : scenes.Message,
            new PixelBarCatalog(
                presetNames,
                categories,
                string.IsNullOrWhiteSpace(category) ? [] : scenes.Data,
                AmbientLightEffectResolver.Catalog,
                ReadCurrentAmbientLightEffect()));
    }

    private static CurrentAmbientLightEffect ReadCurrentAmbientLightEffect()
    {
        var current = AmbientLightEffectResolver.GetItem(
            (AmbientLightEffect)(Math.Clamp(DisplayFeatureProfile.AmbientLightEffectIndex, 0, 5) + 1));
        return new(current.Position, current.Key, current.Name, "localProfile");
    }

    private async Task<DeviceCommandResult> ApplyLightingPresetAsync(
        string requestedName,
        CancellationToken cancellationToken)
    {
        var presets = lightingPresetStore.Load();
        if (presets.Count == 0)
            return DeviceCommandResult.Rejected(DeviceCommandStatus.NotFound, "尚未保存灯光配色方案");

        Models.LightingColorPreset? preset;
        if (DeviceNameResolver.IsRandomReference(requestedName))
        {
            preset = presets[Random.Shared.Next(presets.Count)];
        }
        else
        {
            var resolution = DeviceNameResolver.Resolve(
                requestedName,
                presets.Select(item => new DeviceLookupCandidate<Models.LightingColorPreset>(
                    item,
                    item.Name)),
                ["灯光", "配置", "配色", "方案", "预设"]);
            if (!resolution.IsResolved)
            {
                var candidates = resolution.Suggestions.Count > 0
                    ? $"；候选：{string.Join("、", resolution.Suggestions)}"
                    : string.Empty;
                return DeviceCommandResult.Rejected(
                    resolution.IsAmbiguous ? DeviceCommandStatus.Conflict : DeviceCommandStatus.NotFound,
                    $"未能唯一解析灯光配置“{requestedName}”{candidates}");
            }

            preset = resolution.Value;
        }

        if (preset is null)
        {
            return DeviceCommandResult.Rejected(
                DeviceCommandStatus.NotFound,
                $"未找到灯光配置“{requestedName}”；可用配置：{string.Join("、", presets.Select(item => item.Name))}");
        }

        return await lightingControl.ApplyPresetAsync(preset, cancellationToken);
    }

    private Task<DeviceCommandResult<AmbientLightSpeedChange>> SetAmbientLightSpeedAsync(
        string mode,
        int value,
        CancellationToken cancellationToken)
    {
        if (value is < 1 or > 10)
        {
            return Task.FromResult(DeviceCommandResult<AmbientLightSpeedChange>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                "value 必须是 1 到 10 之间的整数"));
        }

        return mode.Trim().ToLowerInvariant() switch
        {
            "set" => lightingControl.SetAmbientSpeedAsync(value, null, cancellationToken),
            "increase" => lightingControl.SetAmbientSpeedAsync(null, value, cancellationToken),
            "decrease" => lightingControl.SetAmbientSpeedAsync(null, -value, cancellationToken),
            _ => Task.FromResult(DeviceCommandResult<AmbientLightSpeedChange>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                "mode 必须是 set、increase 或 decrease"))
        };
    }

    private Task<DeviceCommandResult<AmbientLightEffectChange>> SetAmbientLightEffectAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var mode = GetRequiredString(parameters, "mode");
        var reference = GetOptionalString(parameters, "effect");
        if (!string.Equals(mode, "set", StringComparison.OrdinalIgnoreCase)
            && parameters.TryGetProperty("effect", out _))
            throw new ArgumentException("只有 set 模式可以提供 effect；next、previous、random 不接受 effect");
        AmbientLightEffectResolver.Parse(mode, reference);
        return lightingControl.SetAmbientEffectAsync(mode, reference, cancellationToken);
    }

    private async Task<DeviceCommandResult<LyricsSubtitleSessionStatus>> StartSpotifyLyricsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var offset = GetOptionalInteger(parameters, "offsetMs") ?? 0;
        var scroll = GetOptionalBoolean(parameters, "scroll") ?? true;
        var result = await lyricsControl.StartSpotifyAsync(offset, scroll, cancellationToken: cancellationToken);
        if (result.Success)
            DisplayFeatureProfile.LyricsProviderIndex = 2;
        return result;
    }

    private async Task<DeviceCommandResult<LyricsSubtitleSessionStatus>> ConfigureLyricsAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var provider = GetOptionalString(parameters, "provider");
        var enabled = GetOptionalBoolean(parameters, "enabled") ?? true;
        if (!enabled)
        {
            return await lyricsControl.StopAsync(
                GetOptionalBoolean(parameters, "restoreScene") ?? true,
                cancellationToken);
        }

        provider ??= string.IsNullOrWhiteSpace(lyricsControl.CurrentStatus.Provider)
            ? "spotify"
            : lyricsControl.CurrentStatus.Provider;
        var normalizedProvider = NormalizeLookupText(provider);
        if (!normalizedProvider.Contains("SPOTIFY", StringComparison.OrdinalIgnoreCase)
            && normalizedProvider is not "声破天" and not "声破天歌词")
        {
            return DeviceCommandResult<LyricsSubtitleSessionStatus>.Rejected(
                DeviceCommandStatus.NotSupported,
                $"歌词来源“{provider}”尚未接入进程级控制服务；当前可由 Agent 启动的来源为 Spotify。网易云与 QQ 音乐仍可在应用的歌词字幕页使用");
        }

        var current = lyricsControl.CurrentStatus;
        var offset = GetOptionalInteger(parameters, "offsetMs") ?? current.OffsetMilliseconds;
        var scroll = GetOptionalBoolean(parameters, "scroll") ?? current.ScrollEnabled;
        var localLrcPath = GetOptionalString(parameters, "localLrcPath");
        var result = await lyricsControl.StartSpotifyAsync(
            offset,
            scroll,
            localLrcPath,
            cancellationToken: cancellationToken);
        if (result.Success)
            DisplayFeatureProfile.LyricsProviderIndex = 2;
        return result;
    }

    private async Task<DeviceCommandResult<SubtitleControlStatus>> ShowSubtitleAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var text = GetRequiredString(parameters, "text");
        var color = GetOptionalString(parameters, "color") ?? "#FFFFFF";
        var normalizedColor = NormalizeLookupText(color);
        if (normalizedColor is not "FFFFFF" and not "FFF" and not "WHITE" and not "白色" and not "白")
        {
            return DeviceCommandResult<SubtitleControlStatus>.Rejected(
                DeviceCommandStatus.NotSupported,
                "当前 PixelBar 文本协议尚未公开字幕颜色字段；本次未发送字幕。请使用白色，或只调节对齐与滚动方式");
        }

        var layout = ParseSubtitleLayout(GetOptionalString(parameters, "layout") ?? "center");
        var scroll = ParseSubtitleScroll(GetOptionalString(parameters, "scroll") ?? "none");
        return await deviceControlService.ShowSubtitleAsync(
            new SubtitleControlRequest(text, layout, scroll),
            cancellationToken);
    }

    private async Task<DeviceCommandResult<LightingAutomationState>> ConfigureLightingAutomationAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var current = lightingAutomation.CurrentConfiguration;
        var displayOffEnabled = GetOptionalBoolean(parameters, "displayOffEnabled")
                                ?? current.TurnOffWhenDisplayOff;
        var scheduleEnabled = GetOptionalBoolean(parameters, "scheduleEnabled")
                              ?? current.ScheduleEnabled;
        var startMinutes = ResolveOptionalTimeMinutes(parameters, "startMinutes", "startTime")
                           ?? current.StartMinutes;
        var endMinutes = ResolveOptionalTimeMinutes(parameters, "endMinutes", "endTime")
                         ?? current.EndMinutes;

        if (GetOptionalBoolean(parameters, "displayOffEnabled") is null
            && GetOptionalBoolean(parameters, "scheduleEnabled") is null
            && ResolveOptionalTimeMinutes(parameters, "startMinutes", "startTime") is null
            && ResolveOptionalTimeMinutes(parameters, "endMinutes", "endTime") is null)
        {
            throw new ArgumentException("至少需要提供一项自动关灯配置");
        }

        return await lightingAutomation.ConfigureAsync(
            new LightingAutomationConfiguration(
                displayOffEnabled,
                scheduleEnabled,
                startMinutes,
                endMinutes),
            cancellationToken);
    }

    private static HaloPixelTextLayout ParseSubtitleLayout(string value)
    {
        var normalized = NormalizeLookupText(value);
        return normalized switch
        {
            "LEFT" or "左" or "左对齐" => HaloPixelTextLayout.Left,
            "CENTER" or "CENTRE" or "居中" or "中间" or "中央" => HaloPixelTextLayout.Center,
            "RIGHT" or "右" or "右对齐" => HaloPixelTextLayout.Right,
            _ => throw new ArgumentException("layout 必须是 left、center 或 right")
        };
    }

    private static TextScrollDirection ParseSubtitleScroll(string value)
    {
        var normalized = NormalizeLookupText(value);
        return normalized switch
        {
            "NONE" or "OFF" or "不滚动" or "停止滚动" or "关闭" => TextScrollDirection.None,
            "LEFT" or "向左" or "向左滚动" or "左滚" or "RIGHTTOLEFT" => TextScrollDirection.RightToLeft,
            "RIGHT" or "向右" or "向右滚动" or "右滚" or "LEFTTORIGHT" => TextScrollDirection.LeftToRight,
            _ => throw new ArgumentException("scroll 必须是 none、left 或 right")
        };
    }

    private static int? ResolveOptionalTimeMinutes(
        JsonElement parameters,
        string minutesName,
        string timeName)
    {
        var minutes = GetOptionalInteger(parameters, minutesName);
        var time = GetOptionalString(parameters, timeName);
        if (minutes is null && time is null)
            return null;

        int? parsedTime = time is null ? null : ParseClockMinutes(time, timeName);
        if (minutes is not null && parsedTime is not null && minutes.Value != parsedTime.Value)
            throw new ArgumentException($"{minutesName} 与 {timeName} 表示的时间不一致");
        return minutes ?? parsedTime;
    }

    private static int ParseClockMinutes(string value, string parameterName)
    {
        var normalized = value.Trim()
            .Replace('：', ':')
            .Replace("點", "点", StringComparison.Ordinal)
            .Replace("時", "时", StringComparison.Ordinal);
        var isPm = normalized.Contains("下午", StringComparison.Ordinal)
                   || normalized.Contains("晚上", StringComparison.Ordinal)
                   || normalized.Contains("晚间", StringComparison.Ordinal);
        var isNoon = normalized.Contains("中午", StringComparison.Ordinal);
        var isAm = normalized.Contains("上午", StringComparison.Ordinal)
                   || normalized.Contains("凌晨", StringComparison.Ordinal)
                   || normalized.Contains("早上", StringComparison.Ordinal);
        normalized = normalized
            .Replace("下午", string.Empty, StringComparison.Ordinal)
            .Replace("晚上", string.Empty, StringComparison.Ordinal)
            .Replace("晚间", string.Empty, StringComparison.Ordinal)
            .Replace("中午", string.Empty, StringComparison.Ordinal)
            .Replace("上午", string.Empty, StringComparison.Ordinal)
            .Replace("凌晨", string.Empty, StringComparison.Ordinal)
            .Replace("早上", string.Empty, StringComparison.Ordinal)
            .Replace("点半", ":30", StringComparison.Ordinal)
            .Replace("时半", ":30", StringComparison.Ordinal)
            .Replace("点", ":", StringComparison.Ordinal)
            .Replace("时", ":", StringComparison.Ordinal)
            .Replace("分", string.Empty, StringComparison.Ordinal)
            .Trim()
            .TrimEnd(':');

        if (!TimeOnly.TryParseExact(
                normalized,
                ["%H", "HH", "H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw new ArgumentException($"参数 {parameterName} 必须是 HH:mm，例如 23:30");
        }

        var hour = parsed.Hour;
        if ((isPm || isNoon) && hour is >= 1 and < 12)
            hour += 12;
        else if (isAm && hour == 12)
            hour = 0;
        return hour * 60 + parsed.Minute;
    }

    private async Task<DeviceBatchCommandResult> ConfigureDeviceAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("设备配置参数必须是对象");
        var lightingPreset = GetOptionalString(parameters, "lightingPreset");
        var ambientLightEnabled = GetOptionalBoolean(parameters, "ambientLightEnabled");
        var pixelScreenEnabled = GetOptionalBoolean(parameters, "pixelScreenEnabled");
        var lightSpeedMode = GetOptionalString(parameters, "lightSpeedMode");
        var lightSpeedValue = GetOptionalInteger(parameters, "lightSpeedValue");
        var lightEffectMode = GetOptionalString(parameters, "lightEffectMode");
        var lightEffectReference = GetOptionalString(parameters, "lightEffectReference");
        var sceneCategory = GetOptionalString(parameters, "sceneCategory");
        var scenePosition = GetOptionalInteger(parameters, "scenePosition");
        var sceneReference = GetOptionalString(parameters, "sceneReference");
        if (parameters.TryGetProperty("restoreDefaultScene", out var restoreValue)
            && restoreValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("restoreDefaultScene 必须是布尔值");
        var restoreDefaultScene = GetOptionalBoolean(parameters, "restoreDefaultScene") ?? false;
        var volume = GetOptionalInteger(parameters, "volume");
        var continueOnError = GetOptionalBoolean(parameters, "continueOnError") ?? false;
        if (lightingPreset is null
            && ambientLightEnabled is null
            && pixelScreenEnabled is null
            && lightSpeedMode is null
            && lightSpeedValue is null
            && lightEffectMode is null
            && lightEffectReference is null
            && sceneCategory is null
            && scenePosition is null
            && sceneReference is null
            && !restoreDefaultScene
            && volume is null)
            throw new ArgumentException("至少需要提供一项设备配置");
        if ((lightSpeedMode is null) != (lightSpeedValue is null))
            throw new ArgumentException("lightSpeedMode 与 lightSpeedValue 必须同时提供");
        if (lightSpeedMode is not null
            && (lightSpeedMode.ToLowerInvariant() is not ("set" or "increase" or "decrease")
                || lightSpeedValue is < 1 or > 10))
            throw new ArgumentException("灯效速度 mode 必须是 set、increase 或 decrease，value 必须是 1 到 10 的整数");
        if (lightEffectMode is null && lightEffectReference is not null)
            throw new ArgumentException("提供 lightEffectReference 时必须同时提供 lightEffectMode=set");
        if (lightEffectMode is not null
            && !string.Equals(lightEffectMode, "set", StringComparison.OrdinalIgnoreCase)
            && parameters.TryGetProperty("lightEffectReference", out _))
            throw new ArgumentException("只有 lightEffectMode=set 可以提供 lightEffectReference");
        if (lightEffectMode is not null)
            AmbientLightEffectResolver.Parse(lightEffectMode, lightEffectReference);
        if (volume is < 0 or > 16)
            throw new ArgumentException("volume 必须是 0 到 16 之间的整数");
        if (lightingPreset is not null && string.IsNullOrWhiteSpace(lightingPreset))
            throw new ArgumentException("lightingPreset 必须是非空字符串");
        if (sceneCategory is not null && string.IsNullOrWhiteSpace(sceneCategory))
            throw new ArgumentException("sceneCategory 必须是非空字符串");
        if (sceneReference is not null && string.IsNullOrWhiteSpace(sceneReference))
            throw new ArgumentException("sceneReference 必须是非空字符串");
        if (scenePosition is < 1)
            throw new ArgumentException("scenePosition 从 1 开始");
        if (restoreDefaultScene && (parameters.TryGetProperty("sceneCategory", out _)
            || parameters.TryGetProperty("scenePosition", out _)
            || parameters.TryGetProperty("sceneReference", out _)))
            throw new ArgumentException("restoreDefaultScene=true 不能同时提供 sceneCategory、scenePosition 或 sceneReference");
        if (sceneCategory is null && (scenePosition is not null || sceneReference is not null))
            throw new ArgumentException("使用场景位置或名称时必须同时提供 sceneCategory");
        if (sceneCategory is not null && scenePosition is null && sceneReference is null)
            throw new ArgumentException("sceneCategory 必须配合 scenePosition 或 sceneReference");
        if (scenePosition is not null && sceneReference is not null)
            throw new ArgumentException("scenePosition 与 sceneReference 只能提供一项");

        var results = new Dictionary<string, DeviceCommandResult>(StringComparer.OrdinalIgnoreCase);
        if (lightingPreset is not null)
        {
            results["lightingPreset"] = await ApplyLightingPresetAsync(lightingPreset, cancellationToken);
            if (ShouldStopBatch(results, continueOnError))
                return BuildBatchResult(results, stoppedEarly: true);
        }
        if (ambientLightEnabled is not null)
        {
            results["ambientLight"] = await lightingControl.SetAmbientLightEnabledAsync(
                ambientLightEnabled.Value,
                cancellationToken);
            if (ShouldStopBatch(results, continueOnError))
                return BuildBatchResult(results, stoppedEarly: true);
        }
        if (pixelScreenEnabled is not null)
        {
            results["pixelScreen"] = await lightingControl.SetPixelScreenEnabledAsync(
                pixelScreenEnabled.Value,
                cancellationToken);
            if (ShouldStopBatch(results, continueOnError))
                return BuildBatchResult(results, stoppedEarly: true);
        }
        if (lightSpeedMode is not null && lightSpeedValue is not null)
        {
            var speed = await SetAmbientLightSpeedAsync(lightSpeedMode, lightSpeedValue.Value, cancellationToken);
            results["lightSpeed"] = new DeviceCommandResult(speed.Status, speed.Message);
            if (ShouldStopBatch(results, continueOnError))
                return BuildBatchResult(results, stoppedEarly: true);
        }
        if (lightEffectMode is not null)
        {
            var effect = await lightingControl.SetAmbientEffectAsync(
                lightEffectMode, lightEffectReference, cancellationToken);
            results["lightEffect"] = new DeviceCommandResult(effect.Status, effect.Message);
            if (ShouldStopBatch(results, continueOnError))
                return BuildBatchResult(results, stoppedEarly: true);
        }
        if (restoreDefaultScene)
        {
            results["scene"] = await deviceControlService.RestoreDefaultSceneAsync(cancellationToken);
            if (ShouldStopBatch(results, continueOnError))
                return BuildBatchResult(results, stoppedEarly: true);
        }
        else if (sceneCategory is not null && scenePosition is not null)
        {
            results["scene"] = await deviceControlService.ActivateSceneByPositionAsync(
                sceneCategory,
                scenePosition.Value,
                cancellationToken);
            if (ShouldStopBatch(results, continueOnError))
                return BuildBatchResult(results, stoppedEarly: true);
        }
        else if (sceneCategory is not null && sceneReference is not null)
        {
            results["scene"] = await deviceControlService.ActivateSceneByReferenceAsync(
                sceneCategory,
                sceneReference,
                cancellationToken);
            if (ShouldStopBatch(results, continueOnError))
                return BuildBatchResult(results, stoppedEarly: true);
        }
        if (volume is not null)
            results["volume"] = await deviceControlService.SetVolumeAsync(volume.Value, cancellationToken);

        return BuildBatchResult(results, stoppedEarly: false);
    }

    private static bool ShouldStopBatch(
        IReadOnlyDictionary<string, DeviceCommandResult> results,
        bool continueOnError)
        => !continueOnError && results.Values.Any(result => !result.Success);

    private static DeviceBatchCommandResult BuildBatchResult(
        IReadOnlyDictionary<string, DeviceCommandResult> results,
        bool stoppedEarly)
    {
        var failed = results.Values.FirstOrDefault(result => !result.Success);
        if (failed is null)
            return new DeviceBatchCommandResult(DeviceCommandStatus.Succeeded, "PixelBar 组合配置已完成", results);

        return new DeviceBatchCommandResult(
            failed.Status,
            stoppedEarly ? "PixelBar 组合配置失败，已停止后续操作" : "PixelBar 组合配置部分失败",
            results);
    }

    private static string NormalizeLookupText(string value)
        => DeviceNameResolver.Normalize(value);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PipeRequest(string? Id, string? Method, JsonElement Parameters);

    private sealed record PipeError(string Code, string Message);

    private enum PipeRequestReadStatus
    {
        Succeeded,
        Disconnected,
        TooLarge,
        InvalidUtf8
    }

    private readonly record struct PipeRequestReadResult(PipeRequestReadStatus Status, string? Json);

    private sealed record SceneCategorySummary(string Name, int Count, int UploadCount);

    private sealed record PixelBarCatalog(
        IReadOnlyList<string> LightingPresets,
        IReadOnlyList<SceneCategorySummary> SceneCategories,
        IReadOnlyList<HaloPixelSceneInfo> Scenes,
        IReadOnlyList<AmbientLightEffectCatalogItem> AmbientLightEffects,
        CurrentAmbientLightEffect CurrentAmbientLightEffect);

    private sealed record CurrentAmbientLightEffect(int Position, string Key, string Name, string Verification);

    private sealed record DeviceBatchCommandResult(
        DeviceCommandStatus Status,
        string Message,
        IReadOnlyDictionary<string, DeviceCommandResult> Data)
    {
        public bool Success => Status == DeviceCommandStatus.Succeeded;

        public string Code => DeviceCommandCodes.FromStatus(Status);
    }

    private sealed record PipeResponse(string? Id, object? Result, PipeError? Error)
    {
        public static PipeResponse FromResult(string? id, object result) => new(id, result, null);

        public static PipeResponse FromError(string? id, string code, string message)
            => new(id, null, new PipeError(code, message));
    }

    private sealed class UnknownMethodException(string method)
        : Exception($"未知设备方法：{method}");
}
