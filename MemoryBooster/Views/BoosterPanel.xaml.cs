using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MemoryBooster.Models;
using MemoryBooster.Services;

namespace MemoryBooster.Views;

public partial class BoosterPanel : Window
{
    private readonly MemoryService _memService;
    private readonly NetworkMonitor _netMonitor;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;

    private ProcessInfo _selectedProcess;
    private bool _loaded;

    // Full process snapshot and the single collection bound directly to the
    // grid. Foreground rows come first and the first background row has
    // IsSectionBreak=true so the RowStyle DataTrigger draws a divider line
    // above it. We used to use CollectionViewSource.GroupDescriptions, but
    // that combo (grouping + virtualization) crashed WPF on scroll.
    private List<ProcessInfo> _allProcs = new List<ProcessInfo>();
    private readonly ObservableCollection<ProcessInfo> _viewProcs = new ObservableCollection<ProcessInfo>();

    // Network-process list. Keyed by PID so we can update in-place on each tick
    // (rather than clearing the collection — which would lose scroll / flash rows).
    private readonly Dictionary<uint, NetProcItem> _netByPid = new Dictionary<uint, NetProcItem>();
    private readonly ObservableCollection<NetProcItem> _netProcs
        = new ObservableCollection<NetProcItem>();
    private int _netSortMode; // 0=total, 1=upload, 2=download, 3=conn, 4=name

    public event Action SettingsChanged;

    public BoosterPanel(MemoryService memService, NetworkMonitor netMonitor, AppSettings settings)
    {
        _memService = memService;
        _netMonitor = netMonitor;
        _settings = settings;
        InitializeComponent();

        ProcessGrid.ItemsSource = _viewProcs;
        NetProcGrid.ItemsSource = _netProcs;

        _loaded = true;

        // Sync network-monitor checkbox + overlay from persisted setting.
        if (ChkEnableNet != null)
            ChkEnableNet.IsChecked = _settings.EnableNetMonitor;
        if (ChkShowFg != null)
            ChkShowFg.IsChecked = _settings.ShowForegroundProcs;
        ApplyNetEnabledUI();

        LoadSettings();
        ApplySkinToHeader();
        RefreshProcessList();
        UpdateMemoryBar();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (s, ev) => { try { OnTimerTick(); } catch { } };
        _refreshTimer.Start();

        Closed += OnPanelClosed;

        // Deselect the highlighted process only when the user clicks somewhere
        // that is NOT inside the process grid (e.g. the header or settings
        // area). Clicks within the list keep the highlight stable across
        // auto-refresh ticks so users can study a row for as long as they like.
        PreviewMouseLeftButtonDown += OnPanelMouseDown;

        // Right-clicking a different row must NOT steal the red highlight from
        // the row the user previously left-clicked — the menu always operates
        // on the currently selected process. Restore SelectedItem whenever the
        // context menu is about to open.
        ProcessGrid.ContextMenuOpening += OnGridContextMenuOpening;
    }

