using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray;

/// An open HID collection and its report sizes.
sealed class HidDevice(SafeFileHandle handle, Hid.HIDP_CAPS caps) : IDisposable
{
    public SafeFileHandle Handle { get; } = handle;
    public Hid.HIDP_CAPS Caps { get; } = caps;

    /// A stream over the handle (requires overlapped: true). Disposing either one closes the
    /// handle; disposing both is harmless.
    public FileStream OpenStream() => new(Handle, FileAccess.ReadWrite, 0, isAsync: true);

    public void Dispose() => Handle.Dispose();
}

/// Minimal HID access through hid.dll / setupapi.dll (no external packages).
static class Hid
{
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public ushort[] Counts;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(DeviceInfoSet set, IntPtr info, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(DeviceInfoSet set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    const uint GENERIC_RW = 0xC0000000, SHARE_RW = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    public static IEnumerable<string> DevicePaths()
    {
        HidD_GetHidGuid(out var g);
        using var set = DeviceInfoSet.ForInterface(g, DeviceInfoSet.DIGCF_PRESENT | DeviceInfoSet.DIGCF_DEVICEINTERFACE);
        if (set.IsInvalid) yield break;
        var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
        for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
        {
            SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int req, IntPtr.Zero);
            var buf = Marshal.AllocHGlobal(req);
            try
            {
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out _, IntPtr.Zero))
                    yield return Marshal.PtrToStringUni(buf + 4)!;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    /// Opens the HID collection of the given VID/PID whose top-level usage page matches.
    public static HidDevice? Open(ushort vid, ushort pid, ushort usagePage, bool overlapped = false)
    {
        string tag = $"vid_{vid:x4}&pid_{pid:x4}";
        foreach (var path in DevicePaths())
        {
            if (!path.Contains(tag, StringComparison.OrdinalIgnoreCase)) continue;
            var h = CreateFile(path, GENERIC_RW, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, overlapped ? FILE_FLAG_OVERLAPPED : 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }
            if (HidD_GetPreparsedData(h, out var pp))
            {
                HidP_GetCaps(pp, out var caps);
                HidD_FreePreparsedData(pp);
                if (caps.UsagePage == usagePage) return new HidDevice(h, caps);
            }
            h.Dispose();
        }
        return null;
    }

    public static byte[] GetFeature(SafeFileHandle h, byte reportId, int len)
    {
        var buf = new byte[len];
        buf[0] = reportId;
        if (!HidD_GetFeature(h, buf, len)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return buf;
    }
}
