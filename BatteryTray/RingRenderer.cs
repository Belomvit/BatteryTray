using System.Drawing;
using System.Drawing.Drawing2D;

namespace BatteryTray;

/// Windows 11 and Classic looks, laid out like the weather widget: per device a thin progress
/// ring with the device icon inside, then the value. No caption: the icon identifies the device,
/// the bolt shows charging and "Off" replaces the number when it is disconnected.
sealed class RingRenderer(RingRenderer.Geometry geometry, Func<SystemTheme, Palette> palette) : ILookRenderer
{
    /// Sizes in px at 100% scale.
    public sealed record Geometry(float Ring, float Stroke, float Between, float IconBox);

    public static readonly Geometry Windows11 = new(Ring: 24, Stroke: 2.5f, Between: 14, IconBox: 11);
    /// Classic: a bolder ring, more spacing.
    public static readonly Geometry Classic = new(Ring: 26, Stroke: 3.2f, Between: 18, IconBox: 12);

    const float Pad = 8, IconGap = 7, BoltPx = 9;

    public Size Measure(IReadOnlyList<ItemView> items, float s)
    {
        float ring = geometry.Ring * s, between = geometry.Between * s;
        using var text = new ValueText(s, BoltPx);
        float total = Pad * s * 2 - between;
        foreach (var v in items)
            total += ring + IconGap * s + text.Width(v) + between;
        return new Size((int)Math.Ceiling(Math.Max(total, 1)), WidgetPainter.FrameHeight(s));
    }

    public void Paint(Graphics g, Size size, IReadOnlyList<ItemView> items, float s, bool hover, SystemTheme theme)
    {
        var t = palette(theme);
        int h = size.Height;
        float ring = geometry.Ring * s, stroke = geometry.Stroke * s, between = geometry.Between * s;
        float iconBox = geometry.IconBox * s, iconGap = IconGap * s;
        using var text = new ValueText(s, BoltPx);
        // Center the ink of the digits on the ring's center.
        float valueY = h / 2f - (text.Digits.Y + text.Digits.Height / 2);

        // taskbar-button style hover: soft rounded fill with a hairline edge
        if (hover)
        {
            var hr = new RectangleF(0.5f, 0.5f, size.Width - 1, size.Height - 1);
            using var hp = WidgetPainter.RoundedRect(hr, 4 * s);
            using (var hb = new SolidBrush(t.HoverFill)) g.FillPath(hb, hp);
            using (var hs = new Pen(t.HoverStroke, 1)) g.DrawPath(hs, hp);
        }

        float x = Pad * s;
        foreach (var v in items)
        {
            // ring: faint full track + colored arc with round caps, starting at 12 o'clock
            float ry = (h - ring) / 2;
            var rr = new RectangleF(x + stroke / 2, ry + stroke / 2, ring - stroke, ring - stroke);
            using (var tp = new Pen(t.Track, stroke)) g.DrawEllipse(tp, rr);
            if (v.Percent is int pct && pct > 0)
            {
                Color arc = v.Low ? t.Critical : v.Charging ? t.Success : t.Accent;
                using var ap = new Pen(arc, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(ap, rr, -90, Math.Max(Math.Min(pct, 100) * 3.6f, 1));
            }

            // device icon inside the ring
            using (var gb = new SolidBrush(v.Off ? t.Secondary : t.Primary))
                WidgetPainter.DrawIconCentered(g, v.Glyph, new PointF(x + ring / 2, ry + ring / 2), iconBox, gb, outline: 0.15f * s);
            x += ring + iconGap;

            using (var vb = new SolidBrush(v.Off ? t.Secondary : v.Low ? t.Critical : t.Primary))
                text.Draw(g, v, new PointF(x, valueY), vb);
            if (v.Charging)
            {
                using var bb = new SolidBrush(t.Success);
                text.DrawBolt(g, v, x, h / 2f, bb);
            }

            x += text.Width(v) + between;
        }
    }
}
