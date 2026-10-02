using HaloPixelToolBox.Core.Models.Subtitles;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public class LrcLyricsParser
{
    private static readonly Regex TagRegex = new(@"\[(?<tag>[^\]]+)\]", RegexOptions.Compiled);
    private static readonly TimeSpan DefaultLineDuration = TimeSpan.FromSeconds(3);

    public LyricsTrack ParseFile(string filePath)
    {
        var content = ReadTextWithFallback(filePath);
        var track = Parse(content, Path.GetFileName(filePath));
        track.SourceName = Path.GetFileName(filePath);
        track.RawSource = content;
        return track;
    }

    public LyricsTrack Parse(string content, string sourceName = "")
    {
        var track = new LyricsTrack
        {
            Provider = LyricsProviderKind.LocalFile,
            SourceName = sourceName,
            RawSource = content
        };

        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var fileOffset = TimeSpan.Zero;
        ApplyMetadata(lines, track, ref fileOffset);

        var timedLines = new List<LrcLine>();
        var plainLines = new List<(int Order, string Text)>();
        for (var order = 0; order < lines.Length; order++)
        {
            var line = lines[order].Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var matches = TagRegex.Matches(line);
            if (matches.Count == 0)
            {
                plainLines.Add((order, line));
                continue;
            }

            var timestamps = new List<TimeSpan>();
            foreach (Match match in matches)
            {
                var tag = match.Groups["tag"].Value.Trim();
                if (TryParseTimestamp(tag, out var timestamp))
                    timestamps.Add(ClampToZero(timestamp + fileOffset));
            }

            var text = TagRegex.Replace(line, string.Empty).Trim();
            if (timestamps.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    plainLines.Add((order, text));
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
                continue;

            foreach (var timestamp in timestamps)
                timedLines.Add(new LrcLine(timestamp, text, order));
        }

        if (timedLines.Count == 0)
            AddPlainTextCues(track, plainLines);
        else
            AddTimedCues(track, timedLines, plainLines);

        if (string.IsNullOrWhiteSpace(track.Title))
            track.Title = Path.GetFileNameWithoutExtension(sourceName);

        if (track.Duration is null && track.Lines.Count > 0)
            track.Duration = track.Lines.Max(line => line.End);

        track.Offset = fileOffset;
        track.IsSynced = timedLines.Count > 0;
        track.Confidence = track.Lines.Count > 0 ? 1 : 0;
        return track;
    }

    private static void ApplyMetadata(IEnumerable<string> lines, LyricsTrack track, ref TimeSpan fileOffset)
    {
        foreach (var line in lines)
        {
            foreach (Match match in TagRegex.Matches(line))
            {
                var tag = match.Groups["tag"].Value.Trim();
                if (TryParseTimestamp(tag, out _))
                    continue;

                ApplyMetadataTag(tag, track, ref fileOffset);
            }
        }
    }

    private static void ApplyMetadataTag(string tag, LyricsTrack track, ref TimeSpan fileOffset)
    {
        var separatorIndex = tag.IndexOf(':');
        if (separatorIndex <= 0)
            return;

        var key = tag[..separatorIndex].Trim().ToLowerInvariant();
        var value = tag[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(value))
            return;

        switch (key)
        {
            case "ti":
            case "title":
                track.Title = value;
                break;
            case "ar":
            case "artist":
                track.Artist = value;
                break;
            case "al":
            case "album":
                track.Album = value;
                break;
            case "length":
                if (TryParseTimestamp(value, out var duration))
                    track.Duration = duration;
                break;
            case "offset":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
                    fileOffset = TimeSpan.FromMilliseconds(milliseconds);
                break;
        }
    }

    private static void AddPlainTextCues(LyricsTrack track, IEnumerable<(int Order, string Text)> plainLines)
    {
        var index = 0;
        foreach (var (_, text) in plainLines.OrderBy(line => line.Order))
        {
            track.Lines.Add(new SubtitleCue
            {
                Start = DefaultLineDuration * index,
                End = DefaultLineDuration * (index + 1),
                Text = text
            });
            index++;
        }
    }

    private static void AddTimedCues(LyricsTrack track, IEnumerable<LrcLine> timedLines, IEnumerable<(int Order, string Text)> plainLines)
    {
        var orderedLines = timedLines
            .OrderBy(line => line.Start)
            .ThenBy(line => line.Order)
            .ToList();

        for (var index = 0; index < orderedLines.Count; index++)
        {
            var current = orderedLines[index];
            var nextStart = index + 1 < orderedLines.Count ? orderedLines[index + 1].Start : current.Start + DefaultLineDuration;
            var end = nextStart > current.Start ? nextStart : current.Start + DefaultLineDuration;
            track.Lines.Add(new SubtitleCue
            {
                Start = current.Start,
                End = end,
                Text = current.Text
            });
        }

        var appendStart = track.Lines.Count > 0 ? track.Lines.Max(line => line.End) : TimeSpan.Zero;
        var plainIndex = 0;
        foreach (var (_, text) in plainLines.OrderBy(line => line.Order))
        {
            track.Lines.Add(new SubtitleCue
            {
                Start = appendStart + DefaultLineDuration * plainIndex,
                End = appendStart + DefaultLineDuration * (plainIndex + 1),
                Text = text
            });
            plainIndex++;
        }
    }

    private static bool TryParseTimestamp(string value, out TimeSpan timestamp)
    {
        timestamp = TimeSpan.Zero;
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is not (2 or 3))
            return false;

        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return false;

        if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
            return false;

        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
            return false;

        if (minutes < 0 || seconds < 0 || hours < 0)
            return false;

        timestamp = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static string ReadTextWithFallback(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes[2..]);
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    private static TimeSpan ClampToZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private sealed record LrcLine(TimeSpan Start, string Text, int Order);
}
