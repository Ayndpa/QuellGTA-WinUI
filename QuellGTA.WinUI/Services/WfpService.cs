using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace QuellGTA.Services;

/// <summary>一条 WFP 阻断规则的条件，各项之间是「同时满足」的关系；全部留空就是阻断所有出站 IPv4 连接。</summary>
public sealed record WfpRule(
    string? ProcessPath = null, IPAddress? RemoteAddress = null, int? RemotePort = null, int? LocalPort = null, int? Protocol = null);

/// <summary>
/// WFP 断网模式：直接向 Windows 筛选平台（Windows Filtering Platform）添加阻断过滤器，不经过 Windows 防火墙，
/// 所以防火墙被关掉的电脑也能用，生效也更快。
///
/// 移植自 QuellGTA_WFP（作者 渟雲 / M3351AN，公有领域发布，原版把它编译成 WFPcon.exe 内嵌在主程序里）。
/// 原版用 C 写，是因为易语言不方便摆这些嵌套结构体；WFP 的用户态接口其实是 fwpuclnt.dll 导出的普通 C 函数，
/// C# 用 P/Invoke 就能调。只有需要自己检查或改写数据包时才必须写内核驱动，按进程 / 端口 / 地址阻断用不到。
///
/// 与原版一致的地方：同样的提供者和子层 GUID（所以两边创建的规则可以互相清理）、挂在出站连接授权这一层、
/// 规则不持久化（重启系统后自动消失）。
/// 与原版不同：原版只管 IPv4；这里对没有指定 IPv4 地址的规则同时在 IPv6 层也加一条，否则走 IPv6 的连接断不掉。
/// </summary>
public sealed class WfpService
{
    // 原版的提供者 / 子层标识
    private static readonly Guid Provider = new("4310d91e-f336-43f3-a210-886848efc14a");
    private static readonly Guid SubLayer = new("9946b99e-15fb-4371-9ca3-84be8b9b42ad");

    // 系统定义的层和条件字段
    private static readonly Guid LayerAleAuthConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid LayerAleAuthConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid ConditionRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid ConditionRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid ConditionLocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b");
    private static readonly Guid ConditionProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");

    private const uint RpcAuthnWinNt = 10;
    private const uint FwpEAlreadyExists = 0x80320009;
    private const uint FwpUint8 = 1, FwpUint16 = 2, FwpUint32 = 3, FwpByteBlobType = 12;
    private const uint FwpMatchEqual = 0;
    private const uint FwpActionBlock = 0x1001;
    private const uint FilterFlagIndexed = 0x40;
    private const string FilterName = "QuellGTA Block";

