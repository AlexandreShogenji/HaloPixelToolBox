using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

/// <summary>
/// 读取 QQ 音乐客户端保存的 QQMusicLyricNew 加密 QRC 缓存。
/// 缓存位置通过 QQ 自己的 WebkitCachePath.ini 定位，因此不依赖固定安装目录或内存地址。
/// </summary>
public sealed class QQMusicLocalQrcCacheReader
{
    private const string QrcHeaderHex = "9825B0ACE3028368E8FC6C";
    private const string XorKeyHex = "629F5B0900C35E95239F13117ED8923FBC90BB740EC347743D90AA3F51D8F411849FDE951DC3C609D59FFA66F9D8F0F7A090A1D6F3C3F3D6A190A0F7F0D8F966FA9FD509C6C31D95DE9F8411F4D8513FAA903D7447C30E74BB90BC3F92D87E11139F23955EC300095B9F6266A1D852F76790CAD64AC34AD6CA9067F752D8A166";
    private static readonly byte[] QrcHeader = Convert.FromHexString(QrcHeaderHex);
    private static readonly byte[] XorKey = Convert.FromHexString(XorKeyHex);
    private static readonly Regex CachePathRegex = new(@"^\s*Path\s*=\s*(?<path>.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    // QQ 会把整首 QRC 放进同一个 XML 属性，不能依赖换行；下一条行级时间戳才是边界。
    private static readonly Regex QrcLineRegex = new(
        @"\[(?<start>\d{1,7}),(?<duration>\d{1,7})\](?<text>.*?)(?=\[\d{1,7},\d{1,7}\]|$)",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex WordTimingRegex = new(@"\(\d{1,7},\d{1,7}\)", RegexOptions.Compiled);

    public QQMusicLyricsScanResult Read(string title, string artist)
    {
        try
        {
            var cacheDirectory = ResolveQrcCacheDirectory();
            if (cacheDirectory is null)
                return QQMusicLyricsScanResult.Failed("未找到 QQ 音乐歌词缓存目录（QQMusicLyricNew）");

            var qrcFile = FindMatchingQrcFile(cacheDirectory, title, artist);
            if (qrcFile is null)
                return QQMusicLyricsScanResult.Failed($"QQ 音乐尚未写入当前歌曲的本地 QRC 缓存：{title} - {artist}".TrimEnd(' ', '-'));

            var qrcXml = DecryptQrcFile(qrcFile);
            var lines = ParseQrcLines(qrcXml);
            if (lines.Count == 0)
                return QQMusicLyricsScanResult.Failed($"已读取 QQ 本地 QRC，但未解析出歌词行：{Path.GetFileName(qrcFile)}");

            return QQMusicLyricsScanResult.Succeeded(
                title,
                artist,
                lines,
                $"QQ 音乐本地 QRC：{Path.GetFileName(qrcFile)}",
                $"cache={cacheDirectory}");
        }
        catch (Exception ex)
        {
            return QQMusicLyricsScanResult.Failed($"QQ 音乐本地 QRC 读取失败：{ex.Message}");
        }
    }

    private static string? ResolveQrcCacheDirectory()
    {
        var iniPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Tencent",
            "QQMusic",
            "WebkitCachePath.ini");
        if (!File.Exists(iniPath))
            return null;

        var iniContent = File.ReadAllText(iniPath, Encoding.UTF8);
        var pathMatch = CachePathRegex.Match(iniContent);
        if (!pathMatch.Success)
            return null;

        var webkitCachePath = pathMatch.Groups["path"].Value.Trim().Trim('"');
        var cacheRoot = Directory.GetParent(webkitCachePath)?.FullName;
        var qrcCacheDirectory = string.IsNullOrWhiteSpace(cacheRoot)
            ? null
            : Path.Combine(cacheRoot, "QQMusicLyricNew");
        return qrcCacheDirectory is not null && Directory.Exists(qrcCacheDirectory)
            ? qrcCacheDirectory
            : null;
    }

    private static string? FindMatchingQrcFile(string cacheDirectory, string title, string artist)
    {
        var expectedTitle = NormalizeForComparison(title);
        var expectedArtist = NormalizeForComparison(artist);
        return Directory.EnumerateFiles(cacheDirectory, "*.qrc", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith("_qm.qrc", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                Path = path,
                Name = NormalizeForComparison(Path.GetFileNameWithoutExtension(path)),
                LastWriteTime = File.GetLastWriteTimeUtc(path)
            })
            .Select(item => new
            {
                item.Path,
                item.LastWriteTime,
                Score = ScoreFilename(item.Name, expectedTitle, expectedArtist)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.LastWriteTime)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    private static int ScoreFilename(string fileName, string title, string artist)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(title) && fileName.Contains(title, StringComparison.Ordinal))
            score += 100;
        if (!string.IsNullOrWhiteSpace(artist) && fileName.Contains(artist, StringComparison.Ordinal))
            score += 80;
        return score;
    }

    private static string DecryptQrcFile(string path)
    {
        var encrypted = File.ReadAllBytes(path);
        if (encrypted.AsSpan().StartsWith(QrcHeader))
            encrypted = encrypted[QrcHeader.Length..];

        if (encrypted.Length == 0 || encrypted.Length % 8 != 0)
            throw new InvalidDataException("QRC 加密数据长度无效");

        for (var index = 0; index < encrypted.Length; index++)
            encrypted[index] ^= XorKey[index % XorKey.Length];

        var firstPass = QQMusicQrcCipher.Transform(encrypted, Encoding.UTF8.GetBytes("!@#)(NHL"), decrypt: true);
        var secondPass = QQMusicQrcCipher.Transform(firstPass, Encoding.UTF8.GetBytes("123ZXC!@"), decrypt: false);
        var compressed = QQMusicQrcCipher.Transform(secondPass, Encoding.UTF8.GetBytes("!@#)(*$%"), decrypt: true);

        using var compressedStream = new MemoryStream(compressed, writable: false);
        using var zlibStream = new ZLibStream(compressedStream, CompressionMode.Decompress);
        using var reader = new StreamReader(zlibStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<SubtitleCue> ParseQrcLines(string qrcXml)
    {
        var document = XDocument.Parse(qrcXml, LoadOptions.PreserveWhitespace);
        var content = document.Descendants("Lyric_1")
            .Attributes("LyricContent")
            .Select(attribute => attribute.Value)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var lines = new Dictionary<long, SubtitleCue>();
        foreach (Match match in QrcLineRegex.Matches(content))
        {
            if (!long.TryParse(match.Groups["start"].Value, out var startMilliseconds) ||
                !long.TryParse(match.Groups["duration"].Value, out var durationMilliseconds))
            {
                continue;
            }

            var text = WordTimingRegex.Replace(match.Groups["text"].Value, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines[startMilliseconds] = new SubtitleCue
            {
                Start = TimeSpan.FromMilliseconds(startMilliseconds),
                End = TimeSpan.FromMilliseconds(startMilliseconds + Math.Max(0, durationMilliseconds)),
                Text = text
            };
        }

        return lines.Values.OrderBy(line => line.Start).ToList();
    }

    private static string NormalizeForComparison(string value)
    {
        return new string(value
                .Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character))
                .ToArray())
            .ToUpperInvariant();
    }
}
