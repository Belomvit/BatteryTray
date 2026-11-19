using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray;

/// A SetupAPI device information set; destroyed when disposed.
sealed class DeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
{
    public DeviceInfoSet() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);

    public const int DIGCF_ALLCLASSES = 0x4, DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

    /// Devices exposing the given interface class.
    public static DeviceInfoSet ForInterface(Guid interfaceClass, int flags) =>
        SetupDiGetClassDevs(ref interfaceClass, IntPtr.Zero, IntPtr.Zero, flags);

    /// Devices of every class created by the given enumerator (e.g. "BTHLE").
    public static DeviceInfoSet ForEnumerator(string enumerator, int flags) =>
        SetupDiGetClassDevs(IntPtr.Zero, enumerator, IntPtr.Zero, flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern DeviceInfoSet SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern DeviceInfoSet SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
