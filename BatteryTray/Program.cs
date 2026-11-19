using Microsoft.Win32;

namespace BatteryTray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--test")
        {
            // Write one reading per device to a file, then exit (for troubleshooting).
            var lines = Devices.All.Select(d =>
            {
                try { var r = d.Read(); return $"{d.Name}: {r.Percent?.ToString() ?? "-"} | {r.Detail}"; }
                catch (Exception ex) { return $"{d.Name}: ERROR {ex.Message}"; }
            });
            File.WriteAllLines(args[1], lines);
            return;
        }

        using var mutex = new Mutex(true, "BatteryTray_SingleInstance", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        // Never show a crash dialog (the widget lives inside Explorer's taskbar): log and carry on.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { ErrorLog.Write(e.Exception); e.SetObserved(); };
        // Refreshes are async methods on the UI thread: make their continuations come back to it.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new BatteryApp());
    }
}

/// Appends unexpected errors to %LOCALAPPDATA%\BatteryTray\errors.log (kept small).
static class ErrorLog
{
    static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryTray", "errors.log");

    public static void Write(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            if (File.Exists(PathName) && new FileInfo(PathName).Length > 256 * 1024) File.Delete(PathName);
            File.AppendAllText(PathName, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }
}

/// Owns the devices, polls them on the user-chosen interval, and keeps the taskbar widget alive.
class BatteryApp : ApplicationContext
{
    /// Quick = re-check only devices that need it often (see <see cref="InQuickPass"/>), redrawing only on change.
    enum RefreshKind { None, Quick, Full }

    readonly List<DeviceStatus> devices = Devices.All.Select(d => new DeviceStatus(d)).ToList();
    readonly DeviceReader reader = new();

    readonly System.Windows.Forms.Timer quickTimer = new() { Interval = 20_000 };
    readonly System.Windows.Forms.Timer pollTimer = new() { Interval = Settings.RefreshMinutes * 60_000 };
    readonly System.Windows.Forms.Timer watchdog = new() { Interval = 3000 };
    readonly ContextMenuStrip menu;
    Skin skin = Skin.For(Settings.Look);
    TaskbarWidget? widget;
    bool refreshing, closing;
    RefreshKind pending; // strongest refresh requested while one was running

    public BatteryApp()
    {
        menu = BuildMenu();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        pollTimer.Tick += (_, _) => Refresh(RefreshKind.Full);
        quickTimer.Tick += (_, _) => Refresh(RefreshKind.Quick);
        watchdog.Tick += (_, _) =>
        {
            try { EnsureWidget(); }
            catch (Exception ex) { ErrorLog.Write(ex); }
        };
        quickTimer.Start();
        pollTimer.Start();
        watchdog.Start();
        Autostart.Migrate();
        EnsureWidget();
        Refresh(RefreshKind.Full);
    }

    /// (Re)creates the widget when Explorer starts late or restarts, and keeps it placed next to the weather.
    void EnsureWidget()
    {
        if (widget is null || widget.IsDisposed || !widget.IsAttached)
        {
            widget?.Dispose();
            widget = null;
            var w = new TaskbarWidget(devices, skin, menu, () => Refresh(RefreshKind.Full));
            if (w.TryAttach()) widget = w;
            else w.Dispose();
        }
        widget?.Reposition();
    }

    // Raised on this (UI) thread, since it subscribed here.
    void OnThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        SystemTheme.Invalidate();
        widget?.Redraw();
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (_, _) => Refresh(RefreshKind.Full));

        var every = new ToolStripMenuItem("Auto-refresh every");
        foreach (int min in Settings.RefreshChoices)
        {
            var opt = new ToolStripMenuItem(min == 1 ? "1 minute" : $"{min} minutes") { Tag = min };
            opt.Click += (_, _) => SetInterval(min);
            every.DropDownItems.Add(opt);
        }
        menu.Opening += (_, _) =>
        {
            int cur = pollTimer.Interval / 60_000;
            foreach (ToolStripMenuItem o in every.DropDownItems) o.Checked = (int)o.Tag! == cur;
        };
        menu.Items.Add(every);

