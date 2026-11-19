using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BatteryTray;

/// Gives a context menu (and its submenus) the Windows 11 look, whatever the widget style:
/// rounded corners, light/dark surface, rounded hover pills, Fluent check marks and chevrons.
static class FluentMenu
{
    public static void Apply(ContextMenuStrip menu)
    {
        var renderer = new Renderer();
        var font = new Font("Segoe UI Variable Text", 10f);
        void Prepare(ToolStripDropDown dd)
        {
            dd.Renderer = renderer;
            dd.Font = font;
            dd.Padding = new Padding(0, 4, 0, 4);
            foreach (ToolStripItem it in dd.Items)
                if (it is not ToolStripSeparator)
                    it.Padding = new Padding(4, 5, 12, 5);
            // Back color follows the system light/dark theme, which can change while running.
            dd.Opening += (_, _) => dd.BackColor = MenuColors.Current.Back;
            dd.BackColor = MenuColors.Current.Back;
            dd.HandleCreated += (_, _) => RoundCorners(dd.Handle);
            foreach (ToolStripItem it in dd.Items)
                if (it is ToolStripMenuItem { HasDropDownItems: true } mi) Prepare(mi.DropDown);
        }
        Prepare(menu);
        menu.ShowImageMargin = true;
        menu.ShowCheckMargin = false;
    }

    static void RoundCorners(IntPtr hwnd)
    {
        int pref = 2; // DWMWCP_ROUND
        try { DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int)); }
        catch { /* older Windows: square corners */ }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    /// Windows 11 menu surface colors plus the Fluent text/accent tokens.
    sealed record MenuColors(Color Back, Color Hover, Color Divider, Palette Text)
    {
        public static MenuColors Current => For(SystemTheme.Current);

        static MenuColors For(SystemTheme t) => t.Light
            ? new(Color.FromArgb(249, 249, 249), Color.FromArgb(234, 234, 234), Color.FromArgb(225, 225, 225), Palette.Windows11(t))
            : new(Color.FromArgb(44, 44, 44), Color.FromArgb(56, 56, 56), Color.FromArgb(64, 64, 64), Palette.Windows11(t));
    }

    sealed class Renderer : ToolStripRenderer
    {
        const string CheckMark = "", ChevronRight = "";

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(MenuColors.Current.Back);
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
            using var p = WidgetPainter.RoundedRect(r, 4);
            using var b = new SolidBrush(MenuColors.Current.Hover);
            g.FillPath(b, p);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var text = MenuColors.Current.Text;
            e.TextColor = e.Item.Enabled ? text.Primary : text.Secondary;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(MenuColors.Current.Divider);
            e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // Accent check mark in the image column.
            DrawGlyph(e.Graphics, CheckMark, e.ImageRectangle, MenuColors.Current.Text.Accent, 12f);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            DrawGlyph(e.Graphics, ChevronRight, e.ArrowRectangle, MenuColors.Current.Text.Secondary, 9f);
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
