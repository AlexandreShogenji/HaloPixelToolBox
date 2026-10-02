using HaloPixelToolBox.Core.Models.Display;

namespace HaloPixelToolBox.Core.Models.DeviceControl;

public sealed record SubtitleControlRequest(
    string Text,
    HaloPixelTextLayout Layout,
    TextScrollDirection ScrollDirection);

public sealed record SubtitleControlStatus(
    string Text,
    int Utf8ByteCount,
    HaloPixelTextLayout EffectiveLayout,
    TextScrollDirection EffectiveScrollDirection);
