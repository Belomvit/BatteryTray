namespace BatteryTray;

/// What the widget shows for one device; every look reads these rules from here.
sealed record ItemView(string Glyph, string Value, int? Percent, bool Charging, bool Low, string Tip)
{
    /// No percentage to show: disconnected, unknown, or not read yet.
    public bool Off => Percent is null;

    public static ItemView From(DeviceStatus d)
    {
        var r = d.Reading;
        int? pct = r?.Percent;
        bool charging = r?.Charging == true && pct is not null;
        // "…" until the first read, then the number or "Off".
        string value = pct is int p ? $"{p}%" : r is null ? "…" : "Off";
        return new(d.Device.Glyph, value, pct, charging, Low: pct <= 20 && !charging,
                   Tip: $"{d.Device.Name}: {r?.Detail ?? "checking…"}");
    }
}
