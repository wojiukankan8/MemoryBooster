using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MemoryBooster.Models;
using MemoryBooster.Services;
using MemoryBooster.Views;
using WinForms = System.Windows.Forms;

namespace MemoryBooster;

public partial class MainWindow : Window
{
    private readonly MemoryService _memService = new MemoryService();
    private readonly NetworkMonitor _netMonitor = new NetworkMonitor();
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _netTimer;
    private readonly DispatcherTimer _autoCleanTimer;
    // Periodic self-trim: EmptyWorkingSet on ourselves so MemoryBooster's
    // own footprint stays tiny (WPF tends to bloat with images/caches).
    private readonly DispatcherTimer _selfTrimTimer;

    public MemoryService MemoryService => _memService;
    public NetworkMonitor NetMonitor => _netMonitor;
    public AppSettings Settings => _settings;

    private AppSettings _settings;
    private bool _isCleaning;
    private bool _isDragging;
    private Point _dragStart;
    private double _windowTop;
    private BoosterPanel _panel;
    private Storyboard _spinStoryboard;

    private WinForms.NotifyIcon _trayIcon;
    private bool _isInTray;

    // Edge-hide state
    private readonly DispatcherTimer _hideTimer;
    private bool _isHidden;
    private double _screenRight;
    private const double PeekWidth = 14;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();

        // Position
        _screenRight = SystemParameters.PrimaryScreenWidth;
        double posY = _settings.BallPositionY >= 0
            ? _settings.BallPositionY
            : (SystemParameters.PrimaryScreenHeight / 2 - Height / 2);
        // First launch: show the ball fully for ~20s before auto-hiding, so new
        // users can find it and the hover hints have a chance to appear.
        Left = _screenRight - Width - 4;
        Top = posY;
        _isHidden = false;

        Opacity = _settings.BallOpacity;

        // Memory update timer (2s)
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _updateTimer.Tick += (s, e) => { try { UpdateMemoryDisplay(); } catch { } };
        _updateTimer.Start();

        // Network update timer (1s) — only runs when user enables monitoring.
        _netTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _netTimer.Tick += (s, e) => { try { UpdateNetworkDisplay(); } catch { } };
        ApplyNetMonitorSetting();

        // Self-trim every 60s — calls EmptyWorkingSet on our own PID.
        _selfTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _selfTrimTimer.Tick += (s, e) => { try { TrimSelf(); } catch { } };
        _selfTrimTimer.Start();

        // Auto-clean timer
        _autoCleanTimer = new DispatcherTimer();
        _autoCleanTimer.Tick += async (s, e) => { try { await DoCleanAsync(); } catch { } };
        ApplyAutoCleanSettings();

        // Hide timer
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _hideTimer.Tick += (s, e) => { _hideTimer.Stop(); SlideToEdge(); };

        // First-launch 20s grace period before initial edge-hide.
        var firstShow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        firstShow.Tick += (s, e) =>
        {
            firstShow.Stop();
            if (!IsMouseOver && _panel == null) _hideTimer.Start();
        };
        firstShow.Start();

        // Events
        MouseEnter += (s, e) => { _hideTimer.Stop(); SlideIn(); };
        MouseLeave += (s, e) => { if (!_isDragging && _panel == null) _hideTimer.Start(); };
        MouseLeftButtonDown += OnBallMouseDown;
        MouseMove += OnBallMouseMove;
        MouseLeftButtonUp += OnBallMouseUp;
        MouseRightButtonUp += OnRightClick;

        // Initial display
        try { UpdateMemoryDisplay(); } catch { }
        try { _netMonitor.Update(); } catch { }

        // Spinner animation
        SetupSpinnerAnimation();

