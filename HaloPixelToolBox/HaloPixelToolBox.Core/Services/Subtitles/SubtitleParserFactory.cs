namespace HaloPixelToolBox.Core.Services.Subtitles;

public class SubtitleParserFactory
{
    public IReadOnlyList<ISubtitleParser> Parsers { get; } =
    [
        new SrtSubtitleParser(),
        new VttSubtitleParser()
    ];

    public ISubtitleParser GetParser(string filePath)
    {
        return Parsers.FirstOrDefault(parser => parser.CanParse(filePath))
            ?? throw new NotSupportedException($"暂不支持该字幕格式：{Path.GetExtension(filePath)}");
    }
}
