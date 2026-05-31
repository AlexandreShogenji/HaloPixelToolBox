using System.Diagnostics;
using XFEExtension.NetCore.StringExtension;

namespace HaloPixelToolBox.Core.Utilities;

public class CloudMusicLyricsReader
{
    public string? CloudMusicVersion { get; private set; }

    public bool Initialize()
    {
        if (GetProcess() is Process process)
        {
            Console.WriteLine($"[DEBUG]已找到云音乐进程：{process.ProcessName}({process.Id})");
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(process.MainModule?.FileName ?? string.Empty);
                CloudMusicVersion = vi.FileVersion;
                Console.WriteLine($"[DEBUG]云音乐版本：{CloudMusicVersion}");
            }
            catch
            {
                CloudMusicVersion = "未知";
            }
            return true;
        }
        return false;
    }

    private static Process? GetProcess()
    {
        foreach (var p in Process.GetProcesses())
        {
            if (p.ProcessName == "cloudmusic" && (p.MainWindowTitle.StartsWith("桌面歌词") || !p.MainWindowTitle.IsNullOrWhiteSpace()))
                return p;
        }
        return null;
    }
}
