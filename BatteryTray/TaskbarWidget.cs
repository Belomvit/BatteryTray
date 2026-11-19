using System.Drawing;
using System.Runtime.InteropServices;

namespace BatteryTray;

/// A small transparent window embedded in the Windows 11 taskbar, placed just right of the
/// Widgets (weather) button. Shows each visible device as drawn by the current skin.
class TaskbarWidget : Form
{
    readonly IReadOnlyList<DeviceStatus> devices;
    readonly ToolTip tip = new() { InitialDelay = 300 };
    IntPtr taskbar;
    WeatherAnchor? anchor;
    Skin skin;
    bool hover;
    Size size = new(1, 1);
    Point? lastPos;     // null until placed, and after a resize so the next Reposition applies it
    string lastTip = "";

    public TaskbarWidget(IReadOnlyList<DeviceStatus> devices, Skin skin, ContextMenuStrip menu, Action refresh)
    {
        this.devices = devices;
        this.skin = skin;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ContextMenuStrip = menu;
        MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) refresh(); };
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
    }

    public void SetSkin(Skin value)
    {
        skin = value;
        Redraw();
    }

    void SetHover(bool on)
    {
        hover = on;
        if (skin.HoverEffect) Redraw();
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
        anchor = new WeatherAnchor(taskbar);
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

    /// Places the widget right after the Widgets button (see <see cref="WeatherAnchor"/>).
    public void Reposition()
    {
        if (!IsAttached || anchor is null) return;
        GetWindowRect(taskbar, out var r);
        var tb = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (anchor.AnchorX(tb, DpiScale, placed: lastPos is not null) is not int x) return;

        var pos = new Point(x - tb.Left, (tb.Height - size.Height) / 2);
        if (pos == lastPos) return;
        lastPos = pos;
        SetWindowPos(Handle, HWND_TOP, pos.X, pos.Y, size.Width, size.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// Renders the visible devices with the current skin and pushes the frame to the layered window.
    public void Redraw()
    {
        if (!IsAttached) return; // Explorer gone: no DPI to draw at until the watchdog re-attaches
        var views = devices.Where(d => d.Visible).Select(ItemView.From).ToList();
        using var frame = WidgetPainter.Render(skin.Renderer, views, DpiScale, hover && skin.HoverEffect, SystemTheme.Current);
        Present(frame, views);
    }

    /// Pushes a finished frame, updates the tooltip, and re-places the window if its size changed.
    void Present(Bitmap frame, IReadOnlyList<ItemView> views)
    {
        PushBitmap(frame);

        string tipText = string.Join("\n", views.Select(v => v.Tip)) + "\nLeft-click: refresh · Right-click: menu";
        if (tipText != lastTip) { tip.SetToolTip(this, tipText); lastTip = tipText; }

        if (frame.Size != size)
        {
            size = frame.Size;
            lastPos = null;
            Reposition();
        }
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
        if (disposing) tip.Dispose();
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
