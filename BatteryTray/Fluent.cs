using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BatteryTray;

enum Look { Windows11, Classic, Modern }

/// Windows 11 (Fluent / WinUI) color tokens for the current light/dark mode and accent color.
sealed record Fluent(bool Light, Color Primary, Color Secondary, Color Track, Color Accent,
                     Color Success, Color Critical, Color HoverFill, Color HoverStroke,
                     Color MenuBack, Color MenuHover, Color MenuDivider)
{
    const string SettingsKey = @"Software\BatteryTray";

    /// The widget's look, chosen from the right-click menu and remembered in the registry.
    public static Look Look { get; private set; } = LoadLook();
    /// Classic = the original macOS Batteries look.
    public static bool Classic => Look == Look.Classic;

    public static void SetLook(Look look)
    {
        Look = look;
        cached = null;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
            k.SetValue("Style", look.ToString());
        }
        catch { /* not critical */ }
    }

    static Look LoadLook()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (Enum.TryParse<Look>(k?.GetValue("Style") as string, out var l)) return l;
        }
        catch { }
        return Look.Windows11;
    }

    static Fluent? cached;
    public static Fluent Current => cached ??= Classic ? LoadClassicPalette() : Load();
    public static void Invalidate() => cached = null;

    static bool SystemLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// macOS system colors (dark / light appearance): green ring, red when low.
    static Fluent LoadClassicPalette()
    {
        bool light = SystemLight();
        Color green = light ? Color.FromArgb(52, 199, 89) : Color.FromArgb(48, 209, 88);
        Color red   = light ? Color.FromArgb(255, 59, 48) : Color.FromArgb(255, 69, 58);
        return new(light,
            Primary:     light ? Color.FromArgb(217, 0, 0, 0) : Color.FromArgb(242, 255, 255, 255),
            Secondary:   light ? Color.FromArgb(128, 0, 0, 0) : Color.FromArgb(140, 255, 255, 255),
            Track:       light ? Color.FromArgb(28, 0, 0, 0)  : Color.FromArgb(38, 255, 255, 255),
            Accent: green, Success: green, Critical: red,
            HoverFill: Color.Transparent, HoverStroke: Color.Transparent,
            MenuBack: SystemColors.Menu, MenuHover: SystemColors.Highlight, MenuDivider: SystemColors.ControlDark);
    }

    static Fluent Load()
    {
        bool light = SystemLight();

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

        return light
            ? new(true,
                  Primary:     Color.FromArgb(228, 0, 0, 0),
                  Secondary:   Color.FromArgb(155, 0, 0, 0),
                  Track:       Color.FromArgb(30, 0, 0, 0),
                  Accent:      accent,
                  Success:     Color.FromArgb(15, 123, 15),
                  Critical:    Color.FromArgb(196, 43, 28),
                  HoverFill:   Color.FromArgb(150, 255, 255, 255),
                  HoverStroke: Color.FromArgb(18, 0, 0, 0),
                  MenuBack:    Color.FromArgb(249, 249, 249),
                  MenuHover:   Color.FromArgb(234, 234, 234),
                  MenuDivider: Color.FromArgb(225, 225, 225))
            : new(false,
                  Primary:     Color.FromArgb(255, 255, 255, 255),
                  Secondary:   Color.FromArgb(200, 255, 255, 255),
                  Track:       Color.FromArgb(40, 255, 255, 255),
                  Accent:      accent,
                  Success:     Color.FromArgb(108, 203, 95),
                  Critical:    Color.FromArgb(255, 153, 164),
                  HoverFill:   Color.FromArgb(22, 255, 255, 255),
                  HoverStroke: Color.FromArgb(14, 255, 255, 255),
                  MenuBack:    Color.FromArgb(44, 44, 44),
                  MenuHover:   Color.FromArgb(56, 56, 56),
                  MenuDivider: Color.FromArgb(64, 64, 64));
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// Gives a context menu (and its submenus) the Windows 11 look: rounded corners,
    /// light/dark surface, rounded hover pills, Fluent check marks and chevrons.
    public static void Style(ContextMenuStrip menu)
    {
        var fluent = new MenuRenderer();
        var classic = new ToolStripProfessionalRenderer();
        var fluentFont = new Font("Segoe UI Variable Text", 10f);
        var classicFont = SystemFonts.MenuFont ?? Control.DefaultFont;
        void Prepare(ToolStripDropDown dd)
        {
            void Apply()
            {
                bool win11 = !Classic;
                dd.Renderer = win11 ? fluent : classic;
                dd.Font = win11 ? fluentFont : classicFont;
                dd.Padding = win11 ? new Padding(0, 4, 0, 4) : new Padding(0, 2, 0, 2);
                if (win11) dd.BackColor = Current.MenuBack; else dd.ResetBackColor();
                foreach (ToolStripItem it in dd.Items)
                    if (it is not ToolStripSeparator)
                        it.Padding = win11 ? new Padding(4, 5, 12, 5) : Padding.Empty;
                if (dd.IsHandleCreated) RoundCorners(dd.Handle, win11);
            }
            dd.HandleCreated += (_, _) => RoundCorners(dd.Handle, !Classic);
            dd.Opening += (_, _) => Apply();
            Apply();
            foreach (ToolStripItem it in dd.Items)
                if (it is ToolStripMenuItem { HasDropDownItems: true } mi) Prepare(mi.DropDown);
        }
        Prepare(menu);
        menu.ShowImageMargin = true;
        menu.ShowCheckMargin = false;
    }

    static void RoundCorners(IntPtr hwnd, bool round)
    {
        int pref = round ? 2 : 1; // DWMWCP_ROUND : DWMWCP_DONOTROUND
        try { DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int)); }
        catch { /* older Windows: square corners */ }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    sealed class MenuRenderer : ToolStripRenderer
    {
        static Fluent T => Current;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(T.MenuBack);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // DWM draws the rounded border.
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using var p = RoundedRect(r, 4);
            using var b = new SolidBrush(T.MenuHover);
            g.FillPath(b, p);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? T.Primary : T.Secondary;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(T.MenuDivider);
            e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // Accent check mark in the image column.
            DrawGlyph(e.Graphics, "", e.ImageRectangle, T.Accent, 12f);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            DrawGlyph(e.Graphics, "", e.ArrowRectangle, T.Secondary, 9f);
        }

        static void DrawGlyph(Graphics g, string glyph, Rectangle r, Color c, float px)
        {
            float dpi = g.DpiX / 96f;
            using var f = new Font("Segoe Fluent Icons", px * dpi, GraphicsUnit.Pixel);
            using var b = new SolidBrush(c);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString(glyph, f, b, r, sf);
        }
    }
}
