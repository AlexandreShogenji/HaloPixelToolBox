using System.Diagnostics;
using HaloPixelToolBox.Core.Models.DeviceControl;
using HaloPixelToolBox.Core.Services.DeviceControl;
using HaloPixelToolBox.Models;

namespace HaloPixelToolBox.Services.Audio;

public sealed record AudioControlSnapshot(
    AudioControlProfile Profile,
    IReadOnlyList<AudioControlPreset> Presets,
    IReadOnlyList<AudioEndpointInfo> Endpoints,
    AudioBackendStatus Backend,
    string Message,
    long Revision,
    bool OperationSucceeded);

/// <summary>One serialized source of audio settings for the page and agent tools.</summary>
public sealed class AudioControlService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AudioProfileStore store;
    private readonly EqualizerApoService backend;
    private readonly WindowsAudioEndpointService endpoints;
    private AudioControlDocument document;
    private AudioControlSnapshot current;

    public AudioControlService() : this(new(), new(), new())
    {
    }

    internal AudioControlService(AudioProfileStore store, EqualizerApoService backend, WindowsAudioEndpointService endpoints)
    {
        this.store = store;
        this.backend = backend;
        this.endpoints = endpoints;
        document = store.Load();
        current = new(document.CurrentProfile.Copy(), GetPresets(), [], new(),
            store.LastError?.Message ?? "请选择音效目标并检查音频组件", 0, true);
    }

    public AudioControlSnapshot Current => Volatile.Read(ref current);
    public event Action<AudioControlSnapshot>? Changed;

    public async Task<AudioControlSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var items = await endpoints.GetEndpointsAsync(cancellationToken);
            if (string.IsNullOrEmpty(document.CurrentProfile.EndpointId) && items.Count > 0)
            {
                var initial = items.FirstOrDefault(item => item.Name.Contains("Halo", StringComparison.OrdinalIgnoreCase)
                    || item.Name.Contains("花再", StringComparison.Ordinal))
                    ?? items.FirstOrDefault(item => item.IsDefault) ?? items[0];
                var next = document with { CurrentProfile = document.CurrentProfile with { EndpointId = initial.Id } };
                if (store.Save(next)) document = next;
            }
            var status = await Task.Run(() => backend.Probe(document.CurrentProfile), cancellationToken);
            var offline = !items.Any(item => item.Id == document.CurrentProfile.EndpointId);
            return Publish(status, items, offline ? "音效目标当前不在线，请重新选择输出设备" : status.Message, !offline);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return Publish(Current.Backend, [], $"读取音频设备失败：{exception.Message}", false);
        }
        finally { gate.Release(); }
    }

    public Task<AudioControlSnapshot> UpdateProfileAsync(AudioControlProfile profile, CancellationToken cancellationToken = default)
        => ConfigureAsync(_ => profile.Copy(), cancellationToken);

    public async Task<AudioControlSnapshot> ConfigureAsync(
        Func<AudioControlProfile, AudioControlProfile> change,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await SaveAndApplyAsync(change(document.CurrentProfile.Copy()), cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<AudioControlSnapshot> InitializeBackendAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Validate(document.CurrentProfile);
            var items = await endpoints.GetEndpointsAsync(cancellationToken);
            if (!items.Any(item => item.Id == document.CurrentProfile.EndpointId))
                return Publish(Current.Backend, items, "音效目标不在线，未接入配置", false);
            var status = await backend.ConnectAsync(document.CurrentProfile.Copy(), cancellationToken);
            return Publish(status, items, status.Message, status.CanApply && status.ConfigurationWritten);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return Publish(Current.Backend, Current.Endpoints, $"接入音效配置失败：{exception.Message}", false);
        }
        finally { gate.Release(); }
    }

    public async Task<AudioControlSnapshot> ApplyPresetAsync(string reference, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var preset = ResolvePreset(reference);
            // Presets contain a curve, never a routing change or a forced effect-power change.
            return await SaveAndApplyAsync(preset.Profile.Copy() with
            {
                EndpointId = document.CurrentProfile.EndpointId,
                Enabled = document.CurrentProfile.Enabled
            }, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public AudioControlPreset ResolvePreset(string reference)
    {
        var presets = Current.Presets;
        var exactId = presets.FirstOrDefault(preset => preset.Id == reference);
        if (exactId is not null) return exactId;
        var resolved = DeviceNameResolver.Resolve(reference, presets.Select(preset =>
            new DeviceLookupCandidate<AudioControlPreset>(preset, preset.Name,
                preset.Id == "flat" ? ["原声", "默认", "平直", "flat"] : [])), ["音效", "曲线", "预设"]);
        if (!resolved.IsResolved || resolved.Value is null)
            throw new ArgumentException($"{resolved.Error ?? "无法唯一确定音效预设"}。可选：{string.Join("、", resolved.Suggestions)}");
        return resolved.Value;
    }

    public async Task<AudioControlSnapshot> SavePresetAsync(string name, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 32 || name.Any(char.IsControl))
            throw new ArgumentException("预设名称须为 1–32 个字符");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (DeviceNameResolver.Normalize(name) == DeviceNameResolver.Normalize("平直原声"))
                throw new ArgumentException("平直原声是内置预设，请使用其他名称");
            var list = document.Presets.ToList();
            var existing = list.FindIndex(item => DeviceNameResolver.Normalize(item.Name) == DeviceNameResolver.Normalize(name));
            if (existing >= 0 && list[existing].Id == "flat")
                throw new ArgumentException("原声是内置预设，请使用其他名称");
            var preset = new AudioControlPreset
            {
                Id = existing >= 0 ? list[existing].Id : Guid.NewGuid().ToString("N"),
                Name = name, Profile = document.CurrentProfile.Copy()
            };
            if (existing >= 0) list[existing] = preset; else list.Add(preset);
            if (list.Count > AudioProfileStore.MaximumPresetCount) throw new ArgumentException("最多保存 32 个音效预设");
            var next = document with { Presets = list };
            if (!store.Save(next)) return Publish(Current.Backend, Current.Endpoints, store.LastError?.Message ?? "预设保存失败", false);
            document = next;
            return Publish(Current.Backend, Current.Endpoints, $"已保存音效预设“{name}”", true);
        }
        finally { gate.Release(); }
    }

    public async Task<AudioControlSnapshot> DeletePresetAsync(string id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (id == "flat") throw new ArgumentException("内置平直原声不能删除");
            var list = document.Presets.Where(item => item.Id != id).ToList();
            if (list.Count == document.Presets.Count) throw new ArgumentException("该预设已不存在");
            var next = document with { Presets = list };
            if (!store.Save(next)) return Publish(Current.Backend, Current.Endpoints, store.LastError?.Message ?? "预设保存失败", false);
            document = next;
            return Publish(Current.Backend, Current.Endpoints, "已删除音效预设", true);
        }
        finally { gate.Release(); }
    }

    public async Task<AudioControlSnapshot> SetDefaultOutputAsync(string id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var result = await endpoints.SetDefaultEndpointAsync(id, cancellationToken);
            var items = await endpoints.GetEndpointsAsync(cancellationToken);
            return Publish(backend.Probe(document.CurrentProfile), items, result.Message, result.Success);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return Publish(Current.Backend, Current.Endpoints, $"切换系统输出失败：{exception.Message}", false);
        }
        finally { gate.Release(); }
    }

    public void OpenConfigurator()
    {
        var path = backend.Probe(Current.Profile).ConfiguratorPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException("尚未找到音效组件的设备配置器，请先安装组件");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void OpenOfficialDownload()
        => Process.Start(new ProcessStartInfo("https://sourceforge.net/projects/equalizerapo/files/") { UseShellExecute = true });

    public static void OpenSoundSettings()
        => Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });

    private async Task<AudioControlSnapshot> SaveAndApplyAsync(AudioControlProfile profile, CancellationToken cancellationToken)
    {
        Validate(profile);
        var items = await endpoints.GetEndpointsAsync(cancellationToken);
        if (!items.Any(item => item.Id == profile.EndpointId))
            throw new ArgumentException("音效目标不在线，请重新选择输出设备");
        cancellationToken.ThrowIfCancellationRequested();
        var next = document with { CurrentProfile = profile.Copy() };
        if (!store.Save(next)) return Publish(Current.Backend, items, store.LastError?.Message ?? "音效参数保存失败", false);
        document = next;
        try
        {
            var status = await backend.ApplyAsync(profile.Copy(), cancellationToken: cancellationToken);
            if (!status.CanApply || !status.ConfigurationWritten)
                status = status with { Message = "曲线已保存，尚未生效：" + status.Message };
            return Publish(status, items, status.Message, status.CanApply && status.ConfigurationWritten);
        }
        catch (OperationCanceledException)
        {
            Publish(backend.Probe(profile), items, "曲线已保存，应用操作已取消，请重新检查组件状态", false);
            throw;
        }
        catch (Exception exception)
        {
            return Publish(backend.Probe(profile), items, $"曲线已保存，但尚未应用：{exception.Message}", false);
        }
    }

    public static void Validate(AudioControlProfile profile)
    {
        if (!double.IsFinite(profile.PreampDb) || profile.PreampDb is < -30 or > 12)
            throw new ArgumentException("前级增益须为 -30 到 12 dB");
        if (!double.IsFinite(profile.Balance) || profile.Balance is < -100 or > 100)
            throw new ArgumentException("左右平衡须为 -100 到 100");
        if (profile.BandGainsDb is null || profile.BandGainsDb.Length != 10
            || profile.BandGainsDb.Any(gain => !double.IsFinite(gain) || gain is < -12 or > 12))
            throw new ArgumentException("均衡器须提供 10 段 -12 到 12 dB 的数值");
    }

    private IReadOnlyList<AudioControlPreset> GetPresets()
        => new[] { new AudioControlPreset { Id = "flat", Name = "平直原声", Profile = new() } }
            .Concat(document.Presets.Where(item => item.Id != "flat"))
            .Select(item => item with { Profile = item.Profile.Copy() }).ToArray();

    private AudioControlSnapshot Publish(AudioBackendStatus status, IReadOnlyList<AudioEndpointInfo> items, string message, bool success)
    {
        var snapshot = new AudioControlSnapshot(document.CurrentProfile.Copy(), GetPresets(), items, status,
            message, Current.Revision + 1, success);
        Volatile.Write(ref current, snapshot);
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action<AudioControlSnapshot>)subscriber)(snapshot); }
            catch (Exception exception) { Console.WriteLine($"[WARN]音频界面刷新失败：{exception.Message}"); }
        }
        return snapshot;
    }
}
