using System.Runtime.InteropServices;

namespace MemoryBooster.Services;

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
public struct ProcessInfoNative
{
    public uint Pid;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string Name;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)]
    public string FilePath;
    public ulong WorkingSetSize;
    public ulong PrivateBytes;
    public double CpuPercent;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SystemMemoryInfo
{
    public ulong TotalPhysical;
    public ulong AvailablePhysical;
    public uint MemoryLoadPercent;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NetworkSnapshot
{
    public ulong BytesSent;
    public ulong BytesRecv;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ProcNetInfoNative
{
    public uint   Pid;
    public ulong  BytesIn;
    public ulong  BytesOut;
    public double BytesInPerSec;
    public double BytesOutPerSec;
    public uint   TcpConnections;
    public uint   UdpConnections;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NetTotalInfo
{
    public ulong  TotalBytesIn;
    public ulong  TotalBytesOut;
    public double BytesInPerSec;
    public double BytesOutPerSec;
}

public static class NativeInterop
{
    private const string DllName = "MemoryCore.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CleanAllWorkingSets();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CleanProcessWorkingSet(uint pid);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetProcessList(
        [Out] ProcessInfoNative[] buffer, int bufferCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int KillProcess(uint pid);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetSystemMemoryInfo(ref SystemMemoryInfo info);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetNetworkSnapshot(ref NetworkSnapshot snapshot);

    // Per-process TCP/UDP byte-level rates, computed inside the DLL via ESTATS.
    // Requires the host process to be running as administrator.
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetPerProcessNetStats(
        [Out] ProcNetInfoNative[] buffer, int bufferCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ResetNetStats();

    // Aggregate physical-NIC throughput (GetIfTable2, 64-bit counters).
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetNetTotalStats(ref NetTotalInfo info);

    // Direct Win32 import for trimming the current process' own working set.
    [DllImport("kernel32.dll")]
    private static extern System.IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(
        System.IntPtr hProcess, System.IntPtr dwMin, System.IntPtr dwMax);

    /// <summary>Force-trims this process' working set, releasing pages back to the OS.</summary>
    public static void TrimSelfWorkingSet()
    {
        try
        {
            SetProcessWorkingSetSize(GetCurrentProcess(),
                new System.IntPtr(-1), new System.IntPtr(-1));
        }
        catch { }
    }
}
