using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MemoryBooster;

public partial class App : Application
{
    // Crash log lives next to the exe when writable, otherwise falls back to
    // %LocalAppData%\MemoryBooster\error.log so Program Files installs still
    // get a paper trail. The path is resolved lazily on the first crash so a
    // clean run never leaves an empty error.log on disk.
    private static string _logPath;

    // Single-instance guard. Held for the entire process lifetime; the GC-root
    // reference here prevents collection from releasing the mutex prematurely.
    // Name includes a fixed GUID to avoid collisions with any unrelated app
    // that happens to share the product name.
    private const string SingleInstanceMutexName =
        "Local\\MemoryBooster.SingleInstance.{7F3C1E9A-4B2D-4E10-9A55-2F6D3C1B8E07}";
    private static Mutex _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Acquire the single-instance mutex before any UI is shown. If another
        // instance already owns it, surface a short notice and bail out — we
        // do NOT try to activate the existing window (the tray icon is the
        // user’s re-entry point).
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
        }
        catch
        {
            // If mutex creation itself fails (extremely unusual), fall through
            // rather than blocking the user.
            createdNew = true;
        }
        if (!createdNew)
        {
            try
            {
                MessageBox.Show(
                    "内存清理加速器已在运行。\n\n请从托盘图标打开主界面。",
                    "内存清理加速器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_singleInstanceMutex != null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }
        catch { }
        base.OnExit(e);
    }

    private static string EnsureLogPath()
    {
        if (_logPath != null) return _logPath;
        // Try exe directory first. Opening with FileMode.Append *will* create
        // the file, so only do this when we are actually about to write a
        // crash record — i.e. from LogError, not at startup.
        try
        {
            var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
            using (File.Open(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
            _logPath = p;
            return _logPath;
        }
        catch { }
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MemoryBooster");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "error.log");
            return _logPath;
        }
        catch { return null; }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Non-fatal — log, show a toast-ish dialog, keep the app running.
        LogError(e.Exception, "Dispatcher");
        e.Handled = true;
        ShowErrorDialog(e.Exception, fatal: false);
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogError(ex, "Domain-fatal");
            ShowErrorDialog(ex, fatal: true);
        }
    }

    private void OnTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
        LogError(e.Exception, "UnobservedTask");
        e.SetObserved();
    }

    private static void LogError(Exception ex, string source)
    {
        var path = EnsureLogPath();
        if (path == null) return;
        try
        {
            var sb = new StringBuilder();
            sb.Append('=', 72).AppendLine();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] source={source}");
            sb.AppendLine($"os   = {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
            sb.AppendLine($"app  = {Assembly.GetExecutingAssembly().GetName().Version}");
            sb.AppendLine($"proc = {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
            sb.AppendLine(ex.ToString());
            sb.AppendLine();
            File.AppendAllText(path, sb.ToString());
        }
        catch { }
    }

    private static void ShowErrorDialog(Exception ex, bool fatal)
    {
        try
        {
            string header = fatal ? "程序发生严重错误即将退出" : "程序发生错误（已忽略）";
            string body = $"{header}\n\n{ex.GetType().Name}: {ex.Message}\n\n日志已写入：\n{_logPath ?? "(路径不可用)"}";
            MessageBox.Show(body, "Memory Booster",
                MessageBoxButton.OK,
                fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
        }
        catch { }
    }
}
