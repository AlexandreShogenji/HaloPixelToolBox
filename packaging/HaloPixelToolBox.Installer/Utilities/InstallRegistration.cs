using Microsoft.Win32;
using System.IO;
using System.Reflection;

namespace HaloPixelToolBox.Installer.Utilities;

internal static class InstallRegistration
{
    private const string ProductName = "HaloPixelToolBox";
    private const string RegistrySubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HaloPixelToolBox";
    private const string ProjectUrl = "https://github.com/AlexandreShogenji/HaloPixelToolBox";

    public static void Register(string installPath)
    {
        var installDirectory = Path.GetFullPath(installPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var applicationPath = Path.Combine(installDirectory, "HaloPixelToolBox.exe");
        var uninstallerPath = Path.Combine(installDirectory, "Uninstaller", "Uninstall.exe");

        if (!File.Exists(applicationPath))
            throw new FileNotFoundException("安装后没有找到 HaloPixelToolBox.exe。", applicationPath);
        if (!File.Exists(uninstallerPath))
            throw new FileNotFoundException("安装包中没有找到卸载程序。", uninstallerPath);

        CreateShortcuts(installDirectory, applicationPath, uninstallerPath);
        RegisterUninstaller(installDirectory, applicationPath, uninstallerPath);
    }

    private static void CreateShortcuts(
        string installDirectory,
        string applicationPath,
        string uninstallerPath)
    {
        var desktopShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "花再工具箱.lnk");
        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            ProductName);

        CreateShortcuts(
            installDirectory,
            applicationPath,
            uninstallerPath,
            desktopShortcut,
            startMenuDirectory);
    }

    private static void CreateShortcuts(
        string installDirectory,
        string applicationPath,
        string uninstallerPath,
        string desktopShortcut,
        string startMenuDirectory)
    {
        if (!FileHelper.CreateShortCut(
                desktopShortcut,
                applicationPath,
                description: "打开 HaloPixelToolBox",
                iconLocation: applicationPath,
                workingDirectory: installDirectory))
        {
            throw new IOException("无法创建桌面快捷方式。");
        }

        Directory.CreateDirectory(startMenuDirectory);

        if (!FileHelper.CreateShortCut(
                Path.Combine(startMenuDirectory, "HaloPixelToolBox.lnk"),
                applicationPath,
                description: "打开 HaloPixelToolBox",
                iconLocation: applicationPath,
                workingDirectory: installDirectory)
            || !FileHelper.CreateShortCut(
                Path.Combine(startMenuDirectory, "卸载 HaloPixelToolBox.lnk"),
                uninstallerPath,
                description: "卸载 HaloPixelToolBox",
                iconLocation: uninstallerPath,
                workingDirectory: Path.GetDirectoryName(uninstallerPath)))
        {
            throw new IOException("无法创建开始菜单快捷方式。");
        }
    }

    private static void RegisterUninstaller(
        string installDirectory,
        string applicationPath,
        string uninstallerPath)
    {
        using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var uninstallKey = localMachine.CreateSubKey(RegistrySubKey, writable: true)
            ?? throw new InvalidOperationException("无法创建 Windows 卸载信息。");

        var assembly = typeof(InstallRegistration).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var displayVersion = informationalVersion?.Split('+', 2)[0]
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";

        WriteUninstallValues(
            uninstallKey,
            installDirectory,
            applicationPath,
            uninstallerPath,
            displayVersion);
    }

    private static void WriteUninstallValues(
        RegistryKey uninstallKey,
        string installDirectory,
        string applicationPath,
        string uninstallerPath,
        string displayVersion)
    {
        var sizeInKilobytes = Math.Ceiling(
            FileHelper.GetDirectorySize(new DirectoryInfo(installDirectory)) / 1024d);
        var estimatedSize = (int)Math.Min(int.MaxValue, sizeInKilobytes);

        uninstallKey.SetValue("DisplayName", ProductName, RegistryValueKind.String);
        uninstallKey.SetValue("DisplayVersion", displayVersion, RegistryValueKind.String);
        uninstallKey.SetValue("Publisher", "AlexandreShogenji", RegistryValueKind.String);
        uninstallKey.SetValue("InstallLocation", installDirectory, RegistryValueKind.String);
        uninstallKey.SetValue("DisplayIcon", $"{applicationPath},0", RegistryValueKind.String);
        uninstallKey.SetValue("UninstallString", $"\"{uninstallerPath}\"", RegistryValueKind.String);
        uninstallKey.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" /quiet", RegistryValueKind.String);
        uninstallKey.SetValue("URLInfoAbout", ProjectUrl, RegistryValueKind.String);
        uninstallKey.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
        uninstallKey.SetValue("EstimatedSize", estimatedSize, RegistryValueKind.DWord);
        uninstallKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
        uninstallKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }
}
