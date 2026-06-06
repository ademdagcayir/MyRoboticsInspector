using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Spawn edilen child process'leri (ffmpeg) bir Windows Job Object'e bağlar.
/// Job, KILL_ON_JOB_CLOSE bayrağıyla kurulduğu için uygulama process'i sona
/// erdiğinde (normal kapanış VEYA çökme) işletim sistemi job'a bağlı tüm
/// process'leri otomatik öldürür.
///
/// Neden gerekli: ffmpeg öksüz kalırsa kameranın RTSP bağlantı havuzundan
/// (Hikvision sub-stream ~6 eşzamanlı) bir slot tutmaya devam eder. Birkaç
/// öksüz process sonrası kamera yeni bağlantıyı reddeder ve "canlı görüntü
/// gelmiyor" yaşanır. Bu reaper sızıntıyı tamamen önler.
///
/// Windows dışı platformlarda no-op'tur.
/// </summary>
public static class ChildProcessReaper
{
    private static readonly object _lock = new();
    private static IntPtr _job = IntPtr.Zero;
    private static bool _initialized;

    /// <summary>Çalışan bir process'i ortak "öldür-birlikte" job'una ekler.</summary>
    public static void Register(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            EnsureJob();
            if (_job != IntPtr.Zero && !process.HasExited)
                AssignProcessToJobObject(_job, process.Handle);
        }
        catch { /* best-effort — başarısız olursa eski davranışa düşer */ }
    }

    private static void EnsureJob()
    {
        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;

            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) return;

            var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int len = Marshal.SizeOf(ext);
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(ext, ptr, false);
                SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ptr, (uint)len);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

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
}
