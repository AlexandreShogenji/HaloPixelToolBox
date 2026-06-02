using System.Diagnostics;
using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Subtitles;

public sealed class PotPlayerPlaybackStateReader
{
    private static readonly string[] ProcessNames =
    [
        "PotPlayerMini64",
        "PotPlayerMini",
        "PotPlayer64",
        "PotPlayer"
    ];

    private static readonly string[] PauseKeywords =
    [
        "暂停",
        "paused",
        "pause"
    ];

    public PotPlayerPlaybackState GetState()
    {
        var processes = ProcessNames
            .SelectMany(Process.GetProcessesByName)
            .ToList();

        if (processes.Count == 0)
            return PotPlayerPlaybackState.NotRunning;

        var titles = processes
            .Select(process => SafeReadTitle(process))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToList();

        if (titles.Any(title => PauseKeywords.Any(keyword => title.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
            return PotPlayerPlaybackState.Paused;

        return titles.Count > 0
            ? PotPlayerPlaybackState.Playing
            : PotPlayerPlaybackState.RunningUnknown;
    }

    private static string SafeReadTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
