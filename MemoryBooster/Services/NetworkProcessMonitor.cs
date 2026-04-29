using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MemoryBooster.Services;

/// <summary>
/// UI-side view-model for one network-active process.
/// The raw numbers (byte rates + connection counts) come straight from
/// MemoryCore.dll's <c>GetPerProcessNetStats</c> (ESTATS + GetExtendedTcpTable).
/// This class just formats them and exposes WPF-bindable properties with
/// <see cref="INotifyPropertyChanged"/> so the grid can refresh in-place
/// without re-creating rows every tick (which would kill the scroll position
/// and flash the selection).
/// </summary>
public class NetProcItem : INotifyPropertyChanged
{
    public uint   Pid  { get; set; }
    public string Name { get; set; } = "";

    private double _upSpeed;   // bytes/s (upload)
    private double _downSpeed; // bytes/s (download)
    private int _tcpConn;
    private int _udpConn;

    public double UpSpeed
    {
        get => _upSpeed;
        set
        {
            if (_upSpeed == value) return;
            _upSpeed = value;
            Raise(nameof(UpSpeed));
            Raise(nameof(UpDisplay));
            Raise(nameof(TotalSpeed));
            Raise(nameof(Status));
            Raise(nameof(StatusColor));
        }
    }

    public double DownSpeed
    {
        get => _downSpeed;
        set
        {
            if (_downSpeed == value) return;
            _downSpeed = value;
            Raise(nameof(DownSpeed));
            Raise(nameof(DownDisplay));
            Raise(nameof(TotalSpeed));
            Raise(nameof(Status));
            Raise(nameof(StatusColor));
        }
    }

    public int TcpConnections
    {
        get => _tcpConn;
        set { if (_tcpConn == value) return; _tcpConn = value; Raise(nameof(TcpConnections)); Raise(nameof(ConnDisplay)); }
    }

    public int UdpConnections
    {
        get => _udpConn;
        set { if (_udpConn == value) return; _udpConn = value; Raise(nameof(UdpConnections)); Raise(nameof(ConnDisplay)); }
    }

    public double TotalSpeed => _upSpeed + _downSpeed;

    public string UpDisplay   => FormatSpeed(_upSpeed);
    public string DownDisplay => FormatSpeed(_downSpeed);

    public string ConnDisplay => (_tcpConn + _udpConn) == 0
        ? ""
        : $"TCP {_tcpConn}  UDP {_udpConn}";

    /// <summary>Bucket used by the UI to colour-code traffic intensity.</summary>
    public string Status
    {
        get
        {
            double total = _upSpeed + _downSpeed;
            if (total >= 1024 * 1024)      return "\u9AD8";  // 高 ≥ 1 MB/s
            if (total >= 100 * 1024)       return "\u4E2D";  // 中 ≥ 100 KB/s
            if (total > 0)                 return "\u4F4E";  // 低 > 0
            return "\u2014";                                  // —
        }
    }

    /// <summary>Colour hex string matching <see cref="Status"/>.</summary>
    public string StatusColor
    {
        get
        {
            double total = _upSpeed + _downSpeed;
            if (total >= 1024 * 1024) return "#E04848";  // red
            if (total >= 100 * 1024)  return "#E0A020";  // amber
            if (total > 0)            return "#23B574";  // green
            return "#BBBBBB";                             // grey
        }
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1) return "0 B/s";
        if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:F1} KB/s";
        return $"{bytesPerSec / (1024 * 1024):F2} MB/s";
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void Raise([CallerMemberName] string n = null)
    {
        var h = PropertyChanged;
        if (h != null) h(this, new PropertyChangedEventArgs(n));
    }
}
