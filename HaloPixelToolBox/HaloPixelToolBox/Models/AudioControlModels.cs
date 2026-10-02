using System.Text.Json.Serialization;

namespace HaloPixelToolBox.Models;

public sealed record AudioEndpointInfo(
    string Id,
    string Name,
    bool IsDefault = false,
    bool IsDefaultCommunications = false);

public sealed record AudioControlProfile
{
    public bool Enabled { get; init; }
    public string EndpointId { get; init; } = string.Empty;
    public double PreampDb { get; init; }
    /// <summary>Negative values attenuate the right channel; positive values attenuate the left.</summary>
    public double Balance { get; init; }
    public double[] BandGainsDb { get; init; } = new double[10];
    public bool AutoHeadroom { get; init; } = true;

    [JsonIgnore]
    public static IReadOnlyList<double> FrequenciesHz { get; } =
        Array.AsReadOnly(new double[] { 31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 });

    [JsonIgnore]
    public double EffectivePreampDb => AutoHeadroom
        ? Math.Min(PreampDb, -Math.Max(0, BandGainsDb.Max()))
        : PreampDb;

    public AudioControlProfile Copy() => this with { BandGainsDb = (double[])BandGainsDb.Clone() };
}

public sealed record AudioControlPreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "未命名音效";
    public AudioControlProfile Profile { get; init; } = new();
}

public sealed record AudioControlDocument
{
    public int SchemaVersion { get; init; } = 1;
    public AudioControlProfile CurrentProfile { get; init; } = new();
    public IReadOnlyList<AudioControlPreset> Presets { get; init; } = [];
}

public enum AudioEndpointRegistration
{
    Unknown,
    NotRegistered,
    Registered
}

public sealed record AudioBackendStatus
{
    public bool IsInstalled { get; init; }
    public string InstallDirectory { get; init; } = string.Empty;
    public string ConfigDirectory { get; init; } = string.Empty;
    public string ConfiguratorPath { get; init; } = string.Empty;
    public string ManagedConfigPath { get; init; } = string.Empty;
    public bool IsIncludeConnected { get; init; }
    public AudioEndpointRegistration EndpointRegistration { get; init; }
    public bool EnhancementsDisabled { get; init; }
    public bool CanApply { get; init; }
    public bool ConfigurationWritten { get; init; }
    public bool RequiresElevation { get; init; }
    public string Message { get; init; } = "尚未检测音频组件";
}
