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
            var lines = new IBatteryDevice[] { new JblQuantum350(), new AtkMouse(), new XboxController() }.Select(d =>
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
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "BatteryTray";
    const string SettingsKey = @"Software\BatteryTray";
    static readonly int[] IntervalChoices = [1, 5, 10, 15, 20, 30];
    const int DefaultMinutes = 30;

    // Glyphs from Segoe Fluent Icons: headphones E7F6, mouse E962, game controller E7FC.
    readonly List<WidgetItem> items =
    [
        new(new JblQuantum350(), "", "Headset"),
        new(new AtkMouse(), "", "Mouse"),
        new(new XboxController(), "", "Controller"),
    ];

    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    readonly System.Windows.Forms.Timer quickTimer = new() { Interval = 20_000 };
    readonly System.Windows.Forms.Timer pollTimer = new() { Interval = LoadMinutes() * 60_000 };
    readonly System.Windows.Forms.Timer watchdog = new() { Interval = 3000 };
    readonly SynchronizationContext ui;
    TaskbarWidget? widget;
    int busy;                       // 1 while a refresh is running
    bool pendingFull, pendingQuick; // refreshes requested meanwhile (UI thread only)

    public BatteryApp()
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        pollTimer.Tick += (_, _) => Refresh();
        watchdog.Tick += (_, _) =>
        {
            try { EnsureWidget(); }
            catch (Exception ex) { ErrorLog.Write(ex); }
        };
        quickTimer.Tick += (_, _) => Refresh(quickOnly: true);
        quickTimer.Start();
        pollTimer.Start();
        watchdog.Start();
        MigrateAutostart();
        EnsureWidget();
        Refresh();
    }

    /// (Re)creates the widget when Explorer starts late or restarts, and keeps it placed next to the weather.
    void EnsureWidget()
    {
        if (widget is null || widget.IsDisposed || !widget.IsAttached)
        {
            widget?.Dispose();
            widget = null;
            var w = new TaskbarWidget(items, BuildMenu(), Refresh);
            if (w.TryAttach()) widget = w;
            else w.Dispose();
        }
        widget?.Reposition();
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, (_, _) => Refresh());

        var every = new ToolStripMenuItem("Auto-refresh every");
        foreach (int min in IntervalChoices)
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
        autostart.Click += (_, _) => SetAutostart(!IsAutostart());
        menu.Opening += (_, _) => autostart.Checked = IsAutostart();
        menu.Items.Add(autostart);

        var style = new ToolStripMenuItem("Style");
        foreach (var (look, name) in new[] { (Look.Windows11, "Windows 11"), (Look.Modern, "Modern"), (Look.Classic, "Classic (macOS)") })
        {
            var opt = new ToolStripMenuItem(name) { Tag = look };
            opt.Click += (_, _) => SetStyle(look);
            style.DropDownItems.Add(opt);
        }
        menu.Opening += (_, _) =>
        {
            foreach (ToolStripMenuItem o in style.DropDownItems) o.Checked = (Look)o.Tag! == Fluent.Look;
        };
        menu.Items.Add(style);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        Fluent.Style(menu);
        return menu;
    }

    /// Switches the widget look (Windows 11, Modern or Classic); remembered across restarts.
    void SetStyle(Look look)
    {
        if (Fluent.Look == look) return;
        Fluent.SetLook(look);
        widget?.Redraw();
    }

    /// Changes the auto-refresh period, remembers it, and restarts the countdown.
    void SetInterval(int minutes)
    {
        pollTimer.Stop();
        pollTimer.Interval = minutes * 60_000;
        pollTimer.Start();
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
            k.SetValue("RefreshMinutes", minutes, RegistryValueKind.DWord);
        }
        catch { /* not critical: falls back to the default next start */ }
    }

    static int LoadMinutes()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (k?.GetValue("RefreshMinutes") is int m && IntervalChoices.Contains(m)) return m;
        }
        catch { }
        return DefaultMinutes;
    }

    void Refresh() => Refresh(quickOnly: false);

    /// quickOnly: re-check only cheap devices (Bluetooth), redrawing only if something changed.
    /// A refresh requested while another is running is queued and runs right after it.
    void Refresh(bool quickOnly)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1)
        {
            if (quickOnly) pendingQuick = true; else pendingFull = true;
            return;
        }
        // Quick pass: cheap devices, plus anything showing "Off" (e.g. a mouse that was asleep) so it comes back soon.
        var targets = items.Where(it => !quickOnly || it.Device.QuickPoll
                                        || (it.Reading is { Percent: null, Hidden: false })).ToList();
        Task.Run(() =>
        {
            List<BatteryReading>? results = null;
            try { results = targets.Select(ReadWithTimeout).ToList(); }
            catch (Exception ex) { ErrorLog.Write(ex); }

            ui.Post(_ =>
            {
                try
                {
                    if (results is null) return;
                    bool changed = false;
                    for (int i = 0; i < targets.Count; i++)
                    {
                        if (targets[i].Reading != results[i]) changed = true;
                        targets[i].Reading = results[i];
                    }
                    if (changed || !quickOnly) widget?.Redraw();
                }
                catch (Exception ex) { ErrorLog.Write(ex); }
                finally
                {
                    // Always release, so one failure can never stop future updates.
                    Volatile.Write(ref busy, 0);
                    if (pendingFull) { pendingFull = pendingQuick = false; Refresh(quickOnly: false); }
                    else if (pendingQuick) { pendingQuick = false; Refresh(quickOnly: true); }
                }
            }, null);
        });
    }

    /// Reads one device, giving up after <see cref="ReadTimeout"/> so a wedged dongle can't
    /// freeze updates. A read that is still stuck from last time is not started again.
    static BatteryReading ReadWithTimeout(WidgetItem it)
    {
        bool keepHidden = it.Reading?.Hidden == true;
        if (it.InFlight is { IsCompleted: false })
            return new(null, false, "Not responding", Hidden: keepHidden);

        var task = Task.Run(it.Device.Read);
        it.InFlight = task;
        try
        {
            return task.Wait(ReadTimeout) ? task.Result : new(null, false, "Not responding (timed out)", Hidden: keepHidden);
        }
        catch (AggregateException ex)
        {
            return new(null, false, "Error: " + (ex.InnerException?.Message ?? ex.Message), Hidden: keepHidden);
        }
    }

    // Autostart is a shortcut in the user's Startup folder, so it shows up (and can be toggled)
    // in Task Manager / Settings > Startup apps. Windows records a toggle there in StartupApproved.
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    static readonly string ShortcutName = AppName + ".lnk";
    static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    static bool IsAutostart()
    {
        if (!File.Exists(ShortcutPath)) return false;
        try
        {
            // Odd first byte = switched off in Startup apps.
            using var k = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (k?.GetValue(ShortcutName) is byte[] { Length: > 0 } b && (b[0] & 1) == 1) return false;
        }
        catch { }
        return true;
    }

    static void SetAutostart(bool on)
    {
        try
        {
            RemoveLegacyRunEntry();
            if (on) CreateShortcut();
            else if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            // Clear any "disabled" flag so the Startup apps toggle matches.
            using var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            k?.DeleteValue(ShortcutName, false);
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    /// Older versions used the Run key, which Windows was silently skipping; move it to the shortcut.
    static void MigrateAutostart()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            if (k?.GetValue(AppName) is not string) return;
            RemoveLegacyRunEntry();
            if (!File.Exists(ShortcutPath)) CreateShortcut();
        }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    static void RemoveLegacyRunEntry()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue(AppName, false);
    }

    static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic lnk = shell.CreateShortcut(ShortcutPath);
        lnk.TargetPath = Environment.ProcessPath;
        lnk.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        lnk.Description = "Battery levels on the taskbar";
        lnk.Save();
    }

    void Exit()
    {
        pollTimer.Stop();
        quickTimer.Stop();
        watchdog.Stop();
        widget?.Dispose();
        ExitThread();
    }
}