        var autostart = new ToolStripMenuItem("Start with Windows");
        autostart.Click += (_, _) => Autostart.Set(!Autostart.IsEnabled);
        menu.Opening += (_, _) => autostart.Checked = Autostart.IsEnabled;
        menu.Items.Add(autostart);

        var style = new ToolStripMenuItem("Style");
        foreach (var s in Skin.All)
        {
            var opt = new ToolStripMenuItem(s.Name) { Tag = s };
            opt.Click += (_, _) => SetSkin(s);
            style.DropDownItems.Add(opt);
        }
        menu.Opening += (_, _) =>
        {
            foreach (ToolStripMenuItem o in style.DropDownItems) o.Checked = o.Tag is Skin s && s == skin;
        };
        menu.Items.Add(style);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        FluentMenu.Apply(menu);
        return menu;
    }

    /// Switches the widget look; remembered across restarts.
    void SetSkin(Skin s)
    {
        if (s == skin) return;
        skin = s;
        Settings.Look = s.Look;
        widget?.SetSkin(s);
    }

    /// Changes the auto-refresh period, remembers it, and restarts the countdown.
    void SetInterval(int minutes)
    {
        pollTimer.Stop();
        pollTimer.Interval = minutes * 60_000;
        pollTimer.Start();
        Settings.RefreshMinutes = minutes;
    }

    /// The quick pass covers cheap devices (Bluetooth), plus anything showing "Off"
    /// (e.g. a mouse that was asleep) so it comes back soon after waking.
    static bool InQuickPass(DeviceStatus d) => d.Device.QuickPoll || (d.Visible && d.Reading is { Percent: null });

    /// Runs on the UI thread. A refresh requested while another is running is queued
    /// (a full one wins over a quick one) and runs right after it.
    async void Refresh(RefreshKind kind)
    {
        if (closing) return;
        if (refreshing)
        {
            if (kind > pending) pending = kind;
            return;
        }
        refreshing = true;
        while (kind != RefreshKind.None && !closing)
        {
            // One failure must never stop future updates.
            try { await RefreshOnce(kind); }
            catch (Exception ex) { ErrorLog.Write(ex); }
            (kind, pending) = (pending, RefreshKind.None);
        }
        refreshing = false;
    }

    async Task RefreshOnce(RefreshKind kind)
    {
        var targets = kind == RefreshKind.Full ? devices : devices.Where(InQuickPass).ToList();
        var readings = await Task.WhenAll(targets.Select(t => reader.Read(t.Device)));
        bool changed = false;
        for (int i = 0; i < targets.Count; i++)
        {
            changed |= targets[i].Reading != readings[i];
            targets[i].Reading = readings[i];
        }
        if (changed || kind == RefreshKind.Full) widget?.Redraw();
    }

    void Exit()
    {
        closing = true;
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        pollTimer.Stop();
        quickTimer.Stop();
        watchdog.Stop();
        widget?.Dispose();
        widget = null;
        menu.Dispose();
        ExitThread();
    }
}

/// Reads devices off the UI thread, each on its own (the devices are independent, so a refresh
/// takes as long as the slowest one, not the sum). Gives up after <see cref="Timeout"/> so a wedged
/// dongle can't freeze updates; a read that is still stuck from last time is not started again.
sealed class DeviceReader
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    readonly Dictionary<IBatteryDevice, Task<BatteryReading>> inFlight = []; // UI thread only

    public async Task<BatteryReading> Read(IBatteryDevice device)
    {
        if (inFlight.TryGetValue(device, out var stuck) && !stuck.IsCompleted)
            return BatteryReading.Failed("Not responding");

        var task = Task.Run(device.Read);
        inFlight[device] = task;
        try { return await task.WaitAsync(Timeout); }
        catch (TimeoutException)
        {
            // If the abandoned read fails later, observe it so it doesn't end up in the error log.
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return BatteryReading.Failed("Not responding (timed out)");
        }
        catch (Exception ex) { return BatteryReading.Failed("Error: " + ex.Message); }
    }
}
