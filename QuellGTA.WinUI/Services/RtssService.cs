using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace QuellGTA.Services;

/// <summary>
/// 控制 RivaTuner Statistics Server（RTSS）：锁帧和在它的 OSD 上显示文字。
/// 原版通过两个内嵌的小程序实现，这里直接调用同样的接口：
/// 锁帧对应 RTSScon.exe（原版注明基于 xanderfrangos/rtss-cli）——加载 RTSS 安装目录里的 RTSSHooks64.dll，调用它导出的配置文件函数；
/// OSD 对应 RTSSosd.exe（Erruar/RTSS-CLI，GPL-3.0）——往 RTSS 的共享内存 RTSSSharedMemoryV2 里写文字，做法出自 RTSS SDK 自带的示例。
/// RTSS 本身由用户自行安装，本程序不包含它的任何文件。
/// </summary>
public sealed class RtssService : IDisposable
{
    public const string DownloadUrl = "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/";
    private const string RegistryKey = @"SOFTWARE\WOW6432Node\Unwinder\RTSS";
    private const string OsdOwner = "QuellGTA";
    private const uint FlagLimiterDisabled = 4, WM_APP = 0x8000, WM_RTSS_UPDATESETTINGS = WM_APP + 100, WM_CLOSE = 0x10;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate void ProfileFn(string profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int PropertyFn(string name, ref uint data, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void UpdateFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint SetFlagsFn(uint and, uint xor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetFlagsFn();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? className, string? title);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    private readonly object _gate = new();
    private nint _library;
    private ProfileFn? _loadProfile, _saveProfile;
    private PropertyFn? _getProperty, _setProperty;
    private UpdateFn? _updateProfiles;
    private SetFlagsFn? _setFlags;
    private GetFlagsFn? _getFlags;

    // ───────── 安装与进程 ─────────

    /// <summary>RTSS.exe 的完整路径；没装返回 null。</summary>
    public static string? ExecutablePath
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RegistryKey);
                return key?.GetValue("InstallPath") is string path && File.Exists(path) ? path : null;
            }
            catch (Exception) { return null; }
        }
    }

    public static bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("RTSS");
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }
    }

    /// <summary>原版「RTSS_是否存在」：正在运行，或者注册表里有安装路径。</summary>
    public static bool IsInstalled => IsRunning || ExecutablePath is not null;

    /// <summary>启动 RTSS（经 explorer 启动，不继承本程序的管理员身份以外的环境）。已在运行或没装时返回 false。</summary>
    public static bool Start()
    {
        if (IsRunning || ExecutablePath is not { } path) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) })?.Dispose();
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>关闭 RTSS（原版「RTSS_关闭程序」）：先请它的窗口自己关；force 或找不到窗口时结束它和它的辅助进程。</summary>
    public static void Close(bool force)
    {
        var hwnd = FindWindowW("RivaTunerStatisticsServer", null);
        if (hwnd != 0 && !force)
        {
            PostMessageW(hwnd, WM_CLOSE, 0, 0);
            return;
        }
        foreach (var name in new[] { "RTSS", "RTSSHooksLoader", "RTSSHooksLoader64", "EncoderServer", "EncoderServer64",
                     "DesktopOverlayHost", "DesktopOverlayHost64", "DesktopOverlayHostLoader" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try { process.Kill(); } catch (Exception) { }
                }
    }

    // ───────── 锁帧（RTSSHooks64.dll 的配置文件接口） ─────────

    private bool EnsureLibrary()
    {
        if (_library != 0) return true;
        if (ExecutablePath is not { } exe) return false;
        var path = Path.Combine(Path.GetDirectoryName(exe)!, "RTSSHooks64.dll");
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var library)) return false;
        try
        {
            T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            _loadProfile = Get<ProfileFn>("LoadProfile");
            _saveProfile = Get<ProfileFn>("SaveProfile");
            _getProperty = Get<PropertyFn>("GetProfileProperty");
            _setProperty = Get<PropertyFn>("SetProfileProperty");
            _updateProfiles = Get<UpdateFn>("UpdateProfiles");
            _setFlags = Get<SetFlagsFn>("SetFlags");
            _getFlags = Get<GetFlagsFn>("GetFlags");
            _library = library;
            return true;
        }
        catch (Exception)
        {
            NativeLibrary.Free(library);
            return false;
        }
    }

    /// <summary>
    /// 设置帧数上限（0 = 不限制）。profile 是进程名（如 GTA5_Enhanced.exe），空串表示全局配置。
    /// 对应 rtss-cli 的 property:set &lt;profile&gt; FramerateLimit / limit:set。
    /// </summary>
    public bool SetFramerateLimit(string profile, int fps)
    {
        lock (_gate)
        {
            if (!EnsureLibrary()) return false;
            var value = (uint)Math.Clamp(fps, 0, 999);
            _loadProfile!(profile);
            _setProperty!("FramerateLimit", ref value, 4);
            _saveProfile!(profile);
            _updateProfiles!();
            return true;
        }
    }

    /// <summary>读取帧数上限；读不到返回 null。</summary>
    public int? GetFramerateLimit(string profile)
    {
        lock (_gate)
        {
            if (!EnsureLibrary()) return null;
            uint value = 0;
            _loadProfile!(profile);
            _getProperty!("FramerateLimit", ref value, 4);
            return (int)value;
        }
    }

    /// <summary>锁帧总开关（rtss-cli 的 limiter:get）；读不到返回 null。</summary>
    public bool? LimiterEnabled
    {
        get
        {
            lock (_gate) return EnsureLibrary() ? (_getFlags!() & FlagLimiterDisabled) == 0 : null;
        }
    }

    /// <summary>rtss-cli 的 limiter:set。</summary>
    public bool SetLimiterEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (!EnsureLibrary()) return false;
            _setFlags!(~FlagLimiterDisabled, enabled ? 0 : FlagLimiterDisabled);
            var hwnd = FindWindowW(null, "RTSS");
            if (hwnd == 0) hwnd = FindWindowW(null, "RivaTuner Statistics Server");
            if (hwnd != 0) PostMessageW(hwnd, WM_RTSS_UPDATESETTINGS, 0, 0);
            return true;
        }
    }

    // ───────── OSD（共享内存 RTSSSharedMemoryV2） ─────────
    // 头部：+0 签名 'RTSS'  +4 版本  +20 OSD 项大小  +24 OSD 数组偏移  +28 OSD 项数  +32 OSD 帧号  +36 忙标志（v2.14 起）
    // OSD 项：+0 szOSD[256]  +256 szOSDOwner[256]  +512 szOSDEx[4096]（v2.7 起）

    private const uint Signature = 0x52545353; // 'RTSS'
    private bool _osdShown;

    /// <summary>
    /// 在 RTSS 的 OSD 上显示一段文字（支持 RTSS 的排版标签，&lt;Br&gt; 换行）。RTSS 没运行时返回 false。
    /// 空串等同于 ClearOsd。
    /// </summary>
    public bool UpdateOsd(string text)
    {
        if (text.Length == 0) { ClearOsd(); return true; }
        var bytes = Encoding.ASCII.GetBytes(text.Replace("<Br>", "\n")); // 调用方只会给可打印的 ASCII
        var ok = WithSharedMemory((view, version, entrySize, arrayOffset, arraySize) =>
        {
            // 第一遍找自己以前占的槽，第二遍占第一个空槽。0 号槽留给 Afterburner 这类主程序
            for (var pass = 0; pass < 2; pass++)
                for (uint i = 1; i < arraySize; i++)
                {
                    long entry = arrayOffset + i * entrySize;
                    var owner = ReadString(view, entry + 256, 256);
                    if (pass == 1 && owner.Length == 0)
                    {
                        WriteString(view, entry + 256, 256, Encoding.ASCII.GetBytes(OsdOwner));
                        owner = OsdOwner;
                    }
                    if (owner != OsdOwner) continue;

                    if (version >= 0x00020007)
                    {
                        if (version >= 0x0002000E)
                        {
                            // 渲染线程正在读 OSD 时不能写，这一次就跳过，下次刷新再写
                            if ((view.ReadUInt32(36) & 1) == 0)
                            {
                                view.Write(36, view.ReadUInt32(36) | 1);
                                WriteString(view, entry + 512, 4096, bytes);
                                view.Write(36, 0u);
                            }
                        }
                        else WriteString(view, entry + 512, 4096, bytes);
                    }
                    else WriteString(view, entry, 256, bytes);
                    view.Write(32, view.ReadUInt32(32) + 1);
                    return true;
                }
            return false;
        });
        if (ok) _osdShown = true;
        return ok;
    }

    /// <summary>清掉本程序在 OSD 上占的槽（RTSSosd.exe --reset）。</summary>
    public void ClearOsd()
    {
        if (!_osdShown) return;
        _osdShown = false;
        WithSharedMemory((view, _, entrySize, arrayOffset, arraySize) =>
        {
            var zeros = new byte[Math.Min(entrySize, 512 + 4096)];
            for (uint i = 1; i < arraySize; i++)
            {
                long entry = arrayOffset + i * entrySize;
                if (ReadString(view, entry + 256, 256) != OsdOwner) continue;
                view.WriteArray(entry, zeros, 0, zeros.Length);
                view.Write(32, view.ReadUInt32(32) + 1);
            }
            return true;
        });
    }

    private static bool WithSharedMemory(Func<MemoryMappedViewAccessor, uint, uint, uint, uint, bool> action)
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.ReadWrite);
            using var view = map.CreateViewAccessor();
            if (view.ReadUInt32(0) != Signature) return false;
            var version = view.ReadUInt32(4);
            if (version < 0x00020000) return false;
            uint entrySize = view.ReadUInt32(20), arrayOffset = view.ReadUInt32(24), arraySize = view.ReadUInt32(28);
            // 防止头部数据异常时越界
            if (entrySize < 512 || arraySize > 64 || (long)arrayOffset + (long)entrySize * arraySize > view.Capacity) return false;
            return action(view, version, entrySize, arrayOffset, arraySize);
        }
        catch (Exception) { return false; } // RTSS 没在运行时共享内存不存在
    }

    private static string ReadString(MemoryMappedViewAccessor view, long offset, int capacity)
    {
        var buffer = new byte[capacity];
        view.ReadArray(offset, buffer, 0, capacity);
        var length = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, length < 0 ? capacity : length);
    }

    private static void WriteString(MemoryMappedViewAccessor view, long offset, int capacity, byte[] text)
    {
        var buffer = new byte[capacity]; // 末尾至少留一个 0
        Array.Copy(text, buffer, Math.Min(text.Length, capacity - 1));
        view.WriteArray(offset, buffer, 0, capacity);
    }

    public void Dispose()
    {
        ClearOsd();
        lock (_gate)
        {
            if (_library != 0) NativeLibrary.Free(_library);
            _library = 0;
        }
    }
}
