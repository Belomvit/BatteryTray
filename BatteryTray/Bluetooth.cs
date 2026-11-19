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

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, int idx, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA d, ref DEVPROPKEY key, out uint type, byte[]? buf, int size, out int req, int flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA d, char[] buf, int size, out int req);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    const int DIGCF_ALLCLASSES = 0x4;
    static DEVPROPKEY FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    static DEVPROPKEY Battery      = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    static DEVPROPKEY IsConnected  = new("83DA6326-97A6-4088-9453-A1923F573B29", 15);

    static byte[]? Get(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY key)
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
            var set = SetupDiGetClassDevs(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1)) continue;
            try
            {
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
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        return null;
    }
}

/// Xbox Wireless Controller over Bluetooth. Hidden from the widget while disconnected.
class XboxController : IBatteryDevice
{
    public string Name => "Xbox Controller";
    public bool QuickPoll => true;

    public BatteryReading Read()
    {
        (bool connected, int? battery)? q;
        try { q = BluetoothProps.Query("Xbox Wireless Controller"); }
        catch (Exception ex) { return new(null, false, "Error: " + ex.Message, Hidden: true); }
        if (q is null) return new(null, false, "Not paired", Hidden: true);
        if (!q.Value.connected) return new(null, false, "Disconnected", Hidden: true);
        if (q.Value.battery is not int pct) return new(null, false, "Connected, battery unknown");
        return new(pct, false, $"{pct}%");
    }
}
