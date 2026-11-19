using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace BatteryTray;

/// Drawing primitives shared by every look.
static class WidgetPainter
{
    // StringFormat.GenericTypographic returns a new object on every access; keep one.
    public static readonly StringFormat Fmt = (StringFormat)StringFormat.GenericTypographic.Clone();

    // Text is measured on the UI thread only, so one probe surface is enough.
    static readonly Graphics Probe = Graphics.FromImage(new Bitmap(1, 1));
    static readonly FontFamily IconFamily = new("Segoe Fluent Icons");

    /// Renders <paramref name="items"/> into a new per-pixel-alpha frame sized by the renderer.
    public static Bitmap Render(ILookRenderer renderer, IReadOnlyList<ItemView> items, float scale, bool hover, SystemTheme theme)
    {
        var size = renderer.Measure(items, scale);
        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.FromArgb(1, 0, 0, 0)); // nearly invisible, so the whole area takes clicks
        renderer.Paint(g, size, items, scale, hover, theme);
        return bmp;
    }

    /// Frame height: the taskbar button height.
    public static int FrameHeight(float scale) => (int)Math.Round(40 * scale);

    public static float TextWidth(string text, Font font) => Probe.MeasureString(text, font, PointF.Empty, Fmt).Width;

    /// Text as a vector outline: anti-aliased like the icons, without the grid-fitting
    /// that makes small GDI+ digits look jagged.
    public static GraphicsPath TextPath(string text, Font font, PointF origin)
    {
        var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, origin, Fmt);
        return path;
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

    /// Draws an icon-font glyph as a vector outline, scaled so its actual ink fits in a
    /// box of <paramref name="box"/> px and centered optically on <paramref name="center"/>
    /// (font metrics/side bearings would otherwise push narrow glyphs like the mouse off-center).
    /// <paramref name="outline"/> thickens the thin Fluent strokes.
    public static void DrawIconCentered(Graphics g, string glyph, PointF center, float box, Brush brush, float outline)
    {
        using var path = new GraphicsPath();
        path.AddString(glyph, IconFamily, 0, 100f, PointF.Empty, Fmt);
        var b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;
        float k = box / Math.Max(b.Width, b.Height);
        using var m = new Matrix();
        m.Translate(center.X, center.Y);
        m.Scale(k, k);
        m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
        path.Transform(m);
        g.FillPath(brush, path);
        using var pen = new Pen(brush, outline) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
    }
}

/// The percentage (and the charging bolt after it) as every look draws it: Segoe UI Variable
/// at the size of the temperature in the Windows weather widget. GraphicsPath can't reach the
/// variable font's Semibold instance, so the weight comes from Regular + a hairline outline.
sealed class ValueText : IDisposable
{
    const string Bolt = "";
    readonly float scale;
    readonly Font font, boltFont;

    /// Ink bounds of "0%" drawn at the origin: lets every value share one baseline,
    /// whatever its characters.
    public RectangleF Digits { get; }

    public ValueText(float scale, float boltPx)
    {
        this.scale = scale;
        font = new Font("Segoe UI Variable Text", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        boltFont = new Font("Segoe Fluent Icons", boltPx * scale, GraphicsUnit.Pixel);
        using var dp = WidgetPainter.TextPath("0%", font, PointF.Empty);
        Digits = dp.GetBounds();
    }

    /// Width of the value plus, when charging, the gap and the bolt.
    public float Width(ItemView v) =>
        WidgetPainter.TextWidth(v.Value, font) + (v.Charging ? 3 * scale + WidgetPainter.TextWidth(Bolt, boltFont) : 0);

    /// Draws the value with its top-left at <paramref name="origin"/>.
    public void Draw(Graphics g, ItemView v, PointF origin, Brush brush)
    {
        using var path = WidgetPainter.TextPath(v.Value, font, origin);
        using var pen = new Pen(brush, 0.15f * scale) { LineJoin = LineJoin.Round };
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }

    /// Draws the charging bolt right after the value that starts at <paramref name="valueX"/>,
    /// with its ink centered on <paramref name="centerY"/>.
    public void DrawBolt(Graphics g, ItemView v, float valueX, float centerY, Brush brush)
    {
        float bx = valueX + WidgetPainter.TextWidth(v.Value, font) + 3 * scale;
        using var bp = WidgetPainter.TextPath(Bolt, boltFont, PointF.Empty);
        var b = bp.GetBounds();
        using (var m = new Matrix()) { m.Translate(bx - b.X, centerY - (b.Y + b.Height / 2)); bp.Transform(m); }
        g.FillPath(brush, bp);
    }

    public void Dispose()
    {
        font.Dispose();
        boltFont.Dispose();
    }
}
