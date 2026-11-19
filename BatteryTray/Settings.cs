using Microsoft.Win32;

namespace BatteryTray;

/// User settings, kept in HKCU\Software\BatteryTray. Failing to read or write one is never
/// fatal: the default is used instead.
static class Settings
{
    const string Key = @"Software\BatteryTray";

    public static readonly int[] RefreshChoices = [1, 5, 10, 15, 20, 30];
    const int DefaultRefreshMinutes = 30;

    /// Auto-refresh period, one of <see cref="RefreshChoices"/>.
    public static int RefreshMinutes
    {
        get => Read("RefreshMinutes") is int m && RefreshChoices.Contains(m) ? m : DefaultRefreshMinutes;
        set => Write("RefreshMinutes", value, RegistryValueKind.DWord);
    }

    /// The widget's look, chosen from the right-click menu.
    public static Look Look
    {
        get => Enum.TryParse<Look>(Read("Style") as string, out var l) && Enum.IsDefined(l) ? l : Look.Windows11;
        set => Write("Style", value.ToString(), RegistryValueKind.String);
    }

    static object? Read(string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(name);
        }
        catch { return null; }
    }

    static void Write(string name, object value, RegistryValueKind kind)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue(name, value, kind);
        }
        catch { /* not critical: falls back to the default next start */ }
    }
}
