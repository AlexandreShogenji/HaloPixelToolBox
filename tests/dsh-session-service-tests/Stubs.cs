namespace HaloPixelToolBox.Services
{
    internal static class DeviceControlPipeServer
    {
        public const string PipeName = "unused-by-session-service-tests";
    }
}

namespace HaloPixelToolBox.Profiles.CrossVersionProfiles
{
    internal static class DisplayFeatureProfile
    {
        public static string DshExecutablePath { get; set; } = string.Empty;
        public static string DshHomePath { get; set; } = string.Empty;
        public static string DshProfileName { get; set; } = "halo-pixelbar";
        public static string DshVoiceTargetSessionId { get; set; } = string.Empty;
        public static string DshVoiceTargetHomePath { get; set; } = string.Empty;
        public static string DshVoiceTargetTitle { get; set; } = string.Empty;
        public static string DshVoiceTargetWorkingDirectory { get; set; } = string.Empty;
        public static string DshDeviceSessionId { get; set; } = string.Empty;
        public static string DshDeviceSessionHomePath { get; set; } = string.Empty;
        public static string DshDeviceSessionProfileName { get; set; } = string.Empty;
    }
}
