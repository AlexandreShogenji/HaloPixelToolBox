using HaloPixelToolBox.Core.Models.Subtitles;
using System.Globalization;

namespace HaloPixelToolBox.Core.Services.Subtitles;

public class VttSubtitleParser : ISubtitleParser
{
    public string FormatName => "WebVTT";

    public bool CanParse(string filePath) => Path.GetExtension(filePath).Equals(".vtt", StringComparison.OrdinalIgnoreCase);

    public SubtitleDocument Parse(string filePath)
    {
        var lines = File.ReadAllLines(filePath).Where(line => !line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)).ToList();
        var document = new SubtitleDocument { SourceName = Path.GetFileName(filePath) };

        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].Contains("-->"))
                continue;

            if (!TryParseRange(lines[index], out var start, out var end))
                continue;

            var textLines = new List<string>();
            index++;
            while (index < lines.Count && !string.IsNullOrWhiteSpace(lines[index]))
            {
                textLines.Add(lines[index]);
                index++;
            }

            document.Cues.Add(new SubtitleCue
            {
                Start = start,
                End = end,
                Text = string.Join(" ", textLines).Trim()
            });
        }

        return document;
    }

    private static bool TryParseRange(string line, out TimeSpan start, out TimeSpan end)
    {
        start = TimeSpan.Zero;
        end = TimeSpan.Zero;
        var parts = line.Split("-->", StringSplitOptions.TrimEntries);
        return parts.Length == 2 && TryParseTime(parts[0], out start) && TryParseTime(parts[1].Split(' ')[0], out end);
    }

    private static bool TryParseTime(string value, out TimeSpan time)
    {
        var format = value.Count(character => character == ':') == 1 ? @"mm\:ss\.fff" : @"hh\:mm\:ss\.fff";
        return TimeSpan.TryParseExact(value, format, CultureInfo.InvariantCulture, out time);
    }
}
