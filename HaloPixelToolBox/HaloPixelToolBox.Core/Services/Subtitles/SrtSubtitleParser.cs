using HaloPixelToolBox.Core.Models.Subtitles;
using System.Globalization;

namespace HaloPixelToolBox.Core.Services.Subtitles;

public class SrtSubtitleParser : ISubtitleParser
{
    public string FormatName => "SRT";

    public bool CanParse(string filePath) => Path.GetExtension(filePath).Equals(".srt", StringComparison.OrdinalIgnoreCase);

    public SubtitleDocument Parse(string filePath)
    {
        var blocks = File.ReadAllText(filePath).ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var document = new SubtitleDocument { SourceName = Path.GetFileName(filePath) };

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (lines.Count < 2)
                continue;

            if (!lines[0].Contains("-->"))
                lines.RemoveAt(0);

            if (lines.Count < 2 || !TryParseRange(lines[0], out var start, out var end))
                continue;

            document.Cues.Add(new SubtitleCue
            {
                Start = start,
                End = end,
                Text = string.Join(" ", lines.Skip(1)).Trim()
            });
        }

        return document;
    }

    private static bool TryParseRange(string line, out TimeSpan start, out TimeSpan end)
    {
        start = TimeSpan.Zero;
        end = TimeSpan.Zero;
        var parts = line.Split("-->", StringSplitOptions.TrimEntries);
        return parts.Length == 2 && TryParseTime(parts[0], out start) && TryParseTime(parts[1], out end);
    }

    private static bool TryParseTime(string value, out TimeSpan time)
    {
        value = value.Replace(',', '.');
        return TimeSpan.TryParseExact(value, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out time);
    }
}
