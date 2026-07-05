using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Shopee.Core.Platform.Windows;

/// <summary>
/// Job Object cấp ỨNG DỤNG cho mọi tiến trình phóng qua đây. Đặt cờ KILL_ON_JOB_CLOSE: khi tiến trình app
/// kết thúc — BÌNH THƯỜNG, CRASH, hay bị force-kill (Task Manager / "End task") — handle job đóng theo, OS
/// TỰ GIẾT toàn bộ Brave (và con cháu) trong job → KHÔNG còn Brave mồ côi ăn RAM (nguyên nhân "máy đơ" sau
/// nhiều lượt test). Chỉ giết con của CHÍNH app này nên an toàn khi chạy nhiều instance ShopeeSuite cùng lúc.
///
/// Brave được phóng CREATE_SUSPENDED → GÁN job → ResumeThread, đảm bảo không tiến trình con nào sinh ra
/// TRƯỚC khi vào job (nếu gán sau Process.Start, browser thật mà stub fork ra có thể đã thoát khỏi job).
/// Win8+ hỗ trợ nested job nên sandbox Chromium (vốn tạo job riêng) vẫn chạy bình thường.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessLifetimeScope : IProcessLifetimeScope
{
    private readonly object _lock = new();
    private IntPtr _job = IntPtr.Zero;   // KHÔNG đóng suốt vòng đời app — đóng = giết Brave ngay
    private bool _failed;

    // Trần CỨNG do OS ép (kể cả khi app treo/chết). 0 = không giới hạn. Đặt TRƯỚC lần phóng Brave đầu.
    private int _activeProcessLimit;
    private ulong _jobMemoryLimit;

    public void ConfigureLimits(int activeProcessLimit, ulong jobMemoryLimitBytes)
    {
        _activeProcessLimit = Math.Max(0, activeProcessLimit);
        _jobMemoryLimit = jobMemoryLimitBytes;
    }

    public Process Start(string fileName, string arguments)
    {
        if (EnsureJob())
        {
            var p = TryStartInJob(fileName, arguments);
            if (p is not null) return p;
        }
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
        })!;
        try { if (_job != IntPtr.Zero) AssignProcessToJobObject(_job, proc.Handle); } catch { }
        return proc;
    }

    private bool EnsureJob()
    {
        if (_job != IntPtr.Zero) return true;
        if (_failed) return false;
        lock (_lock)
        {
            if (_job != IntPtr.Zero) return true;
            if (_failed) return false;

            var h = CreateJobObject(IntPtr.Zero, null);
            if (h == IntPtr.Zero) { _failed = true; return false; }

            var flags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (_activeProcessLimit > 0) flags |= JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
            if (_jobMemoryLimit > 0) flags |= JOB_OBJECT_LIMIT_JOB_MEMORY;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = flags,
                    ActiveProcessLimit = (uint)_activeProcessLimit,
                },
                JobMemoryLimit = _jobMemoryLimit > 0 ? new UIntPtr(_jobMemoryLimit) : UIntPtr.Zero,
            };
            var len = Marshal.SizeOf(info);
            var ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(h, JobObjectExtendedLimitInformation, ptr, (uint)len))
                {
                    CloseHandle(h);
                    _failed = true;
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }

            _job = h;
            return true;
        }
    }

    private Process? TryStartInJob(string fileName, string arguments)
    {
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(si);

        var cmd = new StringBuilder();
        cmd.Append('"').Append(fileName).Append('"');
        if (!string.IsNullOrEmpty(arguments)) cmd.Append(' ').Append(arguments);

        var ok = CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
            CREATE_SUSPENDED, IntPtr.Zero, null, ref si, out var pi);
        if (!ok) return null;

        try
        {
            AssignProcessToJobObject(_job, pi.hProcess);   // GÁN trước resume → con cháu cũng vào job
            ResumeThread(pi.hThread);
            return Process.GetProcessById((int)pi.dwProcessId);
        }
        catch
        {
            try { TerminateProcess(pi.hProcess, 1); } catch { }
            return null;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
        }
    }

    // ── Hằng số ──
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;

    // ── P/Invoke ──
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
