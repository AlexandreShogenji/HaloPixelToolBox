using XFEExtension.NetCore.AutoConfig;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace HaloPixelToolBox.Profiles.CrossVersionProfiles;

public partial class DisplayFeatureProfile : XFEProfile
{
    public DisplayFeatureProfile() => ProfilePath = $@"{AppPathHelper.LocalProfile}\{nameof(DisplayFeatureProfile)}";

    [ProfileProperty]
    private string clockTemplateDirectory = Path.Combine(AppPathHelper.LocalProfile, "ClockTemplates");

    [ProfileProperty]
    private string selectedClockTemplateId = "builtin-clock-ui";

    [ProfileProperty]
    private int clockRefreshInterval = 1000;

    [ProfileProperty]
    private string videoSubtitlePath = string.Empty;

    [ProfileProperty]
    private string videoPath = string.Empty;

    [ProfileProperty]
    private string translationApiEndpoint = string.Empty;

    [ProfileProperty]
    private string translationApiKey = string.Empty;

    [ProfileProperty]
    private string browserProcessName = "msedge";
}
