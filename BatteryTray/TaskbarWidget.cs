using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Win32;

namespace BatteryTray;

class WidgetItem(IBatteryDevice device, string glyph, string label)
{
    public IBatteryDevice Device { get; } = device;
    public string Glyph { get; } = glyph;
    public string Label { get; } = label;
    public BatteryReading? Reading { get; set; }
    /// The last device read started; used to avoid piling up reads on a device that hangs.
    public Task<BatteryReading>? InFlight { get; set; }
}

/// A small transparent window embedded in the Windows 11 taskbar, placed just right of the
/// Widgets (weather) button. Shows a glyph and the percentage for each device.
class TaskbarWidget : Form
{
    readonly List<WidgetItem> items;
    readonly ToolTip tip = new() { InitialDelay = 300 };
    readonly Action refresh;
    IntPtr taskbar;
    Size size = new(1, 1);
    Point lastPos = new(-1, -1);
    string lastTip = "";

    public TaskbarWidget(List<WidgetItem> items, ContextMenuStrip menu, Action refresh)
    {
        this.items = items;
        this.refresh = refresh;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ContextMenuStrip = menu;
        MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) this.refresh(); };
        MouseEnter += (_, _) => { hover = true; if (!Fluent.Classic) Redraw(); };
        MouseLeave += (_, _) => { hover = false; if (!Fluent.Classic) Redraw(); };
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
    }

    void OnThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            BeginInvoke(() => { Fluent.Invalidate(); Redraw(); });
    }

    public bool IsAttached => taskbar != IntPtr.Zero && IsWindow(taskbar)
                              && FindWindow("Shell_TrayWnd", null) == taskbar && IsHandleCreated;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public bool TryAttach()
    {
        taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return false;
        CreateHandle();
        int style = GetWindowLong(Handle, GWL_STYLE);
        style = (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME)) | WS_CHILD | WS_VISIBLE;
        SetWindowLong(Handle, GWL_STYLE, style);
        SetParent(Handle, taskbar);
        Redraw();
        Reposition();
        ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
        return true;
    }

    float DpiScale => GetDpiForWindow(taskbar) / 96f;

    AutomationElement? widgetsButton;  // cached UI Automation element of the weather button
    int? lastGoodX;                    // last trusted screen X right of the weather button
    int misses;                        // consecutive failed lookups
    const int MissesBeforeFallback = 5; // ~15 s at the 3 s watchdog: weather is really turned off
    DateTime nextSearch;               // when a full taskbar search may run again after finding nothing
    static readonly TimeSpan SearchBackoff = TimeSpan.FromSeconds(60);

    /// Screen X of the weather button's right edge, or null if it can't be read right now.
    int? WeatherRight(RECT tb)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (widgetsButton == null)
                {
                    // A full search of Explorer's UI is expensive; if the weather widget is turned off,
                    // don't repeat it every 3 s — try again once a minute.
                    if (DateTime.UtcNow < nextSearch) return null;
                    widgetsButton = AutomationElement.FromHandle(taskbar).FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
                    if (widgetsButton == null) { nextSearch = DateTime.UtcNow + SearchBackoff; return null; }
                }
                var r = widgetsButton.Current.BoundingRectangle;
                // Ignore transient rects while Explorer animates (empty, collapsed, or outside the taskbar).
                bool sane = !r.IsEmpty && r.Width >= 30 * DpiScale && r.Left >= tb.Left - 1 && r.Right <= tb.Right
                            && r.Top >= tb.Top - 1 && r.Bottom <= tb.Bottom + 1;
                return sane ? (int)r.Right : null;
            }
            catch
            {
                widgetsButton = null; // stale element (Explorer rebuilt it): look it up again once
            }
        }
        return null;
    }

    /// Places the widget right after the Widgets button. If the button can't be read for a moment,
    /// stays where it was; only moves to the left edge if the button is gone for a while.
    public void Reposition()
    {
        if (!IsAttached) return;
        GetWindowRect(taskbar, out var tb);
        int tbHeight = tb.Bottom - tb.Top;

        int x;
        if (WeatherRight(tb) is int right)
        {
            x = right;
            lastGoodX = right;
            misses = 0;
        }
        else if (++misses < MissesBeforeFallback && lastGoodX is int keep)
            x = keep;
        else if (misses < MissesBeforeFallback && lastPos.X >= 0)
            return; // nothing trusted yet; don't move
        else
            x = tb.Left + (int)(8 * DpiScale); // weather widget turned off

        var pos = new Point(x - tb.Left, (tbHeight - size.Height) / 2);
        if (pos != lastPos)
        {
            lastPos = pos;
            SetWindowPos(Handle, HWND_TOP, pos.X, pos.Y, size.Width, size.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    // StringFormat.GenericTypographic returns a new object on every access; keep one.
    static readonly StringFormat Fmt = (StringFormat)StringFormat.GenericTypographic.Clone();

    bool hover;

    /// Renders the content into a per-pixel-alpha bitmap and pushes it to the layered window.
    /// Layout like the weather widget (icon, then value over caption): a thin Fluent progress
    /// ring in the system accent color with the device icon inside, and a rounded hover
    /// highlight like other taskbar buttons.
    public void Redraw()
    {
        if (!IsHandleCreated || taskbar == IntPtr.Zero) return;
        if (Fluent.Look == Look.Modern) { RedrawModern(); return; }
        float s = DpiScale;
        var t = Fluent.Current;

        bool classic = Fluent.Classic;
        // Sized like the temperature in the Windows weather widget; GraphicsPath can't reach the
        // variable font's Semibold instance, so the weight comes from Regular + a hairline outline.
        using var valueFont = new Font("Segoe UI Variable Text", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var boltFont  = new Font("Segoe Fluent Icons", 9f * s, GraphicsUnit.Pixel);
        var fmt = Fmt;
        const string Bolt = "";

        int h = (int)Math.Round(40 * s);
        float ring = (classic ? 26 : 24) * s, stroke = (classic ? 3.2f : 2.5f) * s;
        float pad = 8 * s, iconGap = 7 * s, between = (classic ? 18 : 14) * s, iconBox = (classic ? 12 : 11) * s;

        // No caption under the value: the icon in the ring identifies the device,
        // the bolt shows charging and "Off" replaces the number when it is disconnected.
        var parts = items.Where(it => it.Reading?.Hidden != true).Select(it =>
        {
            var r = it.Reading;
            string value = r?.Percent is int p ? $"{p}%" : r is null ? "…" : "Off";
            bool low = r?.Percent <= 20 && r?.Charging != true;
            Color c = r?.Percent is null ? t.Track : low ? t.Critical : r!.Charging ? t.Success : t.Accent;
            return (it, value, c, pct: r?.Percent, charging: r?.Charging == true && r.Percent is not null, low);
        }).ToList();

        using var probe = new Bitmap(1, 1);
        using var pg = Graphics.FromImage(probe);
        float TextWidth(string txt, Font f) => pg.MeasureString(txt, f, PointF.Empty, fmt).Width;
        float ValueWidth(string value, bool charging) =>
            TextWidth(value, valueFont) + (charging ? 3 * s + TextWidth(Bolt, boltFont) : 0);

        // Shared vertical placement: center the ink of the digits on the ring's center,
        // so every value sits on the same baseline regardless of its characters.
        RectangleF digits;
        using (var dp = TextPath("0%", valueFont, PointF.Empty)) digits = dp.GetBounds();
        float valueY = h / 2f - (digits.Y + digits.Height / 2);

        float total = pad * 2 - between;
        foreach (var p in parts)
            total += ring + iconGap + ValueWidth(p.value, p.charging) + between;
        size = new Size((int)Math.Ceiling(Math.Max(total, 1)), h);

        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.FromArgb(1, 0, 0, 0)); // nearly invisible, so the whole area takes clicks

            // taskbar-button style hover: soft rounded fill with a hairline edge
            if (hover && !classic)
            {
                var hr = new RectangleF(0.5f, 0.5f, size.Width - 1, size.Height - 1);
                using var hp = Fluent.RoundedRect(hr, 4 * s);
                using (var hb = new SolidBrush(t.HoverFill)) g.FillPath(hb, hp);
                using (var hs = new Pen(t.HoverStroke, 1)) g.DrawPath(hs, hp);
            }

            float x = pad;
            foreach (var p in parts)
            {
                // ring: faint full track + accent arc with round caps, starting at 12 o'clock
                float ry = (h - ring) / 2;
                var rr = new RectangleF(x + stroke / 2, ry + stroke / 2, ring - stroke, ring - stroke);
                using (var tp = new Pen(t.Track, stroke)) g.DrawEllipse(tp, rr);
                if (p.pct is int pct && pct > 0)
                {
                    using var ap = new Pen(p.c, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawArc(ap, rr, -90, Math.Max(Math.Min(pct, 100) * 3.6f, 1));
                }

                // device icon inside the ring
                using (var gb = new SolidBrush(p.pct is null ? t.Secondary : t.Primary))
                    DrawIconCentered(g, p.it.Glyph, new PointF(x + ring / 2, ry + ring / 2), iconBox, gb, outline: 0.15f * s);
                x += ring + iconGap;

                // value, vertically centered on the ring, drawn as a smooth vector outline
                using (var vb = new SolidBrush(p.pct is null ? t.Secondary : p.low ? t.Critical : t.Primary))
                using (var vp = TextPath(p.value, valueFont, new PointF(x, valueY)))
                using (var vpen = new Pen(vb, 0.15f * s) { LineJoin = LineJoin.Round })
                {
                    g.FillPath(vb, vp);
                    g.DrawPath(vpen, vp);
                }
                if (p.charging)
                {
                    float bx = x + TextWidth(p.value, valueFont) + 3 * s;
                    using var bp = TextPath(Bolt, boltFont, PointF.Empty);
                    var bb0 = bp.GetBounds();
                    using (var m = new Matrix()) { m.Translate(bx - bb0.X, h / 2f - (bb0.Y + bb0.Height / 2)); bp.Transform(m); }
                    using var bb = new SolidBrush(t.Success);
                    g.FillPath(bb, bp);
                }

                x += ValueWidth(p.value, p.charging) + between;
            }
        }
        Present(bmp, parts.Select(p => p.it));
    }

    Size lastPushed;

    /// Pushes a finished frame, updates the tooltip, and re-places the window if its size changed.
    void Present(Bitmap bmp, IEnumerable<WidgetItem> shown)
    {
        var oldSize = lastPushed;
        PushBitmap(bmp);
        lastPushed = size;

        string tipText = string.Join("\n", shown.Select(it => $"{it.Device.Name}: {it.Reading?.Detail ?? "checking…"}"))
                         + "\nLeft-click: refresh · Right-click: menu";
        if (tipText != lastTip) { tip.SetToolTip(this, tipText); lastTip = tipText; }
        if (size != oldSize)
        {
            lastPos = new Point(-1, -1);
            Reposition();
        }
    }

    /// "Modern" look: one glassy capsule per device with the icon, a bold percentage and a short
    /// gradient level bar (cyan to violet normally, lime to emerald while charging, amber to rose when low).
    void RedrawModern()
    {
        float s = DpiScale;
        bool light = Fluent.Current.Light;
        Color primary  = light ? Color.FromArgb(235, 15, 15, 25) : Color.FromArgb(255, 255, 255, 255);
        Color dim      = light ? Color.FromArgb(120, 15, 15, 25) : Color.FromArgb(130, 255, 255, 255);
        Color chipFill = light ? Color.FromArgb(hover ? 170 : 115, 255, 255, 255) : Color.FromArgb(hover ? 36 : 20, 255, 255, 255);
        Color chipEdge = light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255);
        Color barTrack = light ? Color.FromArgb(26, 0, 0, 0) : Color.FromArgb(34, 255, 255, 255);
        var normal   = light ? (Color.FromArgb(8, 145, 178), Color.FromArgb(124, 58, 237))
                             : (Color.FromArgb(34, 211, 238), Color.FromArgb(167, 139, 250));
        var charging = light ? (Color.FromArgb(101, 163, 13), Color.FromArgb(5, 150, 105))
                             : (Color.FromArgb(163, 230, 53), Color.FromArgb(16, 185, 129));
        var lowGrad  = light ? (Color.FromArgb(217, 119, 6), Color.FromArgb(225, 29, 72))
                             : (Color.FromArgb(251, 191, 36), Color.FromArgb(244, 63, 94));

        // Same weight as the Windows 11 / Classic looks: Regular + a hairline outline, as vector paths.
        using var valueFont = new Font("Segoe UI Variable Text", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var boltFont  = new Font("Segoe Fluent Icons", 8.5f * s, GraphicsUnit.Pixel);
        const string Bolt = "\uE945";
        var fmt = Fmt;

        int h = (int)Math.Round(40 * s);
        float chipH = 30 * s, padL = 10 * s, padR = 12 * s, icon = 14 * s, iconGap = 8 * s;
        float chipGap = 6 * s, barW = 34 * s, barH = 3.5f * s, outer = 4 * s;

        var shown = items.Where(it => it.Reading?.Hidden != true).ToList();

        using var probe = new Bitmap(1, 1);
        using var pg = Graphics.FromImage(probe);
        float TextWidth(string txt, Font f) => pg.MeasureString(txt, f, PointF.Empty, fmt).Width;

        RectangleF digits;
        using (var dp = TextPath("0%", valueFont, PointF.Empty)) digits = dp.GetBounds();

        var chips = shown.Select(it =>
        {
            var r = it.Reading;
            string value = r?.Percent is int p ? $"{p}%" : r is null ? "…" : "Off";
            bool chg = r?.Charging == true && r.Percent is not null;
            float vw = TextWidth(value, valueFont) + (chg ? 3 * s + TextWidth(Bolt, boltFont) : 0);
            float w = padL + icon + iconGap + Math.Max(vw, barW) + padR;
            return (it, r, value, chg, w);
        }).ToList();

        float total = outer * 2 + chips.Sum(c => c.w) + Math.Max(chips.Count - 1, 0) * chipGap;
        size = new Size((int)Math.Ceiling(Math.Max(total, 1)), h);

        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.FromArgb(1, 0, 0, 0)); // nearly invisible, so the whole area takes clicks

            float x = outer, cy = (h - chipH) / 2;
            foreach (var c in chips)
            {
                // capsule
                var cr = new RectangleF(x + 0.5f, cy + 0.5f, c.w - 1, chipH - 1);
                using (var cp = Fluent.RoundedRect(cr, chipH / 2 - 0.5f))
                {
                    using (var cb = new SolidBrush(chipFill)) g.FillPath(cb, cp);
                    using (var ce = new Pen(chipEdge, 1)) g.DrawPath(ce, cp);
                }

                bool off = c.r?.Percent is null;
                bool low = c.r?.Percent <= 20 && !c.chg;
                var (g1, g2) = low ? lowGrad : c.chg ? charging : normal;

                using (var ib = new SolidBrush(off ? dim : primary))
                    DrawIconCentered(g, c.it.Glyph, new PointF(x + padL + icon / 2, h / 2f), icon, ib);

                // value (+ bolt when charging), level bar under it
                float tx = x + padL + icon + iconGap;
                // ink of the digits + gap + bar, centered as one block
                float valueH = digits.Height, barGap = 4 * s;
                float top = (h - (valueH + barGap + barH)) / 2;
                using (var vb = new SolidBrush(off ? dim : low ? g2 : primary))
                using (var vp = TextPath(c.value, valueFont, new PointF(tx, top - digits.Y)))
                using (var vpen = new Pen(vb, 0.15f * s) { LineJoin = LineJoin.Round })
                {
                    g.FillPath(vb, vp);
                    g.DrawPath(vpen, vp);
                }
                if (c.chg)
                {
                    float bx = tx + TextWidth(c.value, valueFont) + 3 * s;
                    using var bp = TextPath(Bolt, boltFont, PointF.Empty);
                    var bb0 = bp.GetBounds();
                    using (var m = new Matrix()) { m.Translate(bx - bb0.X, top + valueH / 2 - (bb0.Y + bb0.Height / 2)); bp.Transform(m); }
                    using var bb = new SolidBrush(g1);
                    g.FillPath(bb, bp);
                }

                var track = new RectangleF(tx, top + valueH + barGap, barW, barH);
                using (var tp = Fluent.RoundedRect(track, barH / 2))
                using (var tb = new SolidBrush(barTrack)) g.FillPath(tb, tp);
                if (c.r?.Percent is int pct && pct > 0)
                {
                    var fill = new RectangleF(track.X, track.Y, Math.Max(barW * Math.Min(pct, 100) / 100f, barH), barH);
                    using var fp = Fluent.RoundedRect(fill, barH / 2);
                    using var lg = new LinearGradientBrush(new RectangleF(track.X - 1, track.Y, barW + 2, barH), g1, g2, 0f);
                    g.FillPath(lg, fp);
                }

                x += c.w + chipGap;
            }
        }
        Present(bmp, shown);
    }

    /// Text as a vector outline: anti-aliased like the icons, without the grid-fitting
    /// that makes small GDI+ digits look jagged.
    static GraphicsPath TextPath(string text, Font font, PointF origin)
    {
        var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, origin, Fmt);
        return path;
    }

    /// Draws an icon-font glyph as a vector outline, scaled so its actual ink fits in a
    /// box of <paramref name="box"/> px and centered optically on <paramref name="center"/>
    /// (font metrics/side bearings would otherwise push narrow glyphs like the mouse off-center).
    static void DrawIconCentered(Graphics g, string glyph, PointF center, float box, Brush brush, float? outline = null)
    {
        using var family = new FontFamily("Segoe Fluent Icons");
        using var path = new GraphicsPath();
        path.AddString(glyph, family, 0, 100f, PointF.Empty, Fmt);
        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;
        float k = box / Math.Max(b.Width, b.Height);
        using var m = new Matrix();
        m.Translate(center.X, center.Y);
        m.Scale(k, k);
        m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
        path.Transform(m);
        g.FillPath(brush, path);
        // Outline to thicken the thin Fluent strokes: a hairline (same as the value text) when
        // given, otherwise a bolder default for the Modern look.
        using var pen = new Pen(brush, outline ?? box * 0.036f) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
    }

    void PushBitmap(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var sz = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref sz, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnThemeChanged;
            tip.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- Win32 ----
    const int GWL_STYLE = -16;
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = unchecked((int)0x80000000),
              WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, IntPtr pos, ref SIZE sz, IntPtr src, ref POINT srcPos, int key, ref BLENDFUNCTION b, int flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
}
