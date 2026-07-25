using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hearth.Core;

/// <summary>
/// Measures what Hearth actually costs, by summing the working set of its own
/// WebView2 process tree.
///
/// This exists because there is no per-tab memory API to ask
/// (k.no-per-tab-memory-api). The workaround is the one used throughout this
/// project's benchmarks: enumerate the descendants of our own process, sum their
/// working sets, and treat differences across an eviction as the reclaim.
///
/// Descendants are walked rather than filtering every msedgewebview2.exe on the
/// machine: Windows runs plenty of other WebView2 apps (Widgets, Store, Teams),
/// and counting theirs would inflate our own number.
/// </summary>
public static class MemoryProbe
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public sealed record Reading(long TotalBytes, int ProcessCount, int RendererCount);

    /// <summary>
    /// Total working set of this process and every descendant. Cheap enough to
    /// call on a couple-of-seconds cadence; a Toolhelp snapshot is one syscall
    /// rather than the per-process WMI query the benchmark scripts use.
    /// </summary>
    public static Reading Sample()
    {
        var self = (uint)Environment.ProcessId;
        var children = new Dictionary<uint, List<uint>>();
        var names = new Dictionary<uint, string>();

        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return new Reading(0, 0, 0);

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return new Reading(0, 0, 0);

            do
            {
                if (!children.TryGetValue(entry.th32ParentProcessID, out var list))
                    children[entry.th32ParentProcessID] = list = [];
                list.Add(entry.th32ProcessID);
                names[entry.th32ProcessID] = entry.szExeFile;
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        long total = 0;
        var count = 0;
        var renderers = 0;

        // Breadth-first over descendants. A visited set guards against the
        // cycles that PID reuse can produce in a stale snapshot.
        var queue = new Queue<uint>();
        var seen = new HashSet<uint> { self };
        queue.Enqueue(self);

        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                total += proc.WorkingSet64;
                count++;

                // Renderer count is not directly observable without command
                // lines; WebView2 children that are not the browser process are
                // a serviceable proxy for "content processes".
                if (pid != self && names.TryGetValue(pid, out var name)
                    && name.StartsWith("msedgewebview2", StringComparison.OrdinalIgnoreCase))
                    renderers++;
            }
            catch
            {
                // Process exited between snapshot and query. Normal.
            }

            if (!children.TryGetValue(pid, out var kids)) continue;
            foreach (var kid in kids)
                if (seen.Add(kid)) queue.Enqueue(kid);
        }

        return new Reading(total, count, renderers);
    }

    /// <summary>Formats bytes the way a person reads them, not a profiler.</summary>
    public static string Format(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.0} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B"
    };
}
