using System;

namespace MemoryBooster.Services;

/// <summary>
/// System-wide upload/download speed sourced from MemoryCore.dll's
/// <c>GetNetTotalStats</c>, which walks <c>GetIfTable2</c> and only aggregates
/// real physical NICs (<c>IF_TYPE_ETHERNET_CSMACD</c> / <c>IF_TYPE_IEEE80211</c>).
/// That matches what Task Manager shows and avoids the double-counting we used
/// to get when WSL / Hyper-V / VMware virtual switches mirrored the physical
/// adapter's byte counters.
/// </summary>
public class NetworkMonitor
{
    public double UploadSpeed   { get; private set; }  // bytes/s
    public double DownloadSpeed { get; private set; }  // bytes/s
    public ulong  TotalSent     { get; private set; }
    public ulong  TotalRecv     { get; private set; }

    public void Update()
    {
        var info = new NetTotalInfo();
        try
        {
            if (NativeInterop.GetNetTotalStats(ref info) == 0) return;
        }
        catch { return; }
        UploadSpeed   = info.BytesOutPerSec;
        DownloadSpeed = info.BytesInPerSec;
        TotalSent     = info.TotalBytesOut;
        TotalRecv     = info.TotalBytesIn;
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1) return "0 B/s";
        if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:F1} KB/s";
        return $"{bytesPerSec / (1024 * 1024):F2} MB/s";
    }
}
