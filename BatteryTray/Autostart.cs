using Microsoft.Win32;

namespace BatteryTray;

/// Autostart is a shortcut in the user's Startup folder, so it shows up (and can be toggled)
/// in Task Manager / Settings > Startup apps. Windows records a toggle there in StartupApproved.
static class Autostart
{
    const string AppName = "BatteryTray";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    const string ShortcutName = AppName + ".lnk";
    static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    public static bool IsEnabled
    {
        get
        {
            if (!File.Exists(ShortcutPath)) return false;
            try
            {
                // Odd first byte = switched off in Startup apps.
                using var k = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                if (k?.GetValue(ShortcutName) is byte[] { Length: > 0 } b && (b[0] & 1) == 1) return false;
            }
            catch { }
            return true;
        }
    }

    public static void Set(bool on)
    {
        try
        {
            RemoveLegacyRunEntry();
            if (on) CreateShortcut();
            else if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            // Clear any "disabled" flag so the Startup apps toggle matches.
            using var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            k?.DeleteValue(ShortcutName, false);
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    /// Older versions used the Run key, which Windows was silently skipping; move it to the shortcut.
    public static void Migrate()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            if (k?.GetValue(AppName) is not string) return;
            RemoveLegacyRunEntry();
            if (!File.Exists(ShortcutPath)) CreateShortcut();
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    static void RemoveLegacyRunEntry()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue(AppName, false);
    }

    static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic lnk = shell.CreateShortcut(ShortcutPath);
        lnk.TargetPath = Environment.ProcessPath;
        lnk.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        lnk.Description = "Battery levels on the taskbar";
        lnk.Save();
    }
}
