using Microsoft.Win32;
using System.Diagnostics;
using System.Text;

namespace HaloPixelToolBox.Uninstaller;

internal static class Program
{
    private const string ProductName = "HaloPixelToolBox";
    private const string MainExecutableName = "HaloPixelToolBox.exe";
    private const string UninstallerDirectoryName = "Uninstaller";
    private const string RegistrySubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HaloPixelToolBox";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var quiet = args.Any(argument =>
            string.Equals(argument, "/quiet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "--quiet", StringComparison.OrdinalIgnoreCase));

        try
        {
            var installDirectory = ResolveAndValidateInstallDirectory(AppContext.BaseDirectory);
            if (!quiet && MessageBox.Show(
                    "将卸载 HaloPixelToolBox，并删除程序文件、桌面快捷方式和开始菜单入口。\n\n个人设置、日志和模型缓存将保留。是否继续？",
                    "卸载 HaloPixelToolBox",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return 0;
            }

            StopInstalledApplication(installDirectory);
            RemoveShortcuts();
            RemoveUninstallRegistration();
            StartDeferredDirectoryRemoval(installDirectory, Environment.ProcessId);

            if (!quiet)
            {
                MessageBox.Show(
                    "HaloPixelToolBox 已卸载。个人设置、日志和模型缓存已保留。",
                    "卸载完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return 0;
        }
        catch (Exception exception)
        {
            if (!quiet)
            {
                MessageBox.Show(
                    $"卸载失败：\n{exception.Message}",
                    "卸载 HaloPixelToolBox",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 1;
        }
    }

    private static string ResolveAndValidateInstallDirectory(string uninstallerBaseDirectory)
    {
        var uninstallerDirectory = Path.GetFullPath(uninstallerBaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directoryInfo = Directory.GetParent(uninstallerDirectory)
            ?? throw new InvalidOperationException("无法确定软件安装目录。");
        var installDirectory = Path.GetFullPath(directoryInfo.FullName)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expectedUninstallerDirectory = Path.Combine(installDirectory, UninstallerDirectoryName);

        if (!string.Equals(
                uninstallerDirectory,
                expectedUninstallerDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("卸载程序不在有效的安装目录中。");
        }

        var rootDirectory = Path.GetPathRoot(installDirectory)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(rootDirectory)
            || string.Equals(installDirectory, rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝卸载磁盘根目录。");
        }

        if (!File.Exists(Path.Combine(installDirectory, MainExecutableName)))
        {
            throw new FileNotFoundException("安装目录中没有找到 HaloPixelToolBox 主程序。");
        }

        return installDirectory;
    }

    private static void StopInstalledApplication(string installDirectory)
    {
        var expectedExecutable = Path.GetFullPath(Path.Combine(installDirectory, MainExecutableName));
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MainExecutableName)))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || !IsExpectedProcess(process, expectedExecutable))
                    continue;

                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between discovery and shutdown.
                }
            }
        }
    }

    private static bool IsExpectedProcess(Process process, string expectedExecutable)
    {
        try
        {
            var processPath = process.MainModule?.FileName;
            return processPath is not null
                && string.Equals(
                    Path.GetFullPath(processPath),
                    expectedExecutable,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return false;
        }
    }

    private static void RemoveShortcuts()
    {
        DeleteFileIfPresent(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "花再工具箱.lnk"));

        DeleteDirectoryIfPresent(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            ProductName));
        DeleteDirectoryIfPresent(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            ProductName));
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void RemoveUninstallRegistration()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            localMachine.DeleteSubKeyTree(RegistrySubKey, throwOnMissingSubKey: false);
        }
    }

    private static void StartDeferredDirectoryRemoval(string installDirectory, int processId)
    {
        var targetBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(installDirectory));
        var script = $$"""
            $target = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{targetBase64}}'))
            try { Wait-Process -Id {{processId}} -Timeout 60 -ErrorAction SilentlyContinue } catch {}
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                try {
                    if (Test-Path -LiteralPath $target) {
                        Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
                    }
                    exit 0
                } catch {
                    Start-Sleep -Milliseconds 500
                }
            }
            exit 1
            """;
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        if (!File.Exists(powerShellPath))
            throw new FileNotFoundException("无法找到 Windows PowerShell，不能完成卸载清理。", powerShellPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);

        using var cleanupProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动卸载清理进程。");
    }
}
