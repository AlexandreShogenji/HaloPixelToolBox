using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HaloPixelToolBox.Services;

/// <summary>
/// Keeps DSH workers owned even when an intermediate Node launcher exits.
/// Only the toolbox-created launcher is assigned; external hosts are untouched.
/// </summary>
internal sealed class DshOwnedProcessJob : IDisposable
{
    private readonly JobHandle handle;

    private DshOwnedProcessJob(JobHandle handle) => this.handle = handle;

    public static DshOwnedProcessJob Attach(Process launcher)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 DSH 进程管理对象。");
        }
        try
        {
            var limits = new ExtendedLimits
            {
                Basic = new BasicLimits { LimitFlags = 0x2000 } // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                || !AssignProcessToJobObject(handle, launcher.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法持有 DSH 启动进程。");
            return new(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public Process CaptureMember(int pid)
    {
        var process = Process.GetProcessById(pid);
        try
        {
            // Retain the native process handle to avoid treating a reused PID
            // as the original worker when stopping or waiting later.
            if (!IsProcessInJob(process.SafeHandle, handle, out var member))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!member)
                throw new InvalidOperationException("DSH 桥接进程不属于本次工具箱启动。");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime;
        public long JobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet;
        public UIntPtr MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory;
        public UIntPtr JobMemory;
        public UIntPtr PeakProcessMemory;
        public UIntPtr PeakJobMemory;
    }

    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern JobHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int infoClass, ref ExtendedLimits info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, JobHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
