namespace BatteryTray;

/// Hidden = leave this device off the taskbar (e.g. a controller that isn't connected).
record BatteryReading(int? Percent, bool Charging, string Detail, bool Hidden = false);

interface IBatteryDevice
{
    string Name { get; }
    /// True for cheap reads that should be checked often (so the device appears soon after connecting).
    bool QuickPoll => false;
    BatteryReading Read();
}

/// JBL Quantum350 Wireless, 2.4 GHz dongle 0ECB:206B.
/// Feature 0x68 = headset link (01 = connected), feature 0x49 = battery % (5% steps).
class JblQuantum350 : IBatteryDevice
{
    public string Name => "JBL Quantum350";

    public BatteryReading Read()
    {
        var dev = Hid.Open(0x0ECB, 0x206B, 0x0001);
        if (dev is null) return new(null, false, "Dongle not plugged in");
        using var h = dev.Value.handle;
        int len = Math.Max((int)dev.Value.caps.FeatureReportByteLength, 2);
        var link = Hid.GetFeature(h, 0x68, len);
        if (link[1] != 0x01) return new(null, false, "Headset off / not connected");
        var bat = Hid.GetFeature(h, 0x49, len);
        int pct = bat[1];
        if (pct > 100) return new(null, false, $"Unexpected value {pct}");
        return new(pct, false, $"{pct}%");
    }
}

/// ATK X1 Ultimate (Compx chipset), 8K dongle 373B:11D9.
/// 17-byte output/input report 0x08 on usage page 0xFF02; command 0x04 = power info.
/// Reply: [6] = level %, [7] = charging, [8..9] = millivolts (big endian).
class AtkMouse : IBatteryDevice
{
    public string Name => "ATK X1 Ultimate";

    public BatteryReading Read()
    {
        var dev = Hid.Open(0x373B, 0x11D9, 0xFF02, overlapped: true);
        if (dev is null) return new(null, false, "Dongle not plugged in");
        using var h = dev.Value.handle;
        int len = dev.Value.caps.OutputReportByteLength; // 17
        using var fs = new FileStream(h, FileAccess.ReadWrite, 0, isAsync: true);

        var req = new byte[len];
        req[0] = 0x08; req[1] = 0x04;
        req[len - 1] = (byte)(0x55 - req.Take(len - 1).Sum(b => b));
        fs.WriteAsync(req).AsTask().Wait(1000);

        var buf = new byte[dev.Value.caps.InputReportByteLength];
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
            if (pct == 0 && mv == 0) return new(null, false, "Mouse asleep / off");
            return new(pct, charging, $"{pct}%{(charging ? " (charging)" : "")}, {mv / 1000.0:0.00} V");
        }
        return new(null, false, "Mouse asleep / no reply");
    }
}
