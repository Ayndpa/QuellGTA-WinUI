using System.Net;
using System.Net.Sockets;

namespace QuellGTA.Services;

/// <summary>阻止联网的范围，对应原版「断网选项」里的 阻联选项 / 阻云选项。</summary>
public enum BlockScope
{
    /// <summary>全部出站流量。</summary>
    All = 0,
    /// <summary>仅阻止 UDP 协议。</summary>
    UdpOnly = 1,
    /// <summary>仅阻止 6672 端口（P2P）。</summary>
    Port6672 = 2,
    /// <summary>仅阻止 TCP 协议。</summary>
    TcpOnly = 3,
    /// <summary>仅阻止 80、443 端口。</summary>
    Ports80443 = 4,
    /// <summary>仅阻止指定的 Rockstar 服务器（云存档 / 交易 / 差事）。</summary>
    Servers = 5,
}

public sealed record BlockOptions(
    BlockScope Scope, bool CloudSave, bool Transaction, bool Jobs, string? ApplicationPath);

/// <summary>
/// 对应原版「防火墙断网 / 防火墙恢复 / FW_取开启状态」：通过 Windows 防火墙 COM 接口
/// （HNetCfg.FwPolicy2）创建和删除出站阻止规则。原版可选的 WFP 模式见 WfpService。
/// </summary>
public sealed class FirewallService
{
    public const string BlockRule = "QuellGTA_Block";
    public const string HoverRule = "QuellGTA_Hover";

    private const int DirectionOut = 2, DirectionIn = 1, ActionBlock = 0, ProtocolTcp = 6, ProtocolUdp = 17;

    // 原版内置的兜底 IP：域名解析失败时使用。
    private static readonly (string host, string fallback)[] TransactionHosts =
    [
        ("prod.p01sjc.pod.rockstargames.com", "192.81.245.200"),
        ("prod.p02sjc.pod.rockstargames.com", "192.81.245.201"),
    ];
    private const string CloudSaveHost = "cs-gta5-prod.ros.rockstargames.com", CloudSaveFallback = "192.81.241.171";
    private const string JobsHost = "prod.cloud.rockstargames.com";

    private static dynamic CreatePolicy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    private static dynamic CreateRule(string name, int direction)
    {
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
        rule.Name = name;
        rule.Description = "Created by QuellGTA. Safe to delete.";
        rule.Direction = direction;
        rule.Action = ActionBlock;
        rule.Enabled = true;
        return rule;
    }

