using System;
using System.IO;
using System.Xml.Serialization;

namespace MemoryBooster.Models;

public class AppSettings
{
    public bool AutoStart { get; set; } = false;
    public bool AutoClean { get; set; } = false;
    public int AutoCleanIntervalMinutes { get; set; } = 10;
    public double BallOpacity { get; set; } = 0.9;
    public double BallSize { get; set; } = 70;
    public int BallSkin { get; set; } = 0;
    public int BallPositionY { get; set; } = -1;
    public bool MinimizeToTrayOnExit { get; set; } = false;

    // When false (default), network sampling is fully off to save memory / CPU.
    // When true, BoosterPanel refreshes the Network tab and the floating ball
    // timer pulls total up/down speed.
    public bool EnableNetMonitor { get; set; } = false;

    // Default on so the user sees foreground-window processes above the divider.
    public bool ShowForegroundProcs { get; set; } = true;

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MemoryBooster", "settings.xml");

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var ser = new XmlSerializer(typeof(AppSettings));
            using (var sw = new StreamWriter(ConfigPath))
            {
                ser.Serialize(sw, this);
            }
        }
        catch { }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var ser = new XmlSerializer(typeof(AppSettings));
                using (var sr = new StreamReader(ConfigPath))
                {
                    return (AppSettings)ser.Deserialize(sr);
                }
            }
        }
        catch { }
        return new AppSettings();
    }
}
