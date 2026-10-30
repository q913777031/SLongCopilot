using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SLCopilot.Core;

/// <summary>
/// Windows 上把进程放入“关闭即结束”的作业对象：<see cref="Kill"/> 结束整个进程树，释放时结束仍在运行的子进程。
/// 加入作业失败时立即结束该进程并拒绝执行，不在无法结束子进程的情况下运行命令。其他平台（仅测试）结束进程树。
/// 命令执行（CommandRunner）与 MCP 服务器进程共用。
/// </summary>
internal sealed class ProcessTree : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private readonly Process process;
    private readonly JobHandle? job;

    private ProcessTree(Process process, JobHandle? job)
    {
        this.process = process;
        this.job = job;
    }

    public static ProcessTree Attach(Process process, string runId)
    {
        if (!Compat.IsWindows) return new(process, null);
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        if (job.IsInvalid || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimitInformation>()) ||
            !AssignProcessToJobObject(job, process.Handle))
        {
            var code = Marshal.GetLastWin32Error();
            try { process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            job.Dispose();
            RunLog.Warn(runId, "command_job_failed", ("error", code));
            throw new WorkspaceException("Could not place the process in a job object, so its child processes could not be stopped reliably. The process was stopped.");
        }
        return new(process, job);
    }

    public void Kill()
    {
        if (job is not null)
        {
            TerminateJobObject(job, 1);
            return;
        }
        try
        {
#if NETFRAMEWORK
            process.Kill();
#else
            process.Kill(entireProcessTree: true);
#endif
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose() => job?.Dispose();

    private sealed class JobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern JobHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(JobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