    /// <summary>当前生效的防火墙配置文件是否全部开启。防火墙被关闭时规则不会生效。</summary>
    public bool IsFirewallEnabled()
    {
        try
        {
            dynamic policy = CreatePolicy();
            int current = policy.CurrentProfileTypes;
            foreach (var profile in new[] { 1, 2, 4 }) // Domain / Private / Public
                if ((current & profile) != 0 && !(bool)policy.FirewallEnabled[profile]) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>域名转 IPv4，失败返回空串（原版的「转换为IP地址」）。</summary>
    private static string Resolve(string host)
    {
        try
        {
            var task = Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork);
            return task.Wait(1500) && task.Result.Length > 0 ? task.Result[0].ToString() : "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// 把「仅阻止指定的 Rockstar 服务器」里勾选的服务器解析成 IPv4 地址（WFP 模式也用它）。
    /// blockEverything 为真表示差事服务器解析失败，按原版的做法退化为阻止全部地址。
    /// </summary>
    public static List<string> ResolveServers(BlockOptions options, out bool blockEverything)
    {
        blockEverything = false;
        var addresses = new List<string>();
        if (options.CloudSave)
        {
            var ip = Resolve(CloudSaveHost);
            addresses.Add(ip.Length > 0 ? ip : CloudSaveFallback);
        }
        if (options.Transaction)
            foreach (var (host, fallback) in TransactionHosts)
            {
                var ip = Resolve(host);
                addresses.Add(ip.Length > 0 ? ip : fallback);
            }
        if (options.Jobs)
        {
            var ip = Resolve(JobsHost);
            if (ip.Length > 0) addresses.Add(ip); else blockEverything = true;
        }
        return addresses.Distinct().ToList();
    }

    /// <summary>创建 QuellGTA_Block 规则。</summary>
    public void Block(BlockOptions options)
    {
        dynamic policy = CreatePolicy();
        dynamic rule = CreateRule(BlockRule, DirectionOut);
        if (!string.IsNullOrEmpty(options.ApplicationPath)) rule.ApplicationName = options.ApplicationPath;

        switch (options.Scope)
        {
            case BlockScope.UdpOnly:
                rule.Protocol = ProtocolUdp;
                break;
            case BlockScope.Port6672:
                rule.Protocol = ProtocolUdp;
                rule.LocalPorts = "6672";
                break;
            case BlockScope.TcpOnly:
                rule.Protocol = ProtocolTcp;
                break;
            case BlockScope.Ports80443:
                rule.Protocol = ProtocolTcp;
                rule.RemotePorts = "80,443";
                break;
            case BlockScope.Servers:
                var addresses = new List<string>();
                var blockEverything = false;
                if (options.CloudSave)
                {
                    var ip = Resolve(CloudSaveHost);
                    addresses.Add(ip.Length > 0 ? ip : CloudSaveFallback);
                }
                if (options.Transaction)
                    foreach (var (host, fallback) in TransactionHosts)
                    {
                        var ip = Resolve(host);
                        addresses.Add(ip.Length > 0 ? ip : fallback);
                    }
                if (options.Jobs)
                {
                    // 原版：差事服务器解析失败时没有兜底 IP，退化为阻止全部地址。
                    var ip = Resolve(JobsHost);
                    if (ip.Length > 0) addresses.Add(ip); else blockEverything = true;
                }
                if (!blockEverything && addresses.Count > 0)
                {
                    rule.Protocol = ProtocolTcp;
                    rule.RemoteAddresses = string.Join(",", addresses.Distinct());
                }
                break;
        }
        policy.Rules.Add(rule);
    }

    /// <summary>卡单模式 4：入站 + 出站同时阻止本机 UDP 6672 端口。</summary>
    public void BlockP2PPort()
    {
        dynamic policy = CreatePolicy();
        foreach (var direction in new[] { DirectionIn, DirectionOut })
        {
            dynamic rule = CreateRule(BlockRule, direction);
            rule.Protocol = ProtocolUdp;
            rule.LocalPorts = "6672";
            policy.Rules.Add(rule);
        }
    }

    /// <summary>瞬间悬停用的临时规则 QuellGTA_Hover。tcpOnly = 阻云悬停，否则为断网悬停。</summary>
    public void BlockForHover(bool tcpOnly, string? applicationPath)
    {
        dynamic policy = CreatePolicy();
        dynamic rule = CreateRule(HoverRule, DirectionOut);
        if (!string.IsNullOrEmpty(applicationPath)) rule.ApplicationName = applicationPath;
        if (tcpOnly) rule.Protocol = ProtocolTcp;
        policy.Rules.Add(rule);
    }

    /// <summary>删除指定名称的全部规则（同名规则可能有多条，循环删到没有为止），返回删除条数。</summary>
    public int Remove(string name)
    {
        dynamic policy = CreatePolicy();
        dynamic rules = policy.Rules;
        var removed = 0;
        for (var i = 0; i < 64 && Exists(rules, name); i++)
        {
            rules.Remove(name);
            removed++;
        }
        return removed;
    }

    public bool Exists(string name)
    {
        try { return Exists(CreatePolicy().Rules, name); }
        catch (Exception) { return false; }
    }

    private static bool Exists(dynamic rules, string name)
    {
        try { return rules.Item(name) is not null; }
        catch (Exception) { return false; } // 找不到时 COM 抛 FileNotFound
    }
}
