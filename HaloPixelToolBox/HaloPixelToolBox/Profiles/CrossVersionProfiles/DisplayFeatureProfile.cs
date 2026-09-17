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
    private bool browserBilibiliMusicModeEnabled;

    [ProfileProperty]
    private string browserBilibiliMusicSongTitle = string.Empty;

    [ProfileProperty]
    private string browserBilibiliMusicArtist = string.Empty;

    [ProfileProperty]
    private double browserBilibiliMusicLyricsSyncOffsetMilliseconds;

    [ProfileProperty]
    private string lastToolPageName = "HaloPixelToolBox.Views.MainPage";

    [ProfileProperty]
    private int lyricsProviderIndex = 3;

    [ProfileProperty]
    private int ambientLightRed = 45;

    [ProfileProperty]
    private int ambientLightGreen;

    [ProfileProperty]
    private int ambientLightBlue = 179;

    [ProfileProperty]
    private bool ambientLightEnabled = true;

    [ProfileProperty]
    private int ambientLightEffectIndex = 2;

    [ProfileProperty]
    private int ambientLightBrightnessIndex = 2;

    [ProfileProperty]
    private double ambientLightSpeed = 10;

    [ProfileProperty]
    private bool syncAmbientWithPixel;

    [ProfileProperty]
    private int pixelScreenRed;

    [ProfileProperty]
    private int pixelScreenGreen = 85;

    [ProfileProperty]
    private int pixelScreenBlue = 170;

    [ProfileProperty]
    private bool pixelScreenEnabled = true;

    [ProfileProperty]
    private int quickActionSlotOneIndex;

    [ProfileProperty]
    private int quickActionSlotTwoIndex = 1;

    [ProfileProperty]
    private int quickActionSlotThreeIndex = 2;

    [ProfileProperty]
    private int quickActionSlotFourIndex = 5;
}
