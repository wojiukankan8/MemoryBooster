using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MemoryBooster.Models;

namespace MemoryBooster.Services;

public class MemoryService
{
    // Reused native buffer to avoid repeated large-object-heap allocations on each
    // process list refresh. Access is serialized by the UI thread's single-call pattern.
    private ProcessInfoNative[] _procBuffer = new ProcessInfoNative[1024];
    private ProcNetInfoNative[] _netBuffer = new ProcNetInfoNative[1024];

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    // --- Window enumeration (to classify foreground vs background processes) ---
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder buf, int max);
    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    private const uint GW_OWNER = 4;

    /// <summary>Returns {pid → main window title}. Only top-level, visible, titled,
    /// non-owned windows count; for a PID with multiple windows, keeps the longest title.</summary>
    private Dictionary<uint, string> EnumForegroundWindows()
    {
        var map = new Dictionary<uint, string>(64);
        IntPtr shell = GetShellWindow();
        var sb = new StringBuilder(256);
        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shell) return true;
            if (!IsWindowVisible(hWnd)) return true;
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return true;
            sb.Length = 0; if (sb.Capacity < len + 1) sb.Capacity = len + 1;
            if (GetWindowText(hWnd, sb, sb.Capacity) <= 0) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return true;
            string title = sb.ToString();
            if (!map.TryGetValue(pid, out string existing) || title.Length > existing.Length)
                map[pid] = title;
            return true;
        }, IntPtr.Zero);
        return map;
    }

    public (int cleaned, ulong freedBytes) CleanMemory()
    {
        var before = GetMemoryInfo();
        int cleaned = NativeInterop.CleanAllWorkingSets();
        Thread.Sleep(150);
        var after = GetMemoryInfo();

        ulong freed = 0;
        if (before.AvailablePhysical < after.AvailablePhysical)
            freed = after.AvailablePhysical - before.AvailablePhysical;

        return (cleaned, freed);
    }

    public SystemMemoryInfo GetMemoryInfo()
    {
        var info = new SystemMemoryInfo();
        NativeInterop.GetSystemMemoryInfo(ref info);
        return info;
    }

    public uint GetMemoryLoadPercent()
    {
        var info = GetMemoryInfo();
        return info.MemoryLoadPercent;
    }

    public List<ProcessInfo> GetProcessList()
    {
        int count = NativeInterop.GetProcessList(_procBuffer, _procBuffer.Length);
        var list = new List<ProcessInfo>(count);

        // Foreground map: PID → main window title
        Dictionary<uint, string> fgMap;
        try { fgMap = EnumForegroundWindows(); } catch { fgMap = new Dictionary<uint, string>(); }

        for (int i = 0; i < count; i++)
        {
            uint pid = _procBuffer[i].Pid;
            bool isFg = fgMap.TryGetValue(pid, out string title);
            list.Add(new ProcessInfo
            {
                Pid = pid,
                Name = _procBuffer[i].Name ?? "",
                FilePath = _procBuffer[i].FilePath ?? "",
                WorkingSet = _procBuffer[i].WorkingSetSize,
                PrivateBytes = _procBuffer[i].PrivateBytes,
                // CPU% is computed inside MemoryCore.dll (kernel+user ticks delta).
                CpuPercent = _procBuffer[i].CpuPercent,
                IsForeground = isFg,
                WindowTitle = title ?? ""
            });
        }

        list.Sort((a, b) => b.WorkingSet.CompareTo(a.WorkingSet));
        return list;
    }

    /// <summary>Total physical-NIC throughput via GetIfTable2 (64-bit counters,
    /// computed inside the DLL). Returns bytes/second and cumulative totals.</summary>
    public NetTotalInfo GetNetTotalStats()
    {
        var info = new NetTotalInfo();
        NativeInterop.GetNetTotalStats(ref info);
        return info;
    }

    /// <summary>Per-process TCP byte rates via ESTATS + connection counts via
    /// GetExtendedTcpTable / GetExtendedUdpTable. Requires admin.</summary>
    public List<ProcNetInfoNative> GetPerProcessNetStats()
    {
        int n = NativeInterop.GetPerProcessNetStats(_netBuffer, _netBuffer.Length);
        var res = new List<ProcNetInfoNative>(n);
        for (int i = 0; i < n; i++) res.Add(_netBuffer[i]);
        return res;
    }

    public void ResetNetStats() => NativeInterop.ResetNetStats();

    public bool KillProcess(uint pid)
    {
        try
        {
            if (NativeInterop.KillProcess(pid) != 0) return true;
        }
        catch { }
        return KillProcessForce(pid);
    }

    /// <summary>
    /// Stronger fallback: first try Process.Kill() then shell out to taskkill /F /T.
    /// Covers cases where the initial TerminateProcess handle was refused due to
    /// integrity-level / protected-process restrictions.
    /// </summary>
    public bool KillProcessForce(uint pid)
    {
        try
        {
            using (var p = Process.GetProcessById((int)pid))
            {
                p.Kill();
                if (p.WaitForExit(1000)) return true;
            }
        }
        catch { }

        try
        {
            var psi = new ProcessStartInfo("taskkill", $"/F /T /PID {pid}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var p = Process.Start(psi))
            {
                if (p != null)
                {
                    p.WaitForExit(3000);
                    return p.ExitCode == 0;
                }
            }
        }
        catch { }

        return false;
    }

    public bool CleanProcess(uint pid) => NativeInterop.CleanProcessWorkingSet(pid) != 0;
}
