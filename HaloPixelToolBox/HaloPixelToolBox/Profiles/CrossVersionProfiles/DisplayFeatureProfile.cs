using XFEExtension.NetCore.AutoConfig;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace HaloPixelToolBox.Profiles.CrossVersionProfiles;

public partial class DisplayFeatureProfile : XFEProfile
{
    public DisplayFeatureProfile() => ProfilePath = $@"{AppPathHelper.LocalProfile}\{nameof(DisplayFeatureProfile)}";

    [ProfileProperty]
    private string videoSubtitlePath = string.Empty;

    [ProfileProperty]
    private string potPlayerSubtitleOutputPath = string.Empty;

    [ProfileProperty]
    private bool potPlayerSubtitleSyncEnabled;

    [ProfileProperty]
    private string videoPath = string.Empty;

    [ProfileProperty]
    private string translationApiEndpoint = string.Empty;

    [ProfileProperty]
    private string translationApiKey = string.Empty;

    [ProfileProperty]
    private string browserProcessName = "msedge";
}
