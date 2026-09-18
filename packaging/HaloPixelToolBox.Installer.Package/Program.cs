using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;

return InstallerBootstrapper.Run();

internal static class InstallerBootstrapper
{
    private const string PayloadResourceName = "HaloPixelToolBox.Installer.Package.Source.zip";
    private const string InstallerFileName = "Installer.exe";
    private const int UacCancelledErrorCode = 1223;
    private const int CleanupAttemptCount = 5;

    public static int Run()
    {
        string? extractionDirectory = null;

        try
        {
            extractionDirectory = Path.Combine(
                Path.GetTempPath(),
                $"HaloPixelToolBox-Installer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractionDirectory);

            ExtractPayload(extractionDirectory);

            var installerPath = Path.Combine(extractionDirectory, InstallerFileName);
            if (!File.Exists(installerPath))
            {
                throw new FileNotFoundException(
                    $"安装包中未找到 {InstallerFileName}。",
                    installerPath);
            }

            using var installerProcess = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                WorkingDirectory = extractionDirectory,
                Verb = "runas",
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("无法启动安装程序。");

            installerProcess.WaitForExit();
            return installerProcess.ExitCode;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UacCancelledErrorCode)
        {
            Console.Error.WriteLine("安装已取消：未获得管理员权限。");
            return UacCancelledErrorCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"安装时发生错误：{exception.Message}");
            return 1;
        }
        finally
        {
            if (extractionDirectory is not null)
            {
                TryDeleteExtractionDirectory(extractionDirectory);
            }
        }
    }

    private static void ExtractPayload(string extractionDirectory)
    {
        using var payloadStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidDataException($"未找到嵌入的资源文件：{PayloadResourceName}");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var zipArchive = new ZipArchive(
            payloadStream,
            ZipArchiveMode.Read,
            leaveOpen: false,
            Encoding.GetEncoding("GB2312"));

        var extractionRoot = Path.GetFullPath(extractionDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        foreach (var entry in zipArchive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(extractionRoot, entry.FullName));
            if (!destinationPath.StartsWith(extractionRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"安装包包含不安全的路径：{entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var destinationParent = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidDataException($"安装包路径无效：{entry.FullName}");
            Directory.CreateDirectory(destinationParent);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static void TryDeleteExtractionDirectory(string extractionDirectory)
    {
        for (var attempt = 1; attempt <= CleanupAttemptCount; attempt++)
        {
            try
            {
                if (Directory.Exists(extractionDirectory))
                {
                    Directory.Delete(extractionDirectory, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                attempt < CleanupAttemptCount
                && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(200 * attempt));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"无法清理临时安装目录 {extractionDirectory}：{exception.Message}");
                return;
            }
        }
    }
}
