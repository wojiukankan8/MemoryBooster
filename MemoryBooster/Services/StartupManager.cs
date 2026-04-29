using System;
using System.Reflection;
using Microsoft.Win32;

namespace MemoryBooster.Services;

public static class StartupManager
{
    private const string AppName = "MemoryBooster";
    private const string RegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RegKey, false))
        {
            return key != null && key.GetValue(AppName) != null;
        }
    }

    public static void SetEnabled(bool enable)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RegKey, true))
        {
            if (key == null) return;
            if (enable)
            {
                var exePath = Assembly.GetExecutingAssembly().Location;
                key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
    }
}
