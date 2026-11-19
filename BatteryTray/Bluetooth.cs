using System.Runtime.InteropServices;

namespace BatteryTray;

/// Reads battery + connection state that Windows itself keeps for paired Bluetooth devices
/// (the same values shown in Settings > Bluetooth & devices).
static class BluetoothProps
{
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; } }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(DeviceInfoSet set, int idx, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    static extern bool SetupDiGetDeviceProperty(DeviceInfoSet set, ref SP_DEVINFO_DATA d, ref DEVPROPKEY key, out uint type, byte[]? buf, int size, out int req, int flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInstanceId(DeviceInfoSet set, ref SP_DEVINFO_DATA d, char[] buf, int size, out int req);

    static readonly DEVPROPKEY FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    static readonly DEVPROPKEY Battery      = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    static readonly DEVPROPKEY IsConnected  = new("83DA6326-97A6-4088-9453-A1923F573B29", 15);

    static byte[]? Get(DeviceInfoSet set, ref SP_DEVINFO_DATA d, DEVPROPKEY key)
    {
        SetupDiGetDeviceProperty(set, ref d, ref key, out _, null, 0, out int req, 0);
        if (req <= 0) return null;
        var buf = new byte[req];
        return SetupDiGetDeviceProperty(set, ref d, ref key, out _, buf, req, out _, 0) ? buf : null;
    }

    /// Finds the paired Bluetooth device whose name contains <paramref name="nameContains"/>.
    /// Returns (connected, battery %) or null when it isn't paired.
    public static (bool connected, int? battery)? Query(string nameContains)
    {
        foreach (var enumerator in new[] { "BTHLE", "BTHENUM" })
        {
            using var set = DeviceInfoSet.ForEnumerator(enumerator, DeviceInfoSet.DIGCF_ALLCLASSES);
            if (set.IsInvalid) continue;
            var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
            {
                var idBuf = new char[512];
                if (!SetupDiGetDeviceInstanceId(set, ref d, idBuf, idBuf.Length, out _)) continue;
                var id = new string(idBuf).TrimEnd('\0');
                if (!id.StartsWith(enumerator + @"\DEV_", StringComparison.OrdinalIgnoreCase)) continue;

                var nameBytes = Get(set, ref d, FriendlyName);
                var name = nameBytes is null ? "" : System.Text.Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
                if (!name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;

                var conn = Get(set, ref d, IsConnected);
                var bat = Get(set, ref d, Battery);
                bool connected = conn is { Length: > 0 } && conn[0] != 0;
                int? battery = bat is { Length: > 0 } && bat[0] <= 100 ? bat[0] : null;
                return (connected, battery);
            }
        }
        return null;
    }
}

/// Xbox Wireless Controller over Bluetooth. Hidden from the widget while disconnected.
class XboxController : IBatteryDevice
{
    public string Name => "Xbox Controller";
    public string Glyph => ""; // game controller
    public bool QuickPoll => true;
    public bool HideWhenDisconnected => true;

    public BatteryReading Read()
    {
        var q = BluetoothProps.Query("Xbox Wireless Controller");
        if (q is null) return BatteryReading.Disconnected("Not paired");
        if (!q.Value.connected) return BatteryReading.Disconnected("Disconnected");
        if (q.Value.battery is not int pct) return BatteryReading.Unknown("Connected, battery unknown");
        return BatteryReading.Level(pct, false, $"{pct}%");
    }
}
