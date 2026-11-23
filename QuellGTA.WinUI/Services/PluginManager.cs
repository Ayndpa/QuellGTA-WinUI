using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuellGTA.Plugins;

namespace QuellGTA.Services;

/// <summary>
/// 插件管理：扫描插件目录、读 plugin.json、按用户的启用设置加载程序集并调用插件入口。
/// 原版的外置小工具（PC 设置编辑器、UDP 触发程序、根据任务启停等）在移植版里都做成了插件，
/// 第三方也可以照同样的接口写插件（见 QuellGTA.Plugins.Abstractions）。
///
/// 安全：插件就是普通的 .NET 代码，和本程序同样以管理员权限运行。所以随程序发布的插件默认启用，
/// 用户自己放进数据目录的插件默认不启用——只列出 plugin.json 里写的信息，不加载任何代码，用户确认后才加载。
/// 启用、停用都在下次启动时生效（WinUI 的类型加载后无法安全卸载）。
/// </summary>
public sealed partial class PluginManager(Settings settings, LogService log) : IDisposable
{
    public sealed class Entry(PluginInfo info)
    {
        public PluginInfo Info { get; } = info;
        public IQuellPlugin? Instance { get; internal set; }
        /// <summary>这次启动时是否已经加载（启用设置在下次启动才生效，所以可能和 Enabled 不同）。</summary>
        public bool Loaded => Instance is not null;
        /// <summary>加载或初始化失败的原因。</summary>
        public string Error { get; internal set; } = "";
    }

    private readonly List<Entry> _entries = [];

    /// <summary>随程序发布的插件。</summary>
    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "Plugins");
    /// <summary>用户自己装的插件。</summary>
    public string UserDirectory => Path.Combine(settings.DataDirectory, "Plugins");

    public IReadOnlyList<Entry> Plugins => _entries;

    /// <summary>已加载的插件里有页面的那些变化时触发（目前只在启动时加载一次）。</summary>
    public event Action? Changed;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,47}$")]
    private static partial Regex IdPattern();

    public bool IsEnabled(PluginInfo info)
    {
        var value = settings.Get("插件", info.Id, "");
        return value.Length == 0 ? info.Bundled : value == "是";
    }

    public void SetEnabled(PluginInfo info, bool enabled) => settings.SetBool("插件", info.Id, enabled);

    /// <summary>扫描两个插件目录。同一个 id 出现两次时，随程序发布的那份优先。</summary>
    public void Discover()
    {
        _entries.Clear();
        foreach (var (root, bundled) in new[] { (BundledDirectory, true), (UserDirectory, false) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                var manifest = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifest)) continue;
                var info = ReadManifest(manifest, dir, bundled, out var error);
                if (info is null)
                {
                    log.Write($"插件「{Path.GetFileName(dir)}」的 plugin.json 无效：{error}", 2);
                    continue;
                }
                if (_entries.Any(e => e.Info.Id == info.Id))
                {
                    log.Write($"插件 {info.Id} 重复，已忽略 {dir}。", 2);
                    continue;
                }
                _entries.Add(new Entry(info));
            }
        }
    }

    private static PluginInfo? ReadManifest(string path, string dir, bool bundled, out string error)
    {
        error = "";
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = json.RootElement;
            string Text(string name, string fallback = "") =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
            var id = Text("id");
            if (!IdPattern().IsMatch(id)) { error = "id 只能用小写字母、数字和 -（2–48 个字符）"; return null; }
            var assembly = Text("assembly");
            if (assembly.Length == 0 || assembly.Contains("..") || Path.IsPathRooted(assembly)) { error = "assembly 无效"; return null; }
            var api = root.TryGetProperty("api", out var a) && a.TryGetInt32(out var n) ? n : 1;
            return new PluginInfo(id, Text("name", id), Text("description"), Text("version", "1.0.0"), Text("author"),
                Text("glyph", ""), assembly, api, dir, bundled);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>加载所有启用的插件并初始化。必须在界面线程上调用。</summary>
    public void LoadEnabled(Func<PluginInfo, IPluginHost> createHost)
    {
        foreach (var entry in _entries)
        {
            if (!IsEnabled(entry.Info)) continue;
            var info = entry.Info;
            try
            {
                if (info.Api > PluginApi.Version)
                    throw new InvalidOperationException($"需要插件接口版本 {info.Api}，本程序只支持到 {PluginApi.Version}，请更新本程序。");
                var path = Path.Combine(info.Directory, info.Assembly);
                if (!File.Exists(path)) throw new FileNotFoundException($"找不到 {info.Assembly}");
                var context = new PluginLoadContext(info.Id, path);
                var assembly = context.LoadFromAssemblyPath(path);
                var type = assembly.GetExportedTypes()
                               .FirstOrDefault(t => typeof(IQuellPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
                           ?? throw new InvalidOperationException("程序集里没有实现 IQuellPlugin 的公开类");
                var plugin = (IQuellPlugin)Activator.CreateInstance(type)!;
                plugin.Initialize(createHost(info));
                entry.Instance = plugin;
                if (!info.Bundled) log.Write($"已加载第三方插件「{info.Name}」{info.Version}（{info.Directory}）。");
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
                entry.Error = inner.Message;
                log.Write($"插件「{info.Name}」加载失败：{inner.Message}", 3);
            }
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        foreach (var entry in _entries)
        {
            try { entry.Instance?.Dispose(); }
            catch (Exception ex) { log.Write($"插件「{entry.Info.Name}」退出时出错：{ex.Message}", 2); }
            entry.Instance = null;
        }
    }

    /// <summary>
    /// 每个插件一个加载上下文：插件自带的依赖从插件目录加载；本程序目录里已有的程序集（插件接口、WinUI、
    /// CommunityToolkit、.NET 本身）一律交给默认上下文，用本程序那一份——否则同名类型对不上，插件也就认不出 IQuellPlugin。
    /// </summary>
    private sealed class PluginLoadContext(string id, string mainAssembly) : AssemblyLoadContext("Plugin:" + id, isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);
        private readonly string _directory = Path.GetDirectoryName(mainAssembly)!;

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null) return null;
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll"))) return null;
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            if (path is null)
            {
                var local = Path.Combine(_directory, name.Name + ".dll");
                if (File.Exists(local)) path = local;
            }
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
