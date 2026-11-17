using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuellGTA.Services;

/// <summary>
/// 设备级的启用 / 禁用，对应原版内嵌的 devcon.exe（原版执行 devcon disable "@设备实例路径"）。
/// devcon 内部做的就是通过 SetupAPI 给设备发一个「属性变更」安装请求，这里直接调用同样的接口，不需要外部程序。
/// </summary>
public static class DeviceService
{
    private const string NetworkClassKey = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";
    private const uint DIF_PROPERTYCHANGE = 0x12, DICS_ENABLE = 1, DICS_DISABLE = 2, DICS_FLAG_GLOBAL = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public uint cbSize;          // SP_CLASSINSTALL_HEADER.cbSize
        public uint InstallFunction; // SP_CLASSINSTALL_HEADER.InstallFunction
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern nint SetupDiCreateDeviceInfoList(nint classGuid, nint hwndParent);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiOpenDeviceInfoW(nint set, string instanceId, nint hwndParent, uint flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParamsW(nint set, ref SP_DEVINFO_DATA data, ref SP_PROPCHANGE_PARAMS parameters, uint size);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint function, nint set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    /// <summary>
    /// 网络连接名（「以太网」「WLAN」）→ 设备实例路径。从注册表里查，所以网卡已经被禁用、系统枚举不到时也查得到。
    /// </summary>
    public static string? FindNetworkDevice(string connectionName)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(NetworkClassKey);
            if (root is null) return null;
            foreach (var id in root.GetSubKeyNames())
            {
                using var connection = root.OpenSubKey(id + @"\Connection");
                if (connection?.GetValue("Name") is string name && name.Equals(connectionName, StringComparison.OrdinalIgnoreCase)
                    && connection.GetValue("PnPInstanceId") is string { Length: > 0 } instance)
                    return instance;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>启用或禁用一个设备。失败时抛出带系统错误信息的异常。</summary>
    public static void SetEnabled(string instanceId, bool enabled)
    {
        var set = SetupDiCreateDeviceInfoList(0, 0);
        if (set == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var device = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, instanceId, 0, 0, ref device))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var change = new SP_PROPCHANGE_PARAMS
            {
                cbSize = 8,
                InstallFunction = DIF_PROPERTYCHANGE,
                StateChange = enabled ? DICS_ENABLE : DICS_DISABLE,
                Scope = DICS_FLAG_GLOBAL,
            };
            if (!SetupDiSetClassInstallParamsW(set, ref device, ref change, (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>())
                || !SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref device))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
