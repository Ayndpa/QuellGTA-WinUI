using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace QuellGTA.Plugins;

// QuellGTA 的插件机制
//
// 一个插件是一个文件夹，里面有 plugin.json 和插件自己的程序集（.NET 8 类库）。
// 主程序启动时扫描两个位置：程序目录下的 Plugins\（随程序发布的插件）和数据目录下的 Plugins\（用户自己装的）。
// 先只读 plugin.json，用户启用了才加载程序集；每个插件在自己的 AssemblyLoadContext 里加载，
// 本接口、WinUI 和 CommunityToolkit 这些主程序已有的程序集共用主程序那一份。
//
// plugin.json 示例：
// {
//   "id": "pc-settings-editor",          // 唯一标识，只用小写字母、数字和 -
//   "name": "PC 设置编辑器",
//   "description": "编辑 pc_settings.bin 里的线上设置。",
//   "version": "1.0.0",
//   "author": "MageAngela",
//   "glyph": "\uE713",                   // 导航栏图标（Segoe Fluent Icons）
//   "assembly": "QuellGTA.Plugin.PcSettings.dll",
//   "api": 1                             // 写插件时的 PluginApi.Version
// }

/// <summary>插件接口的版本。</summary>
public static class PluginApi
{
    /// <summary>当前接口版本。插件的 plugin.json 里 api 大于它时不加载。</summary>
    public const int Version = 1;
}

/// <summary>
/// 插件的入口。程序集里第一个实现它的公开类会被实例化（需要无参构造函数）。
/// 方法都在界面线程上调用。
/// </summary>
public interface IQuellPlugin : IDisposable
{
    /// <summary>加载后调用一次。在这里注册快捷键、订阅事件、读设置。</summary>
    void Initialize(IPluginHost host);

    /// <summary>
    /// 创建插件页面，每次在导航栏里打开插件都会调用一次（返回新实例，页面打开时读到的就是最新状态）。
    /// 没有页面的插件返回 null，导航栏里就不显示它。
    /// </summary>
    UIElement? CreatePage();
}

/// <summary>plugin.json 里的信息。</summary>
public sealed record PluginInfo(string Id, string Name, string Description, string Version, string Author, string Glyph,
    string Assembly, int Api, string Directory, bool Bundled);

public enum PluginLogLevel { Info = 0, Success = 1, Warning = 2, Error = 3 }

/// <summary>主程序提供给插件的全部能力。</summary>
public interface IPluginHost
{
    PluginInfo Info { get; }
    /// <summary>主程序版本。</summary>
    Version HostVersion { get; }
    /// <summary>插件自己的数据目录（%AppData%\MageAngela\QuellGTA\Plugins\&lt;id&gt;），第一次取时创建。</summary>
    string DataDirectory { get; }
    /// <summary>主程序的数据目录（%AppData%\MageAngela\QuellGTA）。</summary>
    string HostDataDirectory { get; }
    /// <summary>界面线程的调度器。</summary>
    DispatcherQueue Dispatcher { get; }

    IPluginSettings Settings { get; }
    IPluginGame Game { get; }
    IPluginState State { get; }
    IPluginHotkeys Hotkeys { get; }
    IPluginInput Input { get; }
    IPluginUi Ui { get; }

    /// <summary>写一条日志，出现在「活动」页（前面自动加上插件名）。</summary>
    void Log(string text, PluginLogLevel level = PluginLogLevel.Info);

    /// <summary>
    /// 执行一条主程序指令，和 UDP 指令相同：kill、fwon、fwoff、single、bancard、paste、timerst、timerzero、
    /// timerstart、timerstop、joinbot1、joinbot2、winmini。返回是否认识这条指令。可以从任何线程调用。
    /// </summary>
    bool RunCommand(string command);

    /// <summary>播放主程序的提示音（编号同原版声音资源）。</summary>
    void PlaySound(int sound);

    /// <summary>
    /// 通过 RTSS 给游戏锁帧（0 = 不限制），沿用主程序「浮窗」页里的 RTSS 设置。没装 RTSS 或主程序没开 RTSS 锁帧时返回 false。
    /// 会访问磁盘和 RTSS，不要在界面线程上调用。
    /// </summary>
    bool SetFrameLimit(int fps);
}

/// <summary>插件自己的设置，存在主程序配置文件的 [插件.&lt;id&gt;] 节里。</summary>
public interface IPluginSettings
{
    string Get(string key, string fallback = "");
    void Set(string key, string value);
    int GetInt(string key, int fallback);
    void SetInt(string key, int value);
    bool GetBool(string key, bool fallback);
    void SetBool(string key, bool value);
}

