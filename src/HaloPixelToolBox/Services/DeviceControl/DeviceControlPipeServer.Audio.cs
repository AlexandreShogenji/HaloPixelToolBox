using System.Text.Json;
using HaloPixelToolBox.Core.Models.DeviceControl;
using HaloPixelToolBox.Core.Services.DeviceControl;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services.Audio;

namespace HaloPixelToolBox.Services;

public sealed partial class DeviceControlPipeServer
{
    private async Task<DeviceCommandResult<AudioControlSnapshot>> GetAudioStatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = await audioControl.RefreshAsync(cancellationToken);
        return new(snapshot.OperationSucceeded ? DeviceCommandStatus.Succeeded : DeviceCommandStatus.NotConfirmed,
            "独立音频控制：" + snapshot.Message, snapshot);
    }

    private async Task<DeviceCommandResult<AudioControlSnapshot>> ConfigureAudioAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.EnumerateObject().Any())
            throw new ArgumentException("请至少提供一个音效设置");
        string[] allowed = ["enabled", "preset", "preampDb", "balance", "autoHeadroom", "bandGainsDb", "group", "mode", "value"];
        foreach (var property in parameters.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException($"不支持的音效参数：{property.Name}");
            if (property.Value.ValueKind == JsonValueKind.Null)
                throw new ArgumentException($"音效参数 {property.Name} 不能为 null");
        }
        var enabled = GetOptionalBoolean(parameters, "enabled");
        var preset = GetOptionalString(parameters, "preset");
        var preamp = GetOptionalAudioNumber(parameters, "preampDb", -30, 12);
        var balance = GetOptionalAudioNumber(parameters, "balance", -100, 100);
        var headroom = GetOptionalBoolean(parameters, "autoHeadroom");
        var group = GetOptionalString(parameters, "group");
        var mode = GetOptionalString(parameters, "mode");
        var value = GetOptionalAudioNumber(parameters, "value", -12, 12);
        double[]? gains = null;
        if (parameters.TryGetProperty("bandGainsDb", out var bands))
        {
            if (bands.ValueKind != JsonValueKind.Array || bands.GetArrayLength() != 10)
                throw new ArgumentException("bandGainsDb 须为十段均衡器数值");
            gains = bands.EnumerateArray().Select(band =>
            {
                if (band.ValueKind != JsonValueKind.Number || !band.TryGetDouble(out var gain)
                    || !double.IsFinite(gain) || gain is < -12 or > 12)
                    throw new ArgumentException("每段均衡器须为 -12 到 12 dB");
                return gain;
            }).ToArray();
        }
        if (preset is not null && (gains is not null || preamp is not null || balance is not null || headroom is not null || group is not null))
            throw new ArgumentException("preset 不能与曲线数值、增益、平衡或频段调整同时提供");
        if (group is null && (mode is not null || value is not null)
            || group is not null && (mode is null || value is null))
            throw new ArgumentException("group、mode 和 value 必须同时提供");
        if (group is not null && (gains is not null || group is not ("bass" or "mid" or "treble")
            || mode is not ("set" or "increase" or "decrease")
            || (mode is "increase" or "decrease") && value <= 0))
            throw new ArgumentException("频段调整须使用 bass/mid/treble 与 set/increase/decrease；增减量须大于零，且不能同时提供 bandGainsDb");

        var snapshot = await audioControl.ConfigureAsync(current =>
        {
            var profile = preset is null ? current : audioControl.ResolvePreset(preset).Profile.Copy() with
            {
                EndpointId = current.EndpointId, Enabled = current.Enabled
            };
            if (group is not null)
            {
                gains = (double[])profile.BandGainsDb.Clone();
                int[] indices = group switch { "bass" => [0, 1, 2], "mid" => [3, 4, 5, 6], _ => [7, 8, 9] };
                foreach (var index in indices)
                    gains[index] = Math.Clamp(mode switch
                    {
                        "increase" => gains[index] + value!.Value,
                        "decrease" => gains[index] - value!.Value,
                        _ => value!.Value
                    }, -12, 12);
            }
            return profile with
            {
                Enabled = enabled ?? profile.Enabled,
                PreampDb = preamp ?? profile.PreampDb,
                Balance = balance ?? profile.Balance,
                AutoHeadroom = headroom ?? profile.AutoHeadroom,
                BandGainsDb = gains ?? profile.BandGainsDb
            };
        }, cancellationToken);
        return AudioWriteResult(snapshot);
    }

    private async Task<DeviceCommandResult<AudioControlSnapshot>> SetAudioOutputAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || parameters.EnumerateObject().Any(property => property.Name != "device"))
            throw new ArgumentException("输出切换只接受 device 参数");
        var reference = GetRequiredString(parameters, "device");
        var current = await audioControl.RefreshAsync(cancellationToken);
        var exact = current.Endpoints.FirstOrDefault(endpoint => endpoint.Id == reference);
        var resolution = DeviceNameResolver.Resolve(reference, current.Endpoints.Select(endpoint =>
            new DeviceLookupCandidate<AudioEndpointInfo>(endpoint, endpoint.Name)), ["输出设备", "扬声器"]);
        var target = exact ?? (resolution.IsResolved ? resolution.Value : null);
        if (target is null)
            return DeviceCommandResult<AudioControlSnapshot>.Rejected(
                resolution.IsAmbiguous ? DeviceCommandStatus.Conflict : DeviceCommandStatus.NotFound,
                $"无法唯一确定输出设备，可选：{string.Join("、", resolution.Suggestions)}");
        return AudioWriteResult(await audioControl.SetDefaultOutputAsync(target.Id, cancellationToken));
    }

    private static DeviceCommandResult<AudioControlSnapshot> AudioWriteResult(AudioControlSnapshot snapshot)
        => new(snapshot.OperationSucceeded ? DeviceCommandStatus.Succeeded : DeviceCommandStatus.NotConfirmed,
            snapshot.Message, snapshot);

    private static double? GetOptionalAudioNumber(JsonElement parameters, string name, double minimum, double maximum)
    {
        if (!parameters.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number < minimum || number > maximum)
            throw new ArgumentException($"{name} 须为 {minimum} 到 {maximum} 的数值");
        return number;
    }
}
