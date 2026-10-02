using System.Text;
using System.Text.Json;

namespace HaloPixelToolBox.Services;

/// <summary>Keep the device identity separate from versioned UI profiles.</summary>
internal static class DshDeviceSessionIdentity
{
    public static string Read(string path, string home, string profile)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return string.Empty;
            var identity = JsonSerializer.Deserialize<Identity>(File.ReadAllText(path));
            return identity is { Version: 1 } && string.Equals(identity.Home, home, StringComparison.OrdinalIgnoreCase)
                && identity.Profile == profile && Guid.TryParse(identity.SessionId, out _)
                ? identity.SessionId : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public static void Save(string path, string home, string profile, string sessionId)
    {
        if (!Guid.TryParse(sessionId, out _)) throw new ArgumentException("音箱会话 ID 无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Identity(1, home, profile, sessionId)), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record Identity(int Version, string Home, string Profile, string SessionId);
}
