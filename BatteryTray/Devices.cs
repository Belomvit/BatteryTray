namespace BatteryTray;

/// Whether the device answered: Connected (the percentage may still be unknown),
/// Disconnected (off, asleep, unplugged, unpaired), or Error (the read failed or hung).
enum Link { Connected, Disconnected, Error }

record BatteryReading(Link Link, int? Percent, bool Charging, string Detail)
{
    public static BatteryReading Level(int percent, bool charging, string detail) => new(Link.Connected, percent, charging, detail);
    public static BatteryReading Disconnected(string detail) => new(Link.Disconnected, null, false, detail);
    public static BatteryReading Failed(string detail) => new(Link.Error, null, false, detail);
    public static BatteryReading Unknown(string detail) => new(Link.Connected, null, false, detail);
}

interface IBatteryDevice
{
    string Name { get; }
    /// Segoe Fluent Icons glyph shown for the device.
    string Glyph { get; }
    /// True for cheap reads that should be checked often (so the device appears soon after connecting).
    bool QuickPoll => false;
    /// True to leave the device off the taskbar while it isn't connected (e.g. a controller that's put away).
    bool HideWhenDisconnected => false;
    BatteryReading Read();
}

static class Devices
{
    public static IReadOnlyList<IBatteryDevice> All { get; } = [new JblQuantum350(), new AtkMouse(), new XboxController()];
}

/// A device and its latest reading, as the widget shows it.
sealed class DeviceStatus(IBatteryDevice device)
{
    public IBatteryDevice Device { get; } = device;
    BatteryReading? reading;
    Link? lastKnownLink; // from the last read that didn't fail: an error or hang doesn't say whether it's connected

    /// Null until the first read finishes.
    public BatteryReading? Reading
    {
        get => reading;
        set
        {
            reading = value;
            if (value is { Link: not Link.Error }) lastKnownLink = value.Link;
        }
    }

    /// Hidden only for devices that ask for it, while they aren't known to be connected.
    public bool Visible => !(Device.HideWhenDisconnected && Reading is not null && lastKnownLink is not Link.Connected);
}

/// JBL Quantum350 Wireless, 2.4 GHz dongle 0ECB:206B.
/// Feature 0x68 = headset link (01 = connected), feature 0x49 = battery % (5% steps).
class JblQuantum350 : IBatteryDevice
{
    public string Name => "JBL Quantum350";
    public string Glyph => ""; // headphones

    public BatteryReading Read()
    {
        using var dev = Hid.Open(0x0ECB, 0x206B, 0x0001);
        if (dev is null) return BatteryReading.Disconnected("Dongle not plugged in");
        int len = Math.Max((int)dev.Caps.FeatureReportByteLength, 2);
        var link = Hid.GetFeature(dev.Handle, 0x68, len);
        if (link[1] != 0x01) return BatteryReading.Disconnected("Headset off / not connected");
        var bat = Hid.GetFeature(dev.Handle, 0x49, len);
        int pct = bat[1];
        if (pct > 100) return BatteryReading.Failed($"Unexpected value {pct}");
        return BatteryReading.Level(pct, false, $"{pct}%");
    }
}

/// ATK X1 Ultimate (Compx chipset), 8K dongle 373B:11D9.
/// 17-byte output/input report 0x08 on usage page 0xFF02; command 0x04 = power info.
/// Reply: [6] = level %, [7] = charging, [8..9] = millivolts (big endian).
class AtkMouse : IBatteryDevice
{
    public string Name => "ATK X1 Ultimate";
    public string Glyph => ""; // mouse

    public BatteryReading Read()
    {
        using var dev = Hid.Open(0x373B, 0x11D9, 0xFF02, overlapped: true);
        if (dev is null) return BatteryReading.Disconnected("Dongle not plugged in");
        using var fs = dev.OpenStream();
        int len = dev.Caps.OutputReportByteLength; // 17

        var req = new byte[len];
        req[0] = 0x08; req[1] = 0x04;
        req[len - 1] = (byte)(0x55 - req.Take(len - 1).Sum(b => b));
        var write = fs.WriteAsync(req).AsTask();
        if (!write.Wait(1000)) return BatteryReading.Failed("Dongle not responding (write timed out)");
        write.GetAwaiter().GetResult(); // surfaces a failed write as its own exception

        var buf = new byte[dev.Caps.InputReportByteLength];
        var deadline = DateTime.UtcNow.AddMilliseconds(1500);
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
            int n;
            try { n = fs.ReadAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { break; }
            if (n < 10 || buf[0] != 0x08 || buf[1] != 0x04) continue;
            int pct = buf[6];
            bool charging = buf[7] != 0;
            int mv = (buf[8] << 8) | buf[9];
            if (pct == 0 && mv == 0) return BatteryReading.Disconnected("Mouse asleep / off");
            return BatteryReading.Level(pct, charging, $"{pct}%{(charging ? " (charging)" : "")}, {mv / 1000.0:0.00} V");
        }
        return BatteryReading.Disconnected("Mouse asleep / no reply");
    }
}
