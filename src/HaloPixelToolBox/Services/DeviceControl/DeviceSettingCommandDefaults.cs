using System.Text.Json;

namespace HaloPixelToolBox.Services;

/// <summary>Defaults apply only to an invoked categorical command, never to omitted batch fields.</summary>
internal static class DeviceSettingCommandDefaults
{
    public static string ReadReference(JsonElement parameters, string name, string defaultValue = "随机")
    {
        if (parameters.ValueKind == JsonValueKind.Undefined)
            return defaultValue;
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("parameters 必须是对象");
        if (!parameters.TryGetProperty(name, out var value))
            return defaultValue;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"参数 {name} 必须是非空字符串");
        return value.GetString()!.Trim();
    }

    public static (string Mode, string? Reference) ReadAmbientEffect(JsonElement parameters)
    {
        var hasEffect = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("effect", out _);
        var reference = hasEffect ? ReadReference(parameters, "effect") : null;
        var mode = ReadReference(parameters, "mode", hasEffect ? "set" : "random");
        AmbientLightEffectResolver.Parse(mode, reference);
        return (mode, reference);
    }
}
