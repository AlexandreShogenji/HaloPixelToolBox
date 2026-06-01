using System.Diagnostics;
using System.Text;
using XFEExtension.NetCore.MemoryEditor;
using XFEExtension.NetCore.StringExtension;

namespace HaloPixelToolBox.Core.Utilities;

/// <summary>
/// 网易云桌面歌词内存读取器。该类保留为旧版直连方案，后续建议迁移到 ILyricsProvider。
/// </summary>
public class CloudMusicLyricsReader
{
    public nint Address { get; set; }
    public bool UseInputedAddress { get; set; }
    public FileVersionInfo? VersionInfo { get; set; }
    public Version Version { get; set; } = new();
    public MemoryEditor Editor { get; set; } = new();

    /// <summary>
    /// 不同网易云版本的歌词内存地址解析器。新增版本时在此处追加 resolver。
    /// </summary>
    public static Dictionary<string, Func<MemoryEditor, nint>> VersionResolverDictionary { get; } = new()
    {
        { "3.1.30", editor => editor.ResolvePointerAddress("cloudmusic.dll", 0x01DF44D0, 0x120, 0x8, 0x0) },
        { "3.1.29", editor => editor.ResolvePointerAddress("cloudmusic.dll", 0x01DEB4D0, 0x120, 0x8, 0x0) },
        { "3.1.28", editor => editor.ResolvePointerAddress("cloudmusic.dll", 0x01DDF290, 0x120, 0x8, 0x0) },
        { "3.1.27", editor => editor.ResolvePointerAddress("cloudmusic.dll", 0x01DDE290, 0xE0, 0x8, 0xE8, 0x38, 0x118, 0x8, 0x0) },
        { "3.1.26", editor => editor.ResolvePointerAddress("cloudmusic.dll", 0x01DD5130, 0xE8, 0x38, 0x120, 0x18, 0x0) },
        { "3.1.25", editor => editor.ResolvePointerAddress("cloudmusic.dll", 0x01DAFF60, 0xE0, 0x8, 0x128, 0x18, 0x0) }
    };

    public bool Initialize()
    {
        if (GetCloudMusicLyricsProcess() is not Process process)
            return false;

        Editor.CurrentProcess = process;
        VersionInfo = FileVersionInfo.GetVersionInfo(process.MainModule?.FileName ?? string.Empty);
        Version = new Version(VersionInfo?.FileVersion ?? "0.0.0.0");
        return ReresolveAddress();
    }

    public bool TryReadLyrics(out string lyrics)
    {
        lyrics = "无法读取歌词";
        try
        {
            if (Editor.ReadMemory(Address, 200, out var buffer))
            {
                lyrics = Encoding.Unicode.GetString(buffer, 0, GetValidLength(buffer));
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR]读取歌词异常：{ex.Message}");
            Console.WriteLine($"[TRACE]{ex.StackTrace}");
        }

        return false;
    }

    public bool ReresolveAddress()
    {
        try
        {
            if (UseInputedAddress)
                return Address != 0;

            if (Version.Major == 0)
                return false;

            if (!VersionResolverDictionary.TryGetValue(Version.ToString(3), out var resolver))
            {
                Console.WriteLine($"[WARN]未找到匹配的网易云版本解析器：{Version}");
                return false;
            }

            var address = resolver(Editor);
            if (address == 0)
                return false;

            Address = address;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR]解析网易云歌词地址异常：{ex.Message}");
            Console.WriteLine($"[TRACE]{ex.StackTrace}");
            return false;
        }
    }

    public static int GetValidLength(byte[] buffer)
    {
        var length = 0;
        for (var i = 0; i < buffer.Length - 1; i += 2)
        {
            if (buffer[i] == 0 && buffer[i + 1] == 0)
                break;

            length += 2;
        }

        return length;
    }

    public static Process? GetCloudMusicLyricsProcess()
    {
        foreach (var process in Process.GetProcesses())
        {
            if (process.ProcessName == "cloudmusic" &&
                (process.MainWindowTitle == "桌面歌词" || !process.MainWindowTitle.IsNullOrWhiteSpace()))
                return process;
        }

        return null;
    }
}
