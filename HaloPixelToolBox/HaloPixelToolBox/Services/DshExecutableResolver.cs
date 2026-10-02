namespace HaloPixelToolBox.Services;

/// <summary>Resolve Windows command shims before starting them without a shell.</summary>
internal static class DshExecutableResolver
{
    public static string Resolve(string configured)
    {
        var command = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(command))
            throw new FileNotFoundException("请在设置中填写 DSH 启动程序（如 npx 或 dsh.cmd）。");

        if (File.Exists(command))
            return ResolveScriptShim(Path.GetFullPath(command));
        if (Path.IsPathRooted(command) || command.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new FileNotFoundException($"DSH 启动程序不存在：{command}");

        var name = Path.GetFileNameWithoutExtension(command);
        var candidates = Path.HasExtension(command)
            ? new[] { command }
            : new[] { name + ".cmd", name + ".exe", name + ".bat", command };
        foreach (var directory in CommandDirectories())
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path)) return ResolveScriptShim(path);
            }
        }
        throw new FileNotFoundException($"未找到 DSH 启动程序“{command}”。请在设置中填写 npx.cmd 或 dsh.cmd 的完整路径。");
    }

    private static string ResolveScriptShim(string path)
    {
        if (!Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase)) return path;
        var shim = Path.ChangeExtension(path, ".cmd");
        if (File.Exists(shim)) return shim;
        throw new FileNotFoundException("DSH 启动程序不能直接使用 PowerShell 脚本，请选择对应的 .cmd 或 .exe 文件。");
    }

    private static IEnumerable<string> CommandDirectories()
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(path => path.Trim().Trim('"'))
            .Concat([
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs")
            ])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
