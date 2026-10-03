using System.Collections.Concurrent;
using HaloPixelToolBox.Models;

// These are transport seams only. Formatting, delivery cancellation, observer handling,
// and deduplication execute the production DshTaskFeedback implementation.
namespace HaloPixelToolBox
{
    internal static class App
    {
        internal static readonly FeedbackTaskSource DshTasks = new();
        internal static readonly FeedbackVoice VoiceAgent = new();
        internal static readonly FeedbackLyrics LyricsSubtitleControl = new();
    }

    internal sealed class FeedbackTaskSource
    {
        public DshTaskSnapshot Current { get; private set; } = DshTaskSnapshot.Initial;
        public event EventHandler<DshTaskSnapshot>? Changed;
        public void Publish(DshTaskSnapshot value)
        {
            Current = value;
            Changed?.Invoke(this, value);
        }
    }

    internal sealed record FeedbackSpeech(string Cue, string Text, bool ListenAfter, DshTaskSnapshot? Snapshot);
    internal sealed class FeedbackVoice
    {
        public ConcurrentQueue<FeedbackSpeech> Spoken { get; } = new();
        public int Invalidations;
        public Task NotifyTaskAsync(string cue, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Spoken.Enqueue(new(cue, "", false, null));
            return Task.CompletedTask;
        }
        public Task NotifyTaskAsync(string cue, string text, bool listenAfter, DshTaskSnapshot snapshot, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Spoken.Enqueue(new(cue, text, listenAfter, snapshot));
            return Task.CompletedTask;
        }
        public Task InvalidateTaskPromptAsync()
        {
            Interlocked.Increment(ref Invalidations);
            return Task.CompletedTask;
        }
    }

    internal sealed class FeedbackLyrics
    {
        public (bool IsRunning, bool Unused) CurrentStatus { get; set; }
        public int StopCalls { get; private set; }
        public Func<CancellationToken, Task>? BeforeStop { get; set; }
        public async Task StopAsync(bool restoreScene, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            if (BeforeStop is { } beforeStop)
                await beforeStop(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CurrentStatus = (false, false);
        }
    }
}

namespace HaloPixelToolBox.Profiles.CrossVersionProfiles
{
    internal static class DisplayFeatureProfile
    {
        public static bool PixelScreenEnabled { get; set; } = true;
        public static bool LightsTurnedOffByAutomation { get; set; }
    }
}

namespace HaloPixelToolBox.Core.Models
{
    internal enum HaloPixelTextLayout { Center }
}

namespace HaloPixelToolBox.Core.Models.Display
{
    internal enum TextScrollDirection { RightToLeft }
    internal sealed class DisplayTextOptions
    {
        public string Text { get; init; } = "";
        public DisplayContentKind Source { get; init; }
        public HaloPixelTextLayout Layout { get; init; }
        public TextScrollDirection ScrollDirection { get; init; }
        public int Speed { get; init; }
        public long? ExpectedForegroundRevision { get; init; }
    }
}

namespace HaloPixelToolBox.Core.Services
{
    using HaloPixelToolBox.Core.Models.Display;

    internal sealed class FeedbackBlockedSend
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    internal sealed class HaloPixelDisplayService
    {
        public static long ForegroundRevision { get; private set; }
        public static event EventHandler<DisplayContentChangedEventArgs>? ContentSent;
        public static void Foreground(DisplayContentKind kind = DisplayContentKind.Scene)
        {
            ForegroundRevision++;
            ContentSent?.Invoke(null, new(kind, null));
        }
        public static ConcurrentQueue<DisplayTextOptions> Sent { get; } = new();
        public static ConcurrentQueue<string> Attempted { get; } = new();
        public static FeedbackBlockedSend? BlockNext;
        public async Task<bool> SendTextAsync(DisplayTextOptions options, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Attempted.Enqueue(options.Text);
            if (Interlocked.Exchange(ref BlockNext, null) is { } blocked)
                await blocked.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (options.ExpectedForegroundRevision is { } expected && expected != ForegroundRevision) return false;
            Sent.Enqueue(options);
            return true;
        }
    }
}
