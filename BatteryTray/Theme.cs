using System.Drawing;
using Microsoft.Win32;

namespace BatteryTray;

/// The system's light/dark mode and accent color. Read once, re-read after <see cref="Invalidate"/>.
sealed record SystemTheme(bool Light, Color Accent)
{
    static SystemTheme? cached;
    public static SystemTheme Current => cached ??= Read(SystemLight());
    public static void Invalidate() => cached = null;

    /// The theme for the given mode, with the system accent color tuned for it.
    public static SystemTheme Read(bool light)
    {
        // AccentPalette: 8 RGBA entries, Light3..Light1, Base, Dark1..Dark3. WinUI's default
        // accent fill is Light2 on dark backgrounds and Dark1 on light ones.
        Color accent = light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 205, 255);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] { Length: >= 32 } b)
            {
                int i = (light ? 4 : 1) * 4;
                accent = Color.FromArgb(b[i], b[i + 1], b[i + 2]);
            }
        }
        catch { }
        return new(light, accent);
    }

    static bool SystemLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }
}

/// Colors of the ring looks (Windows 11 and Classic), also used for the Fluent menu text.
sealed record Palette(Color Primary, Color Secondary, Color Track, Color Accent, Color Success,
                      Color Critical, Color HoverFill, Color HoverStroke)
{
    /// Windows 11 (Fluent / WinUI) color tokens.
    public static Palette Windows11(SystemTheme t) => t.Light
        ? new(Primary:     Color.FromArgb(228, 0, 0, 0),
              Secondary:   Color.FromArgb(155, 0, 0, 0),
              Track:       Color.FromArgb(30, 0, 0, 0),
              Accent:      t.Accent,
              Success:     Color.FromArgb(15, 123, 15),
              Critical:    Color.FromArgb(196, 43, 28),
              HoverFill:   Color.FromArgb(150, 255, 255, 255),
              HoverStroke: Color.FromArgb(18, 0, 0, 0))
        : new(Primary:     Color.FromArgb(255, 255, 255, 255),
              Secondary:   Color.FromArgb(200, 255, 255, 255),
              Track:       Color.FromArgb(40, 255, 255, 255),
              Accent:      t.Accent,
              Success:     Color.FromArgb(108, 203, 95),
              Critical:    Color.FromArgb(255, 153, 164),
              HoverFill:   Color.FromArgb(22, 255, 255, 255),
              HoverStroke: Color.FromArgb(14, 255, 255, 255));

    /// Classic colors: green ring, red when low. No hover highlight.
    public static Palette Classic(SystemTheme t)
    {
        bool light = t.Light;
        Color green = light ? Color.FromArgb(52, 199, 89) : Color.FromArgb(48, 209, 88);
        Color red   = light ? Color.FromArgb(255, 59, 48) : Color.FromArgb(255, 69, 58);
        return new(
            Primary:     light ? Color.FromArgb(217, 0, 0, 0) : Color.FromArgb(242, 255, 255, 255),
            Secondary:   light ? Color.FromArgb(128, 0, 0, 0) : Color.FromArgb(140, 255, 255, 255),
            Track:       light ? Color.FromArgb(28, 0, 0, 0)  : Color.FromArgb(38, 255, 255, 255),
            Accent: green, Success: green, Critical: red,
            HoverFill: Color.Transparent, HoverStroke: Color.Transparent);
    }
}
