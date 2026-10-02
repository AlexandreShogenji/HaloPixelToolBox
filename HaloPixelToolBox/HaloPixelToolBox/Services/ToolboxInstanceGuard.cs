using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace HaloPixelToolBox.Services;

/// <summary>
/// Prevents unpackaged copies launched from different directories from running in
/// the same Windows user session. AppInstance continues to handle the normal
/// same-install activation path before this guard is acquired.
/// </summary>
internal sealed class ToolboxInstanceGuard : IDisposable
{
    private const int ShowRestore = 9;
    private const int ShowCurrent = 5;
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconInformation = 0x00000040;
    private readonly Mutex mutex;
    private bool ownsMutex;
    private bool disposed;

    private ToolboxInstanceGuard(Mutex mutex, bool ownsMutex, ExistingToolboxInstance? existingInstance)
    {
        this.mutex = mutex;
        this.ownsMutex = ownsMutex;
        ExistingInstance = existingInstance;
    }

    public bool IsPrimary => ownsMutex && ExistingInstance is null;

    public ExistingToolboxInstance? ExistingInstance { get; }

    public static ToolboxInstanceGuard Acquire()
    {
        using var current = Process.GetCurrentProcess();
        var mutex = new Mutex(false, BuildMutexName(current.SessionId));
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner terminated without releasing the mutex. WaitOne
            // grants ownership to this thread, so this process may safely recover.
            acquired = true;
        }

        var existing = FindExistingInstance(current);
        if (existing is not null && acquired)
        {
            mutex.ReleaseMutex();
            acquired = false;
        }

        return new ToolboxInstanceGuard(mutex, acquired, existing);
    }

    public void ActivateOrExplainDuplicate()
    {
        if (IsPrimary)
            return;

        if (ExistingInstance is { } existing && TryActivateWindow(existing))
            return;

        _ = MessageBox(
            IntPtr.Zero,
            "检测到 HaloPixelToolBox 已在当前 Windows 会话中运行，但无法自动显示它。请从任务栏或托盘打开现有窗口；当前副本将退出。",
            "HaloPixelToolBox 已在运行",
            MessageBoxOk | MessageBoxIconInformation);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (ownsMutex)
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException)
            {
                // ProcessExit may run on a different thread from the UI thread
                // that acquired the mutex. Closing the handle during process exit
                // still releases the process-owned kernel resource.
            }
            ownsMutex = false;
        }
        mutex.Dispose();
    }

    private static string BuildMutexName(int sessionId)
    {
        string identity;
        try { identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch { identity = Environment.UserName; }
        var identityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return $@"Local\HaloPixelToolBox.Singleton.v2.{sessionId}.{identityHash}";
    }

    private static ExistingToolboxInstance? FindExistingInstance(Process current)
    {
        ExistingToolboxInstance? oldest = null;
        foreach (var candidate in Process.GetProcessesByName(current.ProcessName))
        {
            using (candidate)
            {
                try
                {
                    if (candidate.Id == current.Id || candidate.HasExited || candidate.SessionId != current.SessionId)
                        continue;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // A process can exit between enumeration and inspection.
                    continue;
                }

                var startedAt = DateTimeOffset.MaxValue;
                try { startedAt = new DateTimeOffset(candidate.StartTime); }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException) { }
                var executablePath = string.Empty;
                try { executablePath = candidate.MainModule?.FileName ?? string.Empty; }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException) { }
                var window = IntPtr.Zero;
                try { window = candidate.MainWindowHandle; }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException) { }
                var instance = new ExistingToolboxInstance(candidate.Id, executablePath, startedAt, window);
                if (oldest is null || instance.StartedAt < oldest.StartedAt)
                    oldest = instance;
            }
        }
        return oldest;
    }

    private static bool TryActivateWindow(ExistingToolboxInstance existing)
    {
        var window = existing.MainWindowHandle;
        if (window == IntPtr.Zero)
        {
            _ = EnumWindows((candidate, state) =>
            {
                GetWindowThreadProcessId(candidate, out var processId);
                if (processId != existing.ProcessId)
                    return true;
                window = candidate;
                return false;
            }, IntPtr.Zero);
        }
        if (window == IntPtr.Zero)
            return false;

        _ = ShowWindowAsync(window, IsIconic(window) ? ShowRestore : ShowCurrent);
        _ = SetForegroundWindow(window);
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);
}

internal sealed record ExistingToolboxInstance(
    int ProcessId,
    string ExecutablePath,
    DateTimeOffset StartedAt,
    IntPtr MainWindowHandle);
