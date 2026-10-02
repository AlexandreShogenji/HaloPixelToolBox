using System.Text.Json;
using HaloPixelToolBox.Core.Models.DeviceControl;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services.Audio;

namespace HaloPixelToolBox.Services.Audio
{
    public sealed record AudioOutputChangeResult(bool Success, string Message, IReadOnlyList<string> FailedRoles);

    // The coordinator uses an isolated endpoint double; no native playback setter is linked.
    public sealed class WindowsAudioEndpointService
    {
        public IReadOnlyList<AudioEndpointInfo> Items { get; set; } = [];
        public int ChangeCount { get; private set; }
        public string? LastChangedId { get; private set; }
        public bool AllowChange { get; set; } = true;
        public Exception? ReadException { get; set; }
        public Action? AfterRead { get; set; }
        public Task<IReadOnlyList<AudioEndpointInfo>> GetEndpointsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadException is not null) throw ReadException;
            AfterRead?.Invoke();
            return Task.FromResult(Items);
        }
        public Task<AudioOutputChangeResult> SetDefaultEndpointAsync(string endpointId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChangeCount++;
            LastChangedId = endpointId;
            if (AllowChange)
                Items = Items.Select(item => item with { IsDefault = item.Id == endpointId, IsDefaultCommunications = item.Id == endpointId }).ToArray();
            return Task.FromResult(new AudioOutputChangeResult(AllowChange, AllowChange ? "已切换" : "Windows 拒绝切换", AllowChange ? [] : ["媒体播放"]));
        }
    }
}

namespace HaloPixelToolBox.Services
{
    public sealed partial class DeviceControlPipeServer
    {
        private readonly AudioControlService audioControl;
        public DeviceControlPipeServer(AudioControlService audioControl) => this.audioControl = audioControl;
        public Task<DeviceCommandResult<AudioControlSnapshot>> ConfigureForTestAsync(string json)
            => ConfigureAudioAsync(JsonSerializer.Deserialize<JsonElement>(json), CancellationToken.None);
        public Task<DeviceCommandResult<AudioControlSnapshot>> OutputForTestAsync(string reference)
            => SetAudioOutputAsync(JsonSerializer.SerializeToElement(new { device = reference }), CancellationToken.None);
        public Task<DeviceCommandResult<AudioControlSnapshot>> OutputJsonForTestAsync(string json)
            => SetAudioOutputAsync(JsonSerializer.Deserialize<JsonElement>(json), CancellationToken.None);
        public Task<DeviceCommandResult<AudioControlSnapshot>> StatusForTestAsync()
            => GetAudioStatusAsync(CancellationToken.None);

        // These common transport readers are kept identical to DeviceControlPipeServer.cs.
        // The production audio parser and coordinator are linked directly above.
        private static bool? GetOptionalBoolean(JsonElement parameters, string name)
        {
            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException($"参数 {name} 必须是布尔值");
            return value.GetBoolean();
        }
        private static string? GetOptionalString(JsonElement parameters, string name)
        {
            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"参数 {name} 必须是字符串");
            return value.GetString()?.Trim();
        }
        private static string GetRequiredString(JsonElement parameters, string name)
        {
            var result = GetOptionalString(parameters, name);
            if (string.IsNullOrWhiteSpace(result)) throw new ArgumentException($"参数 {name} 必须是非空字符串");
            return result;
        }
    }
}
