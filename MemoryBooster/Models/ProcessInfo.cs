using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MemoryBooster.Models;

public class ProcessInfo : INotifyPropertyChanged
{
    public uint Pid { get; set; }
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";

    private ulong _workingSet;
    public ulong WorkingSet
    {
        get => _workingSet;
        set { _workingSet = value; OnPropertyChanged(); OnPropertyChanged(nameof(WorkingSetDisplay)); }
    }

    private ulong _privateBytes;
    public ulong PrivateBytes
    {
        get => _privateBytes;
        set { _privateBytes = value; OnPropertyChanged(); }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    private double _cpuPercent;
    public double CpuPercent
    {
        get => _cpuPercent;
        set
        {
            if (_cpuPercent == value) return;
            _cpuPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CpuDisplay));
        }
    }

    public string CpuDisplay => _cpuPercent < 0.05 ? "—" : $"{_cpuPercent:F1}%";

    public string WorkingSetDisplay => FormatBytes(WorkingSet);

    // Foreground/background classification (has visible top-level window)
    public bool IsForeground { get; set; }
    public string WindowTitle { get; set; } = "";
    /// <summary>Task-Manager style display name: window title for foreground,
    /// executable name otherwise.</summary>
    public string DisplayName => IsForeground && !string.IsNullOrWhiteSpace(WindowTitle)
        ? WindowTitle
        : Name;

    /// <summary>Group key used by CollectionViewSource in the process grid to
    /// render a divider-like section header between foreground and background.</summary>
    public string GroupName => IsForeground
        ? "\u524D\u53F0\u8FDB\u7A0B"   // 前台进程
        : "\u540E\u53F0\u8FDB\u7A0B"; // 后台进程

    /// <summary>True only for the row that marks the boundary between
    /// foreground and background sections. Used by DataGridRow style trigger
    /// to draw a visible divider line — replaces the old GroupStyle approach
    /// which caused virtualization-scroll crashes under WPF.</summary>
    public bool IsSectionBreak { get; set; }

    // Visibility for the “file path” sub-line inside the name cell. The panel
    // mirrors the “显示路径” checkbox into this property on every row, so the
    // DataTemplate can bind directly to DataContext instead of walking the
    // visual tree with RelativeSource AncestorType=Window — the latter is
    // unstable under DataGrid virtualization and triggered a NullReferenceException
    // inside PropertyMetadata.get_DefaultValue during container recycling.
    private Visibility _pathVisibility = Visibility.Collapsed;
    public Visibility PathVisibility
    {
        get => _pathVisibility;
        set { if (_pathVisibility != value) { _pathVisibility = value; OnPropertyChanged(); } }
    }

    public static string FormatBytes(ulong bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
        var h = PropertyChanged;
        if (h != null) h(this, new PropertyChangedEventArgs(name));
    }
}