    // ───────── 结构体（x64 布局） ─────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayData
    {
        public nint Name, Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Session
    {
        public Guid SessionKey;
        public DisplayData Display;
        public uint Flags, TxnWaitTimeoutInMSec, ProcessId;
        public nint Sid, Username;
        public int KernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteBlob
    {
        public uint Size;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderInfo
    {
        public Guid ProviderKey;
        public DisplayData Display;
        public uint Flags;
        public ByteBlob ProviderData;
        public nint ServiceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SubLayerInfo
    {
        public Guid SubLayerKey;
        public DisplayData Display;
        public uint Flags;
        public nint ProviderKey;
        public ByteBlob ProviderData;
        public ushort Weight;
    }

    /// <summary>FWP_VALUE0 / FWP_CONDITION_VALUE0：一个类型标记加一个 8 字节的联合体。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Value
    {
        public uint Type;
        public ulong Data; // 小于 8 字节的值放在低位；指针类型放地址
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Condition
    {
        public Guid FieldKey;
        public uint MatchType;
        public Value ConditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterAction
    {
        public uint Type;
        public Guid FilterType; // 联合体：calloutKey / filterType
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Filter
    {
        public Guid FilterKey;
        public DisplayData Display;
        public uint Flags;
        public nint ProviderKey;
        public ByteBlob ProviderData;
        public Guid LayerKey;
        public Guid SubLayerKey;
        public Value Weight;
        public uint NumFilterConditions;
        public nint FilterConditions;
        public FilterAction Action;
        public ulong ContextLow, ContextHigh; // 联合体：rawContext / providerContextKey（按 8 字节对齐，所以不能直接写成 Guid）
        public nint Reserved;
        public ulong FilterId;
        public Value EffectiveWeight;
    }

    // ───────── fwpuclnt.dll ─────────

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmEngineOpen0(string? serverName, uint authnService, nint authIdentity, ref Session session, out nint engine);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineClose0(nint engine);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionBegin0(nint engine, uint flags);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionCommit0(nint engine);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionAbort0(nint engine);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmProviderAdd0(nint engine, ref ProviderInfo provider, nint securityDescriptor);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmProviderDeleteByKey0(nint engine, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmSubLayerAdd0(nint engine, ref SubLayerInfo subLayer, nint securityDescriptor);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmSubLayerDeleteByKey0(nint engine, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterAdd0(nint engine, ref Filter filter, nint securityDescriptor, out ulong id);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterDeleteByKey0(nint engine, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterCreateEnumHandle0(nint engine, nint enumTemplate, out nint enumHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterEnum0(nint engine, nint enumHandle, uint requested, out nint entries, out uint returned);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterDestroyEnumHandle0(nint engine, nint enumHandle);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmGetAppIdFromFileName0(string fileName, out nint appId);

    [DllImport("fwpuclnt.dll")]
    private static extern void FwpmFreeMemory0(ref nint pointer);

    // ───────── 实现 ─────────

    private static void Check(uint status, string what)
    {
        if (status != 0) throw new Win32Exception(unchecked((int)status), $"{what}失败（0x{status:X8}）");
    }

    private const uint SessionFlagDynamic = 1;

    private static nint OpenEngine(bool dynamic = false)
    {
        var name = Marshal.StringToHGlobalUni("QuellGTA WFP");
        try
        {
            var session = new Session
            {
                Display = new DisplayData { Name = name }, TxnWaitTimeoutInMSec = 5000, Flags = dynamic ? SessionFlagDynamic : 0,
            };
            Check(FwpmEngineOpen0(null, RpcAuthnWinNt, 0, ref session, out var engine), "打开筛选引擎");
            return engine;
        }
        finally { Marshal.FreeHGlobal(name); }
    }

    /// <summary>注册提供者和子层（已存在则跳过）。规则都挂在这个子层下，清理时按它来找。</summary>
    private static void EnsureProvider(nint engine, nint providerKey)
    {
        nint providerName = Marshal.StringToHGlobalUni("MageAngela"), subLayerName = Marshal.StringToHGlobalUni("QuellGTA");
        try
        {
            var provider = new ProviderInfo { ProviderKey = Provider, Display = new DisplayData { Name = providerName } };
            var status = FwpmProviderAdd0(engine, ref provider, 0);
            if (status != 0 && status != FwpEAlreadyExists) Check(status, "注册提供者");

            var subLayer = new SubLayerInfo
            {
                SubLayerKey = SubLayer, Display = new DisplayData { Name = subLayerName }, ProviderKey = providerKey, Weight = 0xFFFF,
            };
            status = FwpmSubLayerAdd0(engine, ref subLayer, 0);
            if (status != 0 && status != FwpEAlreadyExists) Check(status, "注册子层");
        }
        finally
        {
            Marshal.FreeHGlobal(providerName);
            Marshal.FreeHGlobal(subLayerName);
        }
    }

    /// <summary>添加一条阻断规则。规则一直有效，直到调用 <see cref="Clear"/> 或系统重启。</summary>
    public void Block(WfpRule rule)
    {
        var engine = OpenEngine();
        try { AddFilter(engine, rule); }
        finally { FwpmEngineClose0(engine); }
    }

    /// <summary>
    /// 添加一条临时规则：返回的对象被释放时规则自动消失；即使本程序中途崩溃或被结束，系统也会把它收回
    /// （WFP 的「动态会话」）。瞬间悬停用它来保证不会因为意外而把网络一直断着。
    /// </summary>
    public IDisposable BlockTemporarily(WfpRule rule)
    {
        var engine = OpenEngine(dynamic: true);
        try
        {
            AddFilter(engine, rule);
            return new TemporaryRule(engine);
        }
        catch
        {
            FwpmEngineClose0(engine);
            throw;
        }
    }

    private sealed class TemporaryRule(nint engine) : IDisposable
    {
        private nint _engine = engine;

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _engine, 0);
            if (handle != 0) FwpmEngineClose0(handle);
        }
    }

    private static void AddFilter(nint engine, WfpRule rule)
    {
        nint appId = 0;
        var allocated = new List<nint>();
        nint Alloc(int size) { var p = Marshal.AllocHGlobal(size); allocated.Add(p); return p; }
        try
        {
            var providerKey = Alloc(16);
            Marshal.StructureToPtr(Provider, providerKey, false);
            EnsureProvider(engine, providerKey);

            var conditions = new List<Condition>();
            if (!string.IsNullOrEmpty(rule.ProcessPath))
            {
                // 把文件路径换成 WFP 认的应用标识（内核里的设备路径形式）
                Check(FwpmGetAppIdFromFileName0(rule.ProcessPath, out appId), "解析程序路径");
                conditions.Add(new Condition
                {
                    FieldKey = ConditionAppId, MatchType = FwpMatchEqual,
                    ConditionValue = new Value { Type = FwpByteBlobType, Data = (ulong)appId },
                });
            }
            if (rule.RemoteAddress is { } address)
            {
                var bytes = address.GetAddressBytes();
                if (bytes.Length != 4) throw new ArgumentException("WFP 模式只支持 IPv4 地址。");
                // WFP 里的 IPv4 地址用主机字节序
                var value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
                conditions.Add(new Condition
                {
                    FieldKey = ConditionRemoteAddress, MatchType = FwpMatchEqual, ConditionValue = new Value { Type = FwpUint32, Data = value },
                });
            }
            if (rule.RemotePort is { } remotePort)
                conditions.Add(new Condition
                {
                    FieldKey = ConditionRemotePort, MatchType = FwpMatchEqual,
                    ConditionValue = new Value { Type = FwpUint16, Data = (ushort)remotePort },
                });
            if (rule.LocalPort is { } localPort)
                conditions.Add(new Condition
                {
                    FieldKey = ConditionLocalPort, MatchType = FwpMatchEqual,
                    ConditionValue = new Value { Type = FwpUint16, Data = (ushort)localPort },
                });
            if (rule.Protocol is { } protocol)
                conditions.Add(new Condition
                {
                    FieldKey = ConditionProtocol, MatchType = FwpMatchEqual,
                    ConditionValue = new Value { Type = FwpUint8, Data = (byte)protocol },
                });

            nint conditionArray = 0;
            if (conditions.Count > 0)
            {
                var size = Marshal.SizeOf<Condition>();
                conditionArray = Alloc(size * conditions.Count);
                for (var i = 0; i < conditions.Count; i++) Marshal.StructureToPtr(conditions[i], conditionArray + i * size, false);
            }

            var name = Marshal.StringToHGlobalUni(FilterName);
            allocated.Add(name);
            // 指定了 IPv4 地址的规则只对 IPv4 有意义；其余的规则 IPv4、IPv6 各加一条
            var layers = rule.RemoteAddress is null ? new[] { LayerAleAuthConnectV4, LayerAleAuthConnectV6 } : [LayerAleAuthConnectV4];
            foreach (var layer in layers)
            {
                var filter = new Filter
                {
                    FilterKey = Guid.NewGuid(),
                    Display = new DisplayData { Name = name },
                    Flags = FilterFlagIndexed,
                    ProviderKey = providerKey,
                    LayerKey = layer,
                    SubLayerKey = SubLayer,
                    NumFilterConditions = (uint)conditions.Count,
                    FilterConditions = conditionArray,
                    Action = new FilterAction { Type = FwpActionBlock },
                };
                Check(FwpmFilterAdd0(engine, ref filter, 0, out _), "添加过滤器");
            }
        }
        finally
        {
            if (appId != 0) FwpmFreeMemory0(ref appId);
            foreach (var pointer in allocated) Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>删除本程序（以及原版 WFPcon）创建的全部规则，返回删除的条数。</summary>
    public int Clear()
    {
        var engine = OpenEngine();
        try
        {
            Check(FwpmTransactionBegin0(engine, 0), "开始事务");
            var removed = 0;
            try
            {
                foreach (var key in FindFilters(engine))
                {
                    var copy = key;
                    if (FwpmFilterDeleteByKey0(engine, ref copy) == 0) removed++;
                }
                if (removed > 0)
                {
                    // 子层和提供者上已经没有规则了，一并拿掉，不在系统里留痕迹
                    Guid subLayer = SubLayer, provider = Provider;
                    FwpmSubLayerDeleteByKey0(engine, ref subLayer);
                    FwpmProviderDeleteByKey0(engine, ref provider);
                }
                Check(FwpmTransactionCommit0(engine), "提交事务");
            }
            catch
            {
                FwpmTransactionAbort0(engine);
                throw;
            }
            return removed;
        }
        finally { FwpmEngineClose0(engine); }
    }

    /// <summary>当前有多少条本程序的规则。</summary>
    public int Count()
    {
        var engine = OpenEngine();
        try { return FindFilters(engine).Count; }
        finally { FwpmEngineClose0(engine); }
    }

    /// <summary>枚举系统里所有过滤器，挑出挂在我们子层下的那些。</summary>
    private static List<Guid> FindFilters(nint engine)
    {
        var keys = new List<Guid>();
        Check(FwpmFilterCreateEnumHandle0(engine, 0, out var handle), "枚举过滤器");
        try
        {
            while (true)
            {
                if (FwpmFilterEnum0(engine, handle, 512, out var entries, out var count) != 0 || count == 0)
                {
                    if (entries != 0) FwpmFreeMemory0(ref entries);
                    break;
                }
                for (var i = 0; i < count; i++)
                {
                    var pointer = Marshal.ReadIntPtr(entries, i * nint.Size);
                    if (pointer == 0) continue;
                    var filter = Marshal.PtrToStructure<Filter>(pointer);
                    var ours = filter.SubLayerKey == SubLayer
                               || (filter.ProviderKey != 0 && Marshal.PtrToStructure<Guid>(filter.ProviderKey) == Provider);
                    if (ours) keys.Add(filter.FilterKey);
                }
                FwpmFreeMemory0(ref entries);
            }
        }
        finally { FwpmFilterDestroyEnumHandle0(engine, handle); }
        return keys;
    }
}