    private void OnGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_selectedProcess == null) return;
        if (!_viewProcs.Contains(_selectedProcess)) return;
        if (ProcessGrid.SelectedItem != _selectedProcess)
            ProcessGrid.SelectedItem = _selectedProcess;
    }

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectedProcess == null || ProcessGrid == null) return;
        var src = e.OriginalSource as DependencyObject;
        while (src != null)
        {
            if (src == ProcessGrid) return; // still inside the list, keep selection
            src = (src is System.Windows.Media.Visual || src is System.Windows.Media.Media3D.Visual3D)
                ? System.Windows.Media.VisualTreeHelper.GetParent(src)
                : LogicalTreeHelper.GetParent(src);
        }
        ProcessGrid.UnselectAll();
        _selectedProcess = null;
        UpdateSelCount();
    }

    private void OnPanelClosed(object sender, EventArgs e)
    {
        // Release every cache so the panel’s working set is freed fast when
        // the user closes it — helps keep MemoryBooster’s own footprint small.
        _refreshTimer.Stop();
        foreach (var p in _allProcs) p.PropertyChanged -= OnProcItemChanged;
        _allProcs = new List<ProcessInfo>();
        _viewProcs.Clear();
        _netProcs.Clear();
        _netByPid.Clear();
        // Hint the CLR to compact; panels are opened rarely so this is fine.
        GC.Collect(1, GCCollectionMode.Optimized);
    }

    private void OnTimerTick()
    {
        UpdateMemoryBar();
        // Only poll the network tab when the user has explicitly enabled
        // monitoring — saves both CPU and the memory that per-PID net stats
        // would otherwise hold onto in the native DLL.
        if (NetworkPanel.Visibility == Visibility.Visible)
        {
            if (_settings.EnableNetMonitor)
                UpdateNetworkDisplay();
        }
        else if (ProcessPanel.Visibility == Visibility.Visible)
            RefreshProcessList();
    }

    // ── Process Tab ──
    // PID to re-anchor the scroll position on after a refresh, so foreground
    // rows appearing / disappearing above the user’s viewport don’t yank the
    // list around.
    private uint _scrollAnchorPid;

    private void RefreshProcessList()
    {
        // Remember what the user was looking at — the row they’re currently
        // viewing and the row they’ve clicked (highlighted in red).
        CaptureScrollAnchor();
        uint selectedPid = _selectedProcess != null ? _selectedProcess.Pid : 0u;

        var list = _memService.GetProcessList();

        // Preserve checkbox state across refreshes by PID.
        var selectedPids = new HashSet<uint>();
        foreach (var p in _allProcs)
        {
            p.PropertyChanged -= OnProcItemChanged;
            if (p.IsSelected) selectedPids.Add(p.Pid);
        }

        _allProcs = list;
        // Seed PathVisibility on the fresh snapshot so new rows respect the
        // current “显示路径” checkbox state without waiting for the user to
        // toggle it again.
        var pv = ChkShowPath != null && ChkShowPath.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in _allProcs)
        {
            p.PathVisibility = pv;
            if (selectedPids.Contains(p.Pid)) p.IsSelected = true;
            p.PropertyChanged += OnProcItemChanged;
        }

        ApplyFilter();

        // Restore the highlighted row: find the fresh ProcessInfo by PID and
        // re-select it in the grid. Without this the user’s click target is
        // deselected every 2s when the refresh timer fires.
        if (selectedPid != 0)
        {
            ProcessInfo restored = null;
            for (int i = 0; i < _viewProcs.Count; i++)
            {
                if (_viewProcs[i].Pid == selectedPid) { restored = _viewProcs[i]; break; }
            }
            if (restored != null)
            {
                _selectedProcess = restored;
                ProcessGrid.SelectedItem = restored;
            }
            else
            {
                _selectedProcess = null;
            }
        }

        RestoreScrollAnchor();

        ulong totalWs = 0;
        for (int i = 0; i < _allProcs.Count; i++) totalWs += _allProcs[i].WorkingSet;
        if (TxtTotalMem != null)
            TxtTotalMem.Text = ProcessInfo.FormatBytes(totalWs);

        UpdateSelCount();
    }

    // Record the PID of the row at the top of the visible viewport. After the
    // refresh replaces _viewProcs we scroll that PID back into view, so users
    // skimming background rows don’t get jerked up when a new foreground app
    // appears at the top of the list.
    private void CaptureScrollAnchor()
    {
        _scrollAnchorPid = 0;
        if (_viewProcs.Count == 0) return;
        var sv = FindScrollViewer(ProcessGrid);
        if (sv == null) return;
        // ScrollUnit=Pixel — VerticalOffset is in pixels; row height is 32.
        int idx = (int)(sv.VerticalOffset / 32.0);
        if (idx < 0) idx = 0;
        if (idx >= _viewProcs.Count) idx = _viewProcs.Count - 1;
        _scrollAnchorPid = _viewProcs[idx].Pid;
    }

    private void RestoreScrollAnchor()
    {
        if (_scrollAnchorPid == 0) return;
        for (int i = 0; i < _viewProcs.Count; i++)
        {
            if (_viewProcs[i].Pid == _scrollAnchorPid)
            {
                try { ProcessGrid.ScrollIntoView(_viewProcs[i]); } catch { }
                break;
            }
        }
    }

    private static ScrollViewer FindScrollViewer(DependencyObject root)
    {
        if (root == null) return null;
        if (root is ScrollViewer sv) return sv;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }

    private void OnProcItemChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessInfo.IsSelected))
            UpdateSelCount();
    }

    // 0 = by working-set desc, 1 = by CPU desc, 2 = by name asc.
    private int _procSortMode;

    private IEnumerable<ProcessInfo> SortProcs(IEnumerable<ProcessInfo> src)
    {
        switch (_procSortMode)
        {
            case 1: return src.OrderByDescending(x => x.CpuPercent)
                              .ThenByDescending(x => x.WorkingSet);
            case 2: return src.OrderBy(x => x.DisplayName,
                              StringComparer.OrdinalIgnoreCase);
            default: return src.OrderByDescending(x => x.WorkingSet);
        }
    }

    private bool _filterInFlight;

    private void ApplyFilter()
    {
        if (_filterInFlight) return;            // reentrancy guard
        _filterInFlight = true;
        try
        {
            string q = TxtSearch != null ? (TxtSearch.Text ?? "").Trim() : "";
            string qLower = q.Length > 0 ? q.ToLowerInvariant() : null;
            bool searching = qLower != null;
            // When the user is actively searching, include foreground processes
            // regardless of the "show foreground" checkbox — otherwise users
            // can't find the app they just focused (e.g. searching "notepad"
            // while the checkbox is off would hide the very process that is
            // currently foregrounded).
            bool showFg = searching || (ChkShowFg != null && ChkShowFg.IsChecked == true);

            _viewProcs.Clear();

            bool firstBg = true;   // First background row after foreground carries IsSectionBreak.
            bool anyFg = false;

            if (showFg)
            {
                foreach (var p in SortProcs(_allProcs.Where(x => x.IsForeground)))
                {
                    if (searching)
                    {
                        string hay = ((p.Name ?? "") + " " + (p.WindowTitle ?? "")).ToLowerInvariant();
                        if (!hay.Contains(qLower)) continue;
                    }
                    p.IsSectionBreak = false;
                    _viewProcs.Add(p);
                    anyFg = true;
                }
            }

            foreach (var p in SortProcs(_allProcs.Where(x => !x.IsForeground)))
            {
                if (searching)
                {
                    string hay = (p.Name ?? "").ToLowerInvariant();
                    if (!hay.Contains(qLower)) continue;
                }
                p.IsSectionBreak = firstBg && anyFg;
                _viewProcs.Add(p);
                firstBg = false;
            }
        }
        finally { _filterInFlight = false; }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        if (TxtSearchHint != null)
            TxtSearchHint.Visibility = string.IsNullOrEmpty(TxtSearch.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void OnShowFgChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (ChkShowFg != null)
            _settings.ShowForegroundProcs = ChkShowFg.IsChecked == true;
        _settings.Save();
        ApplyFilter();
    }

    // Network monitoring on/off — defaults to off to keep our footprint small.
    // Flipping this also hides the overlay and lets OnTimerTick push the tab.
    private void OnEnableNetChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || ChkEnableNet == null) return;
        _settings.EnableNetMonitor = ChkEnableNet.IsChecked == true;
        _settings.Save();
        ApplyNetEnabledUI();
        SettingsChanged?.Invoke();    // main window adjusts its _netTimer
        if (!_settings.EnableNetMonitor)
        {
            // Clear the process list so rows don’t linger with stale numbers.
            _netProcs.Clear();
            _netByPid.Clear();
        }
    }

    private void OnEnableNetFromOverlay(object sender, RoutedEventArgs e)
    {
        if (ChkEnableNet != null) ChkEnableNet.IsChecked = true;
    }

    private void ApplyNetEnabledUI()
    {
        bool on = _settings.EnableNetMonitor;
        if (NetDisabledOverlay != null)
            NetDisabledOverlay.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnShowPathChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || ChkShowPath == null) return;
        // Fan the checkbox state out to every ProcessInfo — the cell template
        // binds to ProcessInfo.PathVisibility (DataContext) which is stable
        // under virtualization, unlike a RelativeSource AncestorType=Window
        // binding which NREs inside PropertyMetadata.get_DefaultValue when
        // WPF recycles the row container.
        var v = ChkShowPath.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _allProcs.Count; i++)
            _allProcs[i].PathVisibility = v;
    }

    private void OnProcessSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || CmbProcSort == null) return;
        _procSortMode = CmbProcSort.SelectedIndex;
        ApplyFilter();
    }

    private void UpdateMemoryBar()
    {
        var info = _memService.GetMemoryInfo();
        uint pct = info.MemoryLoadPercent;
        double totalGB = info.TotalPhysical / (1024.0 * 1024 * 1024);
        double usedGB = (info.TotalPhysical - info.AvailablePhysical) / (1024.0 * 1024 * 1024);

        if (TxtMemPct != null) TxtMemPct.Text = pct.ToString();
        if (TxtMemStatus != null)
        {
            if (pct < 50) TxtMemStatus.Text = "\u7535\u8111\u5145\u6EE1\u6D3B\u529B";
            else if (pct < 80) TxtMemStatus.Text = "\u5185\u5B58\u5360\u7528\u8F83\u9AD8";
            else TxtMemStatus.Text = "\u5185\u5B58\u5360\u7528\u8FC7\u9AD8!";
        }
        TxtMemInfo.Text = $"\u5185\u5B58: {pct}% \u5DF2\u7528";
        TxtMemDetail.Text = $"({usedGB:F1} / {totalGB:F1} GB)";
    }

    private void OnProcessSelected(object sender, SelectedCellsChangedEventArgs e)
    {
        // A right-click on an unselected row triggers this event too; ignore
        // that case so the red “pinned” selection stays on whatever row the
        // user last left-clicked. The menu handler below always targets
        // _selectedProcess first, which matches user expectation.
        if (Mouse.RightButton == MouseButtonState.Pressed)
        {
            UpdateSelCount();
            return;
        }
        var grid = sender as DataGrid;
        if (grid != null)
        {
            var pi = grid.SelectedItem as ProcessInfo;
            if (pi != null) _selectedProcess = pi;
        }
        UpdateSelCount();
    }

    private void UpdateSelCount()
    {
        int selCount = 0;
        for (int i = 0; i < _allProcs.Count; i++) if (_allProcs[i].IsSelected) selCount++;
        bool hasAction = selCount > 0 || _selectedProcess != null;
        BtnCleanProc.IsEnabled = hasAction;
        BtnKillProc.IsEnabled = hasAction;
        if (TxtSelCount != null)
            TxtSelCount.Text = selCount > 0 ? $"\u5DF2\u9009 {selCount} \u4E2A" : "";
    }

    private List<ProcessInfo> GetActionTargets()
    {
        var selected = _allProcs.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0 && _selectedProcess != null)
            selected.Add(_selectedProcess);
        return selected;
    }

    private void OnCleanProcess(object sender, RoutedEventArgs e)
    {
        var targets = GetActionTargets();
        if (targets.Count == 0) return;
        foreach (var p in targets) _memService.CleanProcess(p.Pid);
        RefreshProcessList();
    }

    private void OnKillProcess(object sender, RoutedEventArgs e)
    {
        var targets = GetActionTargets();
        KillTargets(targets);
    }

    // Context-menu target resolution — always prefer the currently “pinned”
    // (red-highlighted) selection so that right-clicking any row invokes the
    // action on the row the user explicitly clicked. Falls back to the hit
    // row only when nothing is pinned.
    private ProcessInfo GetMenuTarget(object sender)
    {
        if (_selectedProcess != null && _viewProcs.Contains(_selectedProcess))
            return _selectedProcess;
        return GetContextMenuTarget(sender);
    }

    // Right-click menu “结束进程” — kill just the row under the cursor, ignoring
    // any current checkbox selection (so the user doesn’t accidentally kill a
    // batch they had queued up elsewhere).
    private void OnKillFromMenu(object sender, RoutedEventArgs e)
    {
        var p = GetMenuTarget(sender);
        if (p == null) return;
        KillTargets(new List<ProcessInfo> { p });
    }

    private void KillTargets(List<ProcessInfo> targets)
    {
        if (targets == null || targets.Count == 0) return;

        bool confirmed;
        if (targets.Count == 1)
        {
            var p = targets[0];
            var r = MessageBox.Show(
                $"\u786E\u5B9A\u8981\u7ED3\u675F\u8FDB\u7A0B \"{p.Name}\" (PID: {p.Pid}) \u5417?\n\u5F3A\u5236\u7ED3\u675F\u53EF\u80FD\u5BFC\u81F4\u6570\u636E\u4E22\u5931\u3002",
                "\u786E\u8BA4", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            confirmed = r == MessageBoxResult.Yes;
        }
        else
        {
            var dlg = new MultiKillConfirmDialog(targets) { Owner = this };
            confirmed = dlg.ShowDialog() == true;
        }

        if (!confirmed) return;

        var failed = new List<ProcessInfo>();
        foreach (var p in targets)
        {
            if (!_memService.KillProcess(p.Pid))
            {
                if (!_memService.KillProcessForce(p.Pid)) failed.Add(p);
            }
        }
        RefreshProcessList();

        if (failed.Count > 0)
        {
            var names = string.Join(", ", failed.Select(p => $"{p.Name}({p.Pid})"));
            MessageBox.Show(
                $"\u4EE5\u4E0B\u8FDB\u7A0B\u65E0\u6CD5\u7ED3\u675F\uFF08\u6743\u9650\u4E0D\u8DB3\u6216\u53D7\u4FDD\u62A4\uFF09\uFF1A\n{names}",
                "\u90E8\u5206\u5931\u8D25", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenFileLocation(object sender, RoutedEventArgs e)
    {
        var p = GetMenuTarget(sender);
        if (p == null || string.IsNullOrEmpty(p.FilePath)) return;
        try
        {
            if (File.Exists(p.FilePath))
                Process.Start("explorer.exe", $"/select,\"{p.FilePath}\"");
            else
            {
                var dir = Path.GetDirectoryName(p.FilePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start("explorer.exe", dir);
            }
        }
        catch { }
    }

    private void OnCopyFilePath(object sender, RoutedEventArgs e)
    {
        var p = GetMenuTarget(sender);
        if (p == null || string.IsNullOrEmpty(p.FilePath)) return;
        try { Clipboard.SetText(p.FilePath); } catch { }
    }

    private static ProcessInfo GetContextMenuTarget(object sender)
    {
        var mi = sender as MenuItem;
        var cm = mi != null ? mi.Parent as ContextMenu : null;
        if (cm != null)
        {
            var row = cm.PlacementTarget as DataGridRow;
            if (row != null) return row.Item as ProcessInfo;
        }
        return null;
    }

    private async void OnOneClickBoost(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.IsEnabled = false;
        btn.Content = "\u6E05\u7406\u4E2D...";

        var result = await Task.Run(() => _memService.CleanMemory());

        btn.Content = $"\u5DF2\u91CA\u653E {ProcessInfo.FormatBytes(result.freedBytes)}";
        RefreshProcessList();
        UpdateMemoryBar();

        await Task.Delay(2000);
        btn.Content = "\u4E00\u952E\u52A0\u901F";
        btn.IsEnabled = true;
    }

    // ── Network Tab ──
    private void UpdateNetworkDisplay()
    {
        _netMonitor.Update();
        TxtDlSpeed.Text = NetworkMonitor.FormatSpeed(_netMonitor.DownloadSpeed);
        TxtUlSpeed.Text = NetworkMonitor.FormatSpeed(_netMonitor.UploadSpeed);
        TxtTotalDl.Text = ProcessInfo.FormatBytes(_netMonitor.TotalRecv);
        TxtTotalUl.Text = ProcessInfo.FormatBytes(_netMonitor.TotalSent);

        RefreshNetProcList();
    }

    private void RefreshNetProcList()
    {
        List<ProcNetInfoNative> stats;
        try { stats = _memService.GetPerProcessNetStats(); }
        catch { return; }

        // Build a name map from the last full process snapshot; fall back to
        // a lightweight Process.GetProcessById lookup for PIDs we haven't seen.
        var nameMap = new Dictionary<uint, string>(_allProcs.Count);
        foreach (var p in _allProcs) nameMap[p.Pid] = p.Name;

        var seen = new HashSet<uint>();
        foreach (var s in stats)
        {
            // Hide completely idle entries with no connections — only show PIDs
            // that either have live sockets or actually moved bytes this tick.
            if (s.TcpConnections == 0 && s.UdpConnections == 0 &&
                s.BytesInPerSec < 1 && s.BytesOutPerSec < 1) continue;

            seen.Add(s.Pid);
            if (!_netByPid.TryGetValue(s.Pid, out var item))
            {
                item = new NetProcItem { Pid = s.Pid };
                _netByPid[s.Pid] = item;
                _netProcs.Add(item);
            }
            string name;
            if (!nameMap.TryGetValue(s.Pid, out name) || string.IsNullOrEmpty(name))
            {
                try { name = Process.GetProcessById((int)s.Pid).ProcessName + ".exe"; }
                catch { name = $"PID {s.Pid}"; }
            }
            if (item.Name != name) item.Name = name;
            item.UpSpeed        = s.BytesOutPerSec;
            item.DownSpeed      = s.BytesInPerSec;
            item.TcpConnections = (int)s.TcpConnections;
            item.UdpConnections = (int)s.UdpConnections;
        }

        // Remove rows that dropped out of the active set.
        for (int i = _netProcs.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(_netProcs[i].Pid))
            {
                _netByPid.Remove(_netProcs[i].Pid);
                _netProcs.RemoveAt(i);
            }
        }

        SortNetProcs();
    }

    private void SortNetProcs()
    {
        // In-place sort — faster than re-adding, and keeps selection stable.
        var list = _netProcs.ToList();
        switch (_netSortMode)
        {
            case 1: list.Sort((a, b) => b.UpSpeed.CompareTo(a.UpSpeed)); break;
            case 2: list.Sort((a, b) => b.DownSpeed.CompareTo(a.DownSpeed)); break;
            case 3: list.Sort((a, b) => (b.TcpConnections + b.UdpConnections)
                                         .CompareTo(a.TcpConnections + a.UdpConnections)); break;
            case 4: list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)); break;
            default: list.Sort((a, b) => b.TotalSpeed.CompareTo(a.TotalSpeed)); break;
        }
        for (int i = 0; i < list.Count; i++)
        {
            int cur = _netProcs.IndexOf(list[i]);
            if (cur != i) _netProcs.Move(cur, i);
        }
    }

    private void OnNetSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || CmbNetSort == null) return;
        _netSortMode = CmbNetSort.SelectedIndex;
        if (_netSortMode < 0) _netSortMode = 0;
        SortNetProcs();
    }

    // ── Settings Tab ──
    private void LoadSettings()
    {
        ChkAutoStart.IsChecked = StartupManager.IsEnabled();
        ChkAutoClean.IsChecked = _settings.AutoClean;
        TxtInterval.Text = _settings.AutoCleanIntervalMinutes.ToString();
        SliderOpacity.Value = _settings.BallOpacity;

        switch (_settings.BallSkin)
        {
            case 1: SkinBlue.IsChecked = true; break;
            case 2: SkinPurple.IsChecked = true; break;
            case 3: SkinDark.IsChecked = true; break;
            default: SkinDefault.IsChecked = true; break;
        }

        if (ChkMinToTray != null) ChkMinToTray.IsChecked = _settings.MinimizeToTrayOnExit;
    }

    private void OnMinToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _settings.MinimizeToTrayOnExit = ChkMinToTray.IsChecked == true;
        NotifySettingsChanged();
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        bool enabled = ChkAutoStart.IsChecked == true;
        _settings.AutoStart = enabled;
        try { StartupManager.SetEnabled(enabled); } catch { }
        NotifySettingsChanged();
    }

    private void OnAutoCleanChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _settings.AutoClean = ChkAutoClean.IsChecked == true;
        NotifySettingsChanged();
    }

    private void OnIntervalChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        if (int.TryParse(TxtInterval.Text, out int val) && val > 0)
        {
            _settings.AutoCleanIntervalMinutes = val;
            NotifySettingsChanged();
        }
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        _settings.BallOpacity = SliderOpacity.Value;
        NotifySettingsChanged();
    }

    private void OnSkinChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (SkinBlue.IsChecked == true) _settings.BallSkin = 1;
        else if (SkinPurple.IsChecked == true) _settings.BallSkin = 2;
        else if (SkinDark.IsChecked == true) _settings.BallSkin = 3;
        else _settings.BallSkin = 0;
        ApplySkinToHeader();
        NotifySettingsChanged();
    }

    private void ApplySkinToHeader()
    {
        // Keep panel header visually in sync with the selected skin.
        Color a, b, fa, fb;
        switch (_settings.BallSkin)
        {
            case 1: a = Color.FromRgb(0x1C, 0x6F, 0xB0); b = Color.FromRgb(0x2B, 0x95, 0xD6);
                    fa = a; fb = b; break;
            case 2: a = Color.FromRgb(0x7A, 0x3F, 0xB8); b = Color.FromRgb(0xA9, 0x6A, 0xDD);
                    fa = a; fb = b; break;
            case 3: a = Color.FromRgb(0x22, 0x26, 0x2E); b = Color.FromRgb(0x3A, 0x40, 0x4B);
                    fa = a; fb = b; break;
            default: a = Color.FromRgb(0x2B, 0x95, 0xD6); b = Color.FromRgb(0x23, 0xB5, 0x74);
                    fa = a; fb = b; break;
        }
        if (HeaderStop1 != null) HeaderStop1.Color = a;
        if (HeaderStop2 != null) HeaderStop2.Color = b;
        if (FooterStop1 != null) FooterStop1.Color = fa;
        if (FooterStop2 != null) FooterStop2.Color = fb;
    }

    private void NotifySettingsChanged()
    {
        var h = SettingsChanged;
        if (h != null) h();
    }

    // ── Tab switch ──
    private void TabChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        ProcessPanel.Visibility = TabProcess.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NetworkPanel.Visibility = TabNetwork.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = TabSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (TabProcess.IsChecked == true)
            RefreshProcessList();
        if (TabNetwork.IsChecked == true && _settings.EnableNetMonitor)
        {
            // Reset per-connection deltas when re-entering the network tab so
            // the first visible sample starts from zero rather than an
            // accumulated burst since the DLL was loaded.
            try { _memService.ResetNetStats(); } catch { }
            UpdateNetworkDisplay();
        }
    }

    // ── Title drag ──
    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnExitApp(object sender, RoutedEventArgs e)
    {
        var main = Application.Current.MainWindow as MainWindow;

        if (_settings.MinimizeToTrayOnExit)
        {
            if (main != null) main.HideToTray();
            Close();
            return;
        }

        var dlg = new ExitConfirmDialog { Owner = this };
        dlg.ShowDialog();
        switch (dlg.Choice)
        {
            case ExitChoice.Minimize:
                if (main != null) main.HideToTray();
                Close();
                break;
            case ExitChoice.Exit:
                if (main != null) main.ExitApp();
                else Application.Current.Shutdown();
                break;
            default:
                break; // Cancel: do nothing
        }
    }
}