/// <summary>游戏相关的信息和操作。主程序设置的游戏版本（增强版 / 传承版 / 自定义进程）决定这里的值。</summary>
public interface IPluginGame
{
    /// <summary>游戏进程名，例如 GTA5_Enhanced.exe。</summary>
    string ProcessName { get; }
    /// <summary>1 传承版，2 增强版，-1 自定义进程。</summary>
    int Edition { get; }
    /// <summary>文档里的游戏目录，例如 C:\Users\x\Documents\Rockstar Games\GTAV Enhanced。</summary>
    string DocumentsDirectory { get; }
    /// <summary>存档目录（DocumentsDirectory\Profiles），里面每个子目录是一个账号的存档。</summary>
    string ProfilesDirectory { get; }
    /// <summary>游戏安装目录；还不知道时为空串（游戏运行过一次之后才知道）。</summary>
    string InstallDirectory { get; }
    bool IsRunning { get; }
    bool IsForeground { get; }
    /// <summary>游戏主窗口句柄，找不到为 0。</summary>
    nint WindowHandle { get; }
    void BringToFront();
    /// <summary>游戏启动 / 退出（主程序检测到之后，在后台线程上触发）。</summary>
    event Action? Started;
    event Action? Exited;
}

/// <summary>主程序当前的状态。</summary>
public interface IPluginState
{
    bool NetworkBlocked { get; }
    bool NicDisabled { get; }
    bool SoloActive { get; }
    /// <summary>简易计时的读数（毫秒）和是否在走。</summary>
    long TimerMilliseconds { get; }
    bool TimerRunning { get; }
    /// <summary>「设置 → 外部控制 → UDP 指令」是否开启，以及端口。</summary>
    bool UdpEnabled { get; }
    int UdpPort { get; }
    /// <summary>上面任何一项变化时触发（后台线程）。</summary>
    event Action? Changed;
}

/// <summary>
/// 快捷键。插件注册的快捷键和主程序的放在一起管理：出现在「设置 → 全部快捷键」里，
/// 和其他快捷键冲突时会提示，也受「仅游戏在前台 / 运行时生效」限制。
/// </summary>
public interface IPluginHotkeys
{
    /// <summary>
    /// 注册一个快捷键。key 在本插件内唯一。handler 在线程池线程上调用，参数是按下（true）还是松开（false）。
    /// 用户设置的按键会保存下来；第一次注册时没有按键。
    /// </summary>
    void Register(string key, string title, string description, string glyph, Action<bool> handler);
}

/// <summary>模拟键鼠输入。模拟出来的按键不会触发主程序自己的快捷键。</summary>
public interface IPluginInput
{
    /// <summary>按下再松开一个键（虚拟键码），中间按住 holdMs 毫秒。会阻塞调用线程。</summary>
    void Tap(int virtualKey, int holdMs = 30);
    void KeyDown(int virtualKey);
    void KeyUp(int virtualKey);
    /// <summary>这个键现在是否按着（GetAsyncKeyState）。</summary>
    bool IsDown(int virtualKey);
    /// <summary>距离最后一次真实键鼠操作过了多少毫秒。</summary>
    long IdleMilliseconds { get; }
}

/// <summary>
/// 用主程序的风格搭界面：返回的都是 Windows 设置风格的设置卡（CommunityToolkit SettingsCard），
/// 插件也可以直接往页面里放任何 WinUI 控件。只能在界面线程上调用。
/// </summary>
public interface IPluginUi
{
    /// <summary>一个标准页面：标题、说明，往下依次添加内容。</summary>
    IPluginPage CreatePage(string title, string description = "");

    /// <summary>一行设置卡：左边图标、标题、说明，右边放 content。</summary>
    FrameworkElement Row(string title, string description, UIElement? content = null, string? glyph = null);
    FrameworkElement Toggle(string title, string description, Func<bool> get, Action<bool> set, string? glyph = null);
    FrameworkElement Number(string title, string description, double min, double max, Func<double> get, Action<double> set, double step = 1);
    FrameworkElement Text(string title, string description, Func<string> get, Action<string> set, string placeholder = "", double width = 280);
    FrameworkElement Choice(string title, string description, string[] options, Func<int> get, Action<int> set, double width = 220);
    FrameworkElement Buttons(string title, string description, params (string text, Action click)[] buttons);
    /// <summary>可展开的设置卡：header 放在右边（例如总开关），items 是展开后的子项。</summary>
    FrameworkElement Expander(string title, string description, string? glyph, UIElement? header, params UIElement[] items);
    /// <summary>提示条。severity：0 信息、1 成功、2 警告、3 错误。</summary>
    InfoBar Info(string title, string message, int severity = 0);
    /// <summary>选一个按键（虚拟键码）的设置卡：点击后按下要用的键。</summary>
    FrameworkElement Key(string title, string description, Func<int> get, Action<int> set, string? glyph = null);
    /// <summary>本插件某个快捷键的设置卡（key 是 Register 时用的那个）。</summary>
    FrameworkElement Shortcut(string key);

    Task MessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirm = "继续");
    /// <summary>让用户输入一段文字，取消时返回 null。</summary>
    Task<string?> InputAsync(string title, string message, string initial = "", string placeholder = "");
}

/// <summary>标准页面。</summary>
public interface IPluginPage
{
    /// <summary>交给 CreatePage 返回的元素。</summary>
    UIElement Element { get; }
    /// <summary>添加一个小节标题（和一行说明）。</summary>
    void Section(string title, string note = "");
    void Add(UIElement element);
    /// <summary>页面显示 / 离开时触发，用来启动和停止刷新。</summary>
    event Action? Shown;
    event Action? Hidden;
}
