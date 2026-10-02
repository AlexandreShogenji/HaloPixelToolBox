using System.Diagnostics;
using System.Text;
using HaloPixelToolBox.Core.Models.Subtitles;
using Microsoft.Win32;
using Windows.Media.Control;

namespace HaloPixelToolBox.Core.Services.Subtitles;

public sealed class PotPlayerPlaybackStateReader
{
    private static GlobalSystemMediaTransportControlsSessionManager? mediaSessionManager;

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
        => GetSnapshotAsync().GetAwaiter().GetResult().State;

    public async Task<PotPlayerPlaybackSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var processSnapshot = GetProcessSnapshot();
        if (processSnapshot.State == PotPlayerPlaybackState.NotRunning)
            return processSnapshot;

        return await TryReadMediaSessionSnapshotAsync(cancellationToken) ?? processSnapshot;
    }

    public string? TryGetCurrentMediaPath()
    {
        var processFolders = ProcessNames
            .SelectMany(Process.GetProcessesByName)
            .Select(process => SafeReadProcessPath(process))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>();

        foreach (var playlistPath in GetPlaylistPaths(processFolders))
        {
            var mediaPath = TryReadCurrentMediaPathFromPlaylist(playlistPath);
            if (!string.IsNullOrWhiteSpace(mediaPath))
                return mediaPath;
        }

        return null;
    }

    private static PotPlayerPlaybackSnapshot GetProcessSnapshot()
    {
        var processes = ProcessNames
            .SelectMany(Process.GetProcessesByName)
            .ToList();

        if (processes.Count == 0)
            return PotPlayerPlaybackSnapshot.NotRunning;

        var titles = processes
            .Select(process => SafeReadTitle(process))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToList();

        if (titles.Any(title => PauseKeywords.Any(keyword => title.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
            return new PotPlayerPlaybackSnapshot
            {
                State = PotPlayerPlaybackState.Paused,
                Source = "PotPlayer 进程窗口"
            };

        return new PotPlayerPlaybackSnapshot
        {
            State = titles.Count > 0 ? PotPlayerPlaybackState.Playing : PotPlayerPlaybackState.RunningUnknown,
            Source = "PotPlayer 进程窗口"
        };
    }

    private static async Task<PotPlayerPlaybackSnapshot?> TryReadMediaSessionSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            // PotPlayer 若接入了系统媒体控制中心，可通过 Windows 媒体会话读取真实播放进度。
            var manager = await GetMediaSessionManagerAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var session = manager.GetSessions()
                .FirstOrDefault(item => IsPotPlayerSession(item.SourceAppUserModelId));
            if (session is null)
                return null;

            var playbackInfo = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var state = ConvertPlaybackStatus(playbackInfo.PlaybackStatus);
            var duration = NormalizeTimelinePosition(timeline.EndTime);
            var position = NormalizeTimelinePosition(timeline.Position);
            if (position is not null && state == PotPlayerPlaybackState.Playing)
            {
                var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                if (elapsed is { Ticks: > 0 } && elapsed < TimeSpan.FromSeconds(5))
                    position += elapsed;

                if (duration is not null && position > duration)
                    position = duration;
            }

            return new PotPlayerPlaybackSnapshot
            {
                State = state,
                Position = position,
                Duration = duration,
                Source = $"Windows 媒体会话：{session.SourceAppUserModelId}"
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionManager> GetMediaSessionManagerAsync()
    {
        mediaSessionManager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return mediaSessionManager;
    }

    private static bool IsPotPlayerSession(string? sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
            return false;

        return ProcessNames.Any(name => sourceAppUserModelId.Contains(name, StringComparison.OrdinalIgnoreCase))
            || sourceAppUserModelId.Contains("PotPlayer", StringComparison.OrdinalIgnoreCase);
    }

    private static PotPlayerPlaybackState ConvertPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PotPlayerPlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PotPlayerPlaybackState.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PotPlayerPlaybackState.Paused,
            _ => PotPlayerPlaybackState.RunningUnknown
        };
    }

    private static TimeSpan? NormalizeTimelinePosition(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            return null;

        return value;
    }

    private static IEnumerable<string> GetPlaylistPaths(IEnumerable<string> processFolders)
    {
        var settingsKey = Registry.CurrentUser.OpenSubKey(@"Software\DAUM\PotPlayerMini64\Settings");
        var playlistName = settingsKey?.GetValue("LastPlayListName") as string;
        if (string.IsNullOrWhiteSpace(playlistName))
            playlistName = "PotPlayerMini64.dpl";

        var programFolder = Registry.CurrentUser.OpenSubKey(@"Software\DAUM\PotPlayer64")?.GetValue("ProgramFolder") as string;
        var playlistRoots = processFolders
            .Concat(string.IsNullOrWhiteSpace(programFolder) ? [] : [programFolder])
            .Concat([
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Daum", "PotPlayer")
            ])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(folder => new[]
            {
                Path.Combine(folder, "Playlist"),
                folder
            })
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in playlistRoots)
        {
            var lastPlaylist = Path.Combine(root, playlistName);
            if (File.Exists(lastPlaylist))
                yield return lastPlaylist;
        }

        foreach (var playlist in playlistRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.dpl"))
            .OrderByDescending(file => new FileInfo(file).LastWriteTimeUtc))
        {
            yield return playlist;
        }
    }

    private static string? TryReadCurrentMediaPathFromPlaylist(string playlistPath)
    {
        try
        {
            var lines = File.ReadLines(playlistPath, Encoding.UTF8).ToList();
            var playNameLine = lines.FirstOrDefault(line => line.StartsWith("playname=", StringComparison.OrdinalIgnoreCase));
            var playName = playNameLine?["playname=".Length..].Trim();
            if (IsExistingMediaPath(playName))
                return playName;

            return lines
                .Select(ExtractPlaylistFilePath)
                .FirstOrDefault(IsExistingMediaPath);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractPlaylistFilePath(string line)
    {
        var markerIndex = line.IndexOf("*file*", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        return line[(markerIndex + "*file*".Length)..].Trim();
    }

    private static bool IsExistingMediaPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

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

    private static string SafeReadProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