        // Tray icon
        InitTrayIcon();
    }

    private void InitTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "内存加速器",
            Visible = false
        };
        try
        {
            var sri = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/icon.ico"));
            if (sri != null) _trayIcon.Icon = new System.Drawing.Icon(sri.Stream);
            else _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }
        catch { _trayIcon.Icon = System.Drawing.SystemIcons.Application; }

        var menu = new WinForms.ContextMenuStrip();
        var miShow = new WinForms.ToolStripMenuItem("打开面板");
        miShow.Click += (s, e) => RestoreFromTray(showPanel: true);
        var miRestore = new WinForms.ToolStripMenuItem("显示悬浮球");
        miRestore.Click += (s, e) => RestoreFromTray(showPanel: false);
        var miExit = new WinForms.ToolStripMenuItem("退出程序");
        miExit.Click += (s, e) => ExitApp();
        menu.Items.Add(miShow);
        menu.Items.Add(miRestore);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(miExit);
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (s, e) => RestoreFromTray(showPanel: false);
    }

    public void HideToTray()
    {
        if (_panel != null) ClosePanel();
        _isInTray = true;
        Visibility = Visibility.Hidden;
        ShowInTaskbar = false;
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
            try { _trayIcon.ShowBalloonTip(1500, "内存加速器",
                "程序已最小化到托盘，双击托盘图标恢复",
                WinForms.ToolTipIcon.Info); } catch { }
        }
    }

    private void RestoreFromTray(bool showPanel)
    {
        if (!_isInTray && Visibility == Visibility.Visible)
        {
            if (showPanel && _panel == null) OpenPanel();
            return;
        }
        _isInTray = false;
        Visibility = Visibility.Visible;
        Activate();
        if (_trayIcon != null) _trayIcon.Visible = false;
        SlideIn();
        if (showPanel && _panel == null) OpenPanel();
    }

    public void ExitApp()
    {
        try { if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); } } catch { }
        Application.Current.Shutdown();
    }

    private void SetupSpinnerAnimation()
    {
        var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        _spinStoryboard = new Storyboard();
        _spinStoryboard.Children.Add(anim);
        Storyboard.SetTarget(anim, SpinnerArc);
        Storyboard.SetTargetProperty(anim,
            new PropertyPath("(Path.RenderTransform).(RotateTransform.Angle)"));
    }

    // ── Memory Display ──
    private void UpdateMemoryDisplay()
    {
        if (_isCleaning) return;
        uint pct = _memService.GetMemoryLoadPercent();
        TxtPercent.Text = $"{pct}%";
        UpdateBallColor(pct);
    }

    private void UpdateBallColor(uint pct)
    {
        Color bright, dark;
        switch (_settings.BallSkin)
        {
            case 1: // Blue skin
                bright = Color.FromRgb(0x6C, 0xC8, 0xFF);
                dark = Color.FromRgb(0x18, 0x6F, 0xB5);
                break;
            case 2: // Purple skin
                bright = Color.FromRgb(0xC8, 0x9A, 0xF0);
                dark = Color.FromRgb(0x6A, 0x33, 0xB0);
                break;
            case 3: // Dark skin
                bright = Color.FromRgb(0x55, 0x5A, 0x66);
                dark = Color.FromRgb(0x18, 0x1B, 0x22);
                break;
            default: // Dynamic (follow memory load)
                if (pct < 50)
                {
                    byte r = (byte)(255 * pct / 50);
                    bright = Color.FromRgb(r, 255, 80);
                    dark = Color.FromRgb((byte)(r * 0.5), 180, 40);
                }
                else if (pct < 80)
                {
                    byte g = (byte)(255 * (80 - pct) / 30);
                    bright = Color.FromRgb(255, g, 50);
                    dark = Color.FromRgb(200, (byte)(g * 0.6), 30);
                }
                else
                {
                    bright = Color.FromRgb(255, 60, 60);
                    dark = Color.FromRgb(180, 20, 20);
                }
                break;
        }
        GradStop1.Color = bright;
        GradStop2.Color = dark;
    }

    // ── Network Display ──
    private void UpdateNetworkDisplay()
    {
        if (!_settings.EnableNetMonitor)
        {
            NetSpeedPanel.Visibility = Visibility.Collapsed;
            return;
        }
        _netMonitor.Update();
        if (_isHidden) return;
        NetSpeedPanel.Visibility = Visibility.Visible;
        TxtUpSpeed.Text = $"\u2191 {NetworkMonitor.FormatSpeed(_netMonitor.UploadSpeed)}";
        TxtDownSpeed.Text = $"\u2193 {NetworkMonitor.FormatSpeed(_netMonitor.DownloadSpeed)}";
    }

    private void ApplyNetMonitorSetting()
    {
        if (_settings.EnableNetMonitor)
        {
            if (!_netTimer.IsEnabled) _netTimer.Start();
        }
        else
        {
            _netTimer.Stop();
            NetSpeedPanel.Visibility = Visibility.Collapsed;
        }
    }

    // Shrink our own working set. Uses the same EmptyWorkingSet path as
    // the "clean process" button applied to other PIDs.
    private void TrimSelf()
    {
        try
        {
            uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            _memService.CleanProcess(pid);
        }
        catch { }
    }

    // ── Slide Animation ──
    private void SlideIn()
    {
        if (!_isHidden) return;
        _isHidden = false;
        var anim = new DoubleAnimation(_screenRight - Width - 4, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(LeftProperty, anim);
    }

    private void SlideToEdge()
    {
        if (_isHidden || _panel != null) return;
        _isHidden = true;
        NetSpeedPanel.Visibility = Visibility.Collapsed;
        var anim = new DoubleAnimation(_screenRight - PeekWidth, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        BeginAnimation(LeftProperty, anim);
    }

    // ── Drag ──
    private void OnBallMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _dragStart = e.GetPosition(this);
        _windowTop = Top;
        CaptureMouse();
    }

    private void OnBallMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.Y - _dragStart.Y) > 5)
            _isDragging = true;
        if (_isDragging)
        {
            BeginAnimation(TopProperty, null);
            Top = _windowTop + (pos.Y - _dragStart.Y);
        }
    }

    private void OnBallMouseUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_isDragging)
        {
            _settings.BallPositionY = (int)Top;
            _settings.Save();
            _isDragging = false;
            return;
        }

        // Left-click (single or double) → clean. Drag is handled above.
        OnBallClick();
    }

    // ── Double-click → Clean ──
    private async void OnBallClick()
    {
        if (_isCleaning) return;
        if (_panel != null) ClosePanel();
        await DoCleanAsync();
    }

    private async Task DoCleanAsync()
    {
        if (_isCleaning) return;
        _isCleaning = true;

        TxtPercent.Visibility = Visibility.Collapsed;
        TxtLabel.Visibility = Visibility.Collapsed;
        SpinnerOverlay.Visibility = Visibility.Visible;
        if (_spinStoryboard != null) _spinStoryboard.Begin();

        var result = await Task.Run(() => _memService.CleanMemory());
        var freed = result.freedBytes;

        if (_spinStoryboard != null) _spinStoryboard.Stop();
        SpinnerOverlay.Visibility = Visibility.Collapsed;
        TxtPercent.Visibility = Visibility.Visible;
        TxtLabel.Visibility = Visibility.Visible;
        _isCleaning = false;

        UpdateMemoryDisplay();
        ShowTooltip($"已释放 {ProcessInfo.FormatBytes(freed)}");
    }

    private void ShowTooltip(string text)
    {
        var tip = new ToolTipPopup(text);
        tip.Left = Left - tip.Width + 10;
        tip.Top = Top - 36;
        tip.Show();
    }

    // ── Right-click → open / close panel directly ──
    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_panel == null) OpenPanel();
        else ClosePanel();
        e.Handled = true;
    }

    private void OpenPanel()
    {
        _hideTimer.Stop();
        _panel = new BoosterPanel(_memService, _netMonitor, _settings);
        _panel.Left = Left - _panel.Width - 6;
        _panel.Top = Math.Max(10, Top - 150);
        _panel.Closed += (s2, e2) => { _panel = null; _hideTimer.Start(); };
        _panel.SettingsChanged += () => { ApplySettings(); };
        _panel.Show();
    }

    private void ClosePanel()
    {
        if (_panel != null) _panel.Close();
        _panel = null;
    }

    public void ApplySettings()
    {
        Opacity = _settings.BallOpacity;
        // Re-apply skin immediately without waiting for next timer tick.
        try { UpdateBallColor(_memService.GetMemoryLoadPercent()); } catch { }
        ApplyAutoCleanSettings();
        ApplyNetMonitorSetting();
        _settings.Save();
    }

    private void ApplyAutoCleanSettings()
    {
        if (_settings.AutoClean && _settings.AutoCleanIntervalMinutes > 0)
        {
            _autoCleanTimer.Interval = TimeSpan.FromMinutes(_settings.AutoCleanIntervalMinutes);
            _autoCleanTimer.Start();
        }
        else
        {
            _autoCleanTimer.Stop();
        }
    }
}