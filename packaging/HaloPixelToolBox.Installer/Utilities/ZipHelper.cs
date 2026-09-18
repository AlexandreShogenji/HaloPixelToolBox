using System.IO;
using System.IO.Compression;

namespace HaloPixelToolBox.Installer.Utilities
{
    public static class ZipHelper
    {
        public static void ExtraZipFile(string zipPath, string targetPath)
        {
            using var zipArchive = ZipFile.OpenRead(zipPath);
            ExtraZip(zipArchive, targetPath);
        }

        public static void ExtraZipStream(Stream stream, string targetPath)
        {
            using var zipArchive = new ZipArchive(stream);
            ExtraZip(zipArchive, targetPath);
        }

        public static void ExtraZip(ZipArchive zipArchive, string targetPath)
        {
            var targetDirectory = Path.GetFullPath(targetPath);
            Directory.CreateDirectory(targetDirectory);

            var targetDirectoryPrefix = targetDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (var entry in zipArchive.Entries)
            {
                var filePath = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));
                if (!filePath.StartsWith(targetDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"ZIP entry escapes the installation directory: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(filePath);
                    continue;
                }

                var parentDirectory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(parentDirectory))
                    Directory.CreateDirectory(parentDirectory);

                entry.ExtractToFile(filePath, true);
            }
        }
    }
}
