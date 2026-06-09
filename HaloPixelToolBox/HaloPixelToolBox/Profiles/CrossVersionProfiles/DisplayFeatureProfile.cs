using XFEExtension.NetCore.AutoConfig;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace HaloPixelToolBox.Profiles.CrossVersionProfiles;

public partial class DisplayFeatureProfile : XFEProfile
{
    public DisplayFeatureProfile() => ProfilePath = $@"{AppPathHelper.LocalProfile}\{nameof(DisplayFeatureProfile)}";

    [ProfileProperty]
    private string potPlayerSubtitleOutputPath = string.Empty;

    [ProfileProperty]
    private bool potPlayerSubtitleSyncEnabled;

    [ProfileProperty]
    private string translationApiEndpoint = string.Empty;

    [ProfileProperty]
    private string translationApiKey = string.Empty;

    [ProfileProperty]
    private string browserProcessName = "chrome";

    [ProfileProperty]
    private string bilibiliVideoUrl = string.Empty;

    [ProfileProperty]
    private int browserSubtitleOutputModeIndex = 1;

    [ProfileProperty]
    private int browserSubtitleAsrEngineIndex = 1;

    [ProfileProperty]
    private string lastToolPageName = "HaloPixelToolBox.Views.PersonalSceneToolPage";

    [ProfileProperty]
    private int lyricsProviderIndex = 3;
}
