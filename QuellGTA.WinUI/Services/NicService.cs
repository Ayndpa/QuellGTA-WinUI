using System.Net.NetworkInformation;

namespace QuellGTA.Services;

public sealed record NicInfo(string Name, string Description, bool IsUp);

/// <summary>
/// 对应原版「网卡禁用 / 网卡恢复」：PowerShell 模式（Disable-NetAdapter / Disable-NetAdapterBinding），
/// 以及 Dev 硬件模式（原版用内嵌的 devcon.exe，这里用 DeviceService 直接调 SetupAPI）。
/// </summary>
public sealed class NicService(Settings settings)
{
    /// <summary>
    /// Dev 硬件模式：直接在设备层面禁用网卡，不经过 PowerShell，省掉启动 PowerShell 的一两秒。
    /// 开启后「仅禁用TCPIP协议」不起作用（设备整个被禁用）。
    /// </summary>
    public bool HardwareMode
    {
        get => settings.GetBool("本地", "Dev模式", false);
        set => settings.SetBool("本地", "Dev模式", value);
    }

    /// <summary>硬件模式下被禁用的设备（实例路径），恢复时按此列表启用。</summary>
    private string[] DisabledDevices
    {
        get => settings.Get("状态", "已禁设备", "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        set => settings.Set("状态", "已禁设备", string.Join('|', value));
    }

    /// <summary>后台启用设备失败时触发（设备实例路径和原因）。</summary>
    public event Action<string>? DeviceError;

    /// <summary>最近一次硬件模式禁用操作里失败的网卡和原因，供日志使用。</summary>
    public List<string> LastErrors { get; } = [];

    /// <summary>「仅禁用TCPIP协议」：只解绑 TCP/IP 协议而不是停用整块网卡，恢复更快。</summary>
    public bool IpOnly
    {
        get => settings.GetBool("本地", "仅IP协议", false);
        set => settings.SetBool("本地", "仅IP协议", value);
    }

    /// <summary>「仅IP协议包含IPv6」。</summary>
    public bool IncludeIPv6
    {
        get => settings.GetBool("扩展", "仅IP含IPv6", false);
        set => settings.SetBool("扩展", "仅IP含IPv6", value);
    }

    /// <summary>用户勾选的要禁用的网卡（连接名），用「|」分隔保存。为空表示全部正在使用的物理网卡。</summary>
    public string[] SelectedNames
    {
        get => settings.Get("本地", "禁卡列表", "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        set => settings.Set("本地", "禁卡列表", string.Join('|', value));
    }

    /// <summary>上一次实际被禁用的网卡，恢复时按此列表启用（网卡被停用后系统就枚举不到它了）。</summary>
    private string[] DisabledNames
    {
        get => settings.Get("状态", "已禁网卡", "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        set => settings.Set("状态", "已禁网卡", string.Join('|', value));
    }

    /// <summary>列出以太网 / 无线网卡（排除回环、隧道）。</summary>
    public static List<NicInfo> List() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.Ppp)
            .Select(n => new NicInfo(n.Name, n.Description, n.OperationalStatus == OperationalStatus.Up))
            .OrderByDescending(n => n.IsUp).ThenBy(n => n.Name)
            .ToList();

    private string[] ResolveTargets()
    {
        var selected = SelectedNames;
        if (selected.Length > 0) return selected;
        return List().Where(n => n.IsUp).Select(n => n.Name).ToArray();
    }

    private static string Quote(string name) => "'" + name.Replace("'", "''") + "'";

    /// <summary>禁用网卡，返回被处理的连接名。</summary>
    public string[] Disable()
    {
        var targets = ResolveTargets();
        LastErrors.Clear();
        if (HardwareMode)
        {
            var done = new List<string>();
            var devices = new List<string>();
            foreach (var name in targets)
            {
                try
                {
                    var device = DeviceService.FindNetworkDevice(name) ?? throw new InvalidOperationException("没有找到对应的设备");
                    DeviceService.SetEnabled(device, false);
                    done.Add(name);
                    devices.Add(device);
                }
                catch (Exception ex) { LastErrors.Add($"\"{name}\"：{ex.Message}"); }
            }
            if (done.Count > 0)
            {
                DisabledNames = DisabledNames.Union(done).ToArray();
                DisabledDevices = DisabledDevices.Union(devices).ToArray();
            }
            return done.ToArray();
        }
        foreach (var name in targets)
        {
            if (IpOnly)
            {
                Shell.PowerShell($"Disable-NetAdapterBinding -Name {Quote(name)} -ComponentID ms_tcpip", wait: false);
                if (IncludeIPv6)
                    Shell.PowerShell($"Disable-NetAdapterBinding -Name {Quote(name)} -ComponentID ms_tcpip6", wait: false);
            }
            else
            {
                Shell.PowerShell($"Disable-NetAdapter -Name {Quote(name)} -Confirm:$false", wait: false);
            }
        }
        if (targets.Length > 0) DisabledNames = DisabledNames.Union(targets).ToArray();
        return targets;
    }

    /// <summary>恢复网卡，返回被处理的连接名。</summary>
    public string[] Restore()
    {
        var targets = DisabledNames.Union(SelectedNames).ToArray();
        LastErrors.Clear();
        // 硬件模式禁用的设备直接在设备层面启用；之后仍然走一遍 PowerShell，用户可能中途换过模式。
        // 启用要等驱动把网卡重新初始化完才返回（无线网卡可能要十几秒），所以放到后台，不卡住调用方。
        foreach (var device in DisabledDevices)
            Task.Run(() =>
            {
                try { DeviceService.SetEnabled(device, true); }
                catch (Exception ex) { DeviceError?.Invoke($"{device}：{ex.Message}"); }
            });
        DisabledDevices = [];
        foreach (var name in targets)
        {
            // 两种方式都恢复一遍：用户可能在禁用之后切换过「仅IP协议」选项。
            Shell.PowerShell($"Enable-NetAdapter -Name {Quote(name)} -Confirm:$false", wait: false);
            Shell.PowerShell($"Enable-NetAdapterBinding -Name {Quote(name)} -ComponentID ms_tcpip", wait: false);
            if (IncludeIPv6)
                Shell.PowerShell($"Enable-NetAdapterBinding -Name {Quote(name)} -ComponentID ms_tcpip6", wait: false);
        }
        DisabledNames = [];
        return targets;
    }

    public bool HasPendingRestore => DisabledNames.Length > 0;

    /// <summary>
    /// 尝试修复网络（原版「_修网络按钮_被单击」）：启用所有可见的网卡，并重新绑定它们的 IPv4 / IPv6 协议。
    /// 包括用户自己手动禁用的网卡也会被启用。
    /// </summary>
    public void RepairAll()
    {
        foreach (var device in DisabledDevices)
        {
            try { DeviceService.SetEnabled(device, true); } catch (Exception) { }
        }
        DisabledDevices = [];
        Shell.PowerShell("Enable-NetAdapter -Name * -Confirm:$false", wait: true);
        Shell.PowerShell("Enable-NetAdapterBinding -Name * -ComponentID ms_tcpip", wait: true);
        Shell.PowerShell("Enable-NetAdapterBinding -Name * -ComponentID ms_tcpip6", wait: true);
        DisabledNames = [];
    }
}
