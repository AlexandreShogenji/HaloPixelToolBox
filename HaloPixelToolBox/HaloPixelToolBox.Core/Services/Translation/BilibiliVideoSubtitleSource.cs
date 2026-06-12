using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Translation;

public sealed class BilibiliVideoSubtitleSource
{
    private readonly BilibiliCcSubtitleCapture ccSubtitleCapture;

    public BilibiliVideoSubtitleSource(BilibiliCcSubtitleCapture ccSubtitleCapture)
    {
        this.ccSubtitleCapture = ccSubtitleCapture;
    }

    public async Task<BrowserSubtitleSourceResult> LoadAsync(
        BrowserTranslationConfiguration configuration,
        Action<string> reportStatus,
        CancellationToken cancellationToken)
    {
        reportStatus("正在获取 B 站 CC 字幕...");
        var cues = await ccSubtitleCapture.FetchCuesAsync(configuration, cancellationToken);
        if (cues.Count == 0)
        {
            reportStatus("未获取到 B 站 CC 字幕，正在启动 ASR 兜底...");
            return BrowserSubtitleSourceResult.AsrStreaming();
        }

        return BrowserSubtitleSourceResult.Timeline("B 站 CC", cues);
    }
}
