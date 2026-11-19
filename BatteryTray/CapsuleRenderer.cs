using System.Drawing;
using System.Drawing.Drawing2D;

namespace BatteryTray;

/// "Modern" look: one glassy capsule per device with the icon, the percentage and a short
/// gradient level bar (cyan to violet normally, lime to emerald while charging, amber to rose when low).
sealed class CapsuleRenderer : ILookRenderer
{
    // Sizes in px at 100% scale.
    const float ChipH = 30, PadL = 10, PadR = 12, Icon = 14, IconGap = 8;
    const float ChipGap = 6, BarW = 34, BarH = 3.5f, BarGap = 4, Outer = 4, BoltPx = 8.5f;

    sealed record CapsuleColors(Color Primary, Color Dim, Color ChipFill, Color ChipEdge, Color BarTrack,
                         (Color, Color) Normal, (Color, Color) Charging, (Color, Color) Low)
    {
        public static CapsuleColors For(SystemTheme theme, bool hover) => theme.Light
            ? new(Primary:  Color.FromArgb(235, 15, 15, 25),
                  Dim:      Color.FromArgb(120, 15, 15, 25),
                  ChipFill: Color.FromArgb(hover ? 170 : 115, 255, 255, 255),
                  ChipEdge: Color.FromArgb(22, 0, 0, 0),
                  BarTrack: Color.FromArgb(26, 0, 0, 0),
                  Normal:   (Color.FromArgb(8, 145, 178), Color.FromArgb(124, 58, 237)),
                  Charging: (Color.FromArgb(101, 163, 13), Color.FromArgb(5, 150, 105)),
                  Low:      (Color.FromArgb(217, 119, 6), Color.FromArgb(225, 29, 72)))
            : new(Primary:  Color.FromArgb(255, 255, 255, 255),
                  Dim:      Color.FromArgb(130, 255, 255, 255),
                  ChipFill: Color.FromArgb(hover ? 36 : 20, 255, 255, 255),
                  ChipEdge: Color.FromArgb(26, 255, 255, 255),
                  BarTrack: Color.FromArgb(34, 255, 255, 255),
                  Normal:   (Color.FromArgb(34, 211, 238), Color.FromArgb(167, 139, 250)),
                  Charging: (Color.FromArgb(163, 230, 53), Color.FromArgb(16, 185, 129)),
                  Low:      (Color.FromArgb(251, 191, 36), Color.FromArgb(244, 63, 94)));
    }

    /// Width of each capsule: icon, then the value or the bar, whichever is wider.
    static List<float> ChipWidths(IReadOnlyList<ItemView> items, ValueText text, float s) =>
        items.Select(v => PadL * s + Icon * s + IconGap * s + Math.Max(text.Width(v), BarW * s) + PadR * s).ToList();

    public Size Measure(IReadOnlyList<ItemView> items, float s)
    {
        using var text = new ValueText(s, BoltPx);
        var widths = ChipWidths(items, text, s);
        float total = Outer * s * 2 + widths.Sum() + Math.Max(widths.Count - 1, 0) * (ChipGap * s);
        return new Size((int)Math.Ceiling(Math.Max(total, 1)), WidgetPainter.FrameHeight(s));
    }

    public void Paint(Graphics g, Size size, IReadOnlyList<ItemView> items, float s, bool hover, SystemTheme theme)
    {
        var c = CapsuleColors.For(theme, hover);
        int h = size.Height;
        float chipH = ChipH * s, padL = PadL * s, icon = Icon * s, iconGap = IconGap * s;
        float barW = BarW * s, barH = BarH * s, barGap = BarGap * s;
        using var text = new ValueText(s, BoltPx);
        var widths = ChipWidths(items, text, s);

        // ink of the digits + gap + bar, centered as one block
        float valueH = text.Digits.Height;
        float top = (h - (valueH + barGap + barH)) / 2;

        float x = Outer * s, cy = (h - chipH) / 2;
        for (int i = 0; i < items.Count; i++)
        {
            var v = items[i];
            float w = widths[i];

            var cr = new RectangleF(x + 0.5f, cy + 0.5f, w - 1, chipH - 1);
            using (var cp = WidgetPainter.RoundedRect(cr, chipH / 2 - 0.5f))
            {
                using (var cb = new SolidBrush(c.ChipFill)) g.FillPath(cb, cp);
                using (var ce = new Pen(c.ChipEdge, 1)) g.DrawPath(ce, cp);
            }

            var (g1, g2) = v.Low ? c.Low : v.Charging ? c.Charging : c.Normal;

            using (var ib = new SolidBrush(v.Off ? c.Dim : c.Primary))
                WidgetPainter.DrawIconCentered(g, v.Glyph, new PointF(x + padL + icon / 2, h / 2f), icon, ib, outline: icon * 0.036f);

            // value (+ bolt when charging), level bar under it
            float tx = x + padL + icon + iconGap;
            using (var vb = new SolidBrush(v.Off ? c.Dim : v.Low ? g2 : c.Primary))
                text.Draw(g, v, new PointF(tx, top - text.Digits.Y), vb);
            if (v.Charging)
            {
                using var bb = new SolidBrush(g1);
                text.DrawBolt(g, v, tx, top + valueH / 2, bb);
            }

            var track = new RectangleF(tx, top + valueH + barGap, barW, barH);
            using (var tp = WidgetPainter.RoundedRect(track, barH / 2))
            using (var tb = new SolidBrush(c.BarTrack)) g.FillPath(tb, tp);
            if (v.Percent is int pct && pct > 0)
            {
                var fill = new RectangleF(track.X, track.Y, Math.Max(barW * Math.Min(pct, 100) / 100f, barH), barH);
                using var fp = WidgetPainter.RoundedRect(fill, barH / 2);
                using var lg = new LinearGradientBrush(new RectangleF(track.X - 1, track.Y, barW + 2, barH), g1, g2, 0f);
                g.FillPath(lg, fp);
            }

            x += w + ChipGap * s;
        }
    }
}
