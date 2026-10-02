using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using System.Text;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

internal static class DshTaskFeedback
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HaloPixelDisplayService Display = new();
    private static string lastText = string.Empty;

    public static async Task PublishAsync(DshTaskSnapshot snapshot, string cue, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (App.DshTasks.Current.SessionId != snapshot.SessionId || !App.DshTasks.Current.IsMonitoring) return;
            if (cue == "task_started" && App.LyricsSubtitleControl.CurrentStatus.IsRunning)
                await App.LyricsSubtitleControl.StopAsync(restoreScene: false, cancellationToken: token);
            var detail = snapshot.NeedsAttention ? snapshot.Detail
                : snapshot.State == "completed" ? snapshot.FinalText : snapshot.Detail;
            var text = FitSubtitle(snapshot.StatusText + (string.IsNullOrWhiteSpace(detail) ? "" : "：" + detail));
            if (text != lastText && DisplayFeatureProfile.PixelScreenEnabled && !DisplayFeatureProfile.LightsTurnedOffByAutomation)
            {
                var sent = await Display.SendTextAsync(new DisplayTextOptions
                {
                    Text = text, Source = DisplayContentKind.TaskStatus, Layout = HaloPixelTextLayout.Center,
                    ScrollDirection = TextScrollDirection.RightToLeft, Speed = 5
                }, token);
                if (sent) lastText = text;
            }
            if (cue.Length > 0) await App.VoiceAgent.NotifyTaskAsync(cue, token);
        }
        finally { Gate.Release(); }
    }

    internal static string FitSubtitle(string text)
    {
        text = Regex.Replace(text, @"[`*#\r\n\t]+", " ").Trim();
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 55) break;
            result.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
