# QuellGTA 插件

原版随附的外置小工具（PC 设置编辑器、热键穿透、UDP 触发程序……）在 WinUI 3 移植版里都做成了插件。插件是普通的 .NET 8 类库，主程序启动时动态加载，界面直接显示在主程序的导航栏里。

## 本目录里的插件

| 文件夹 | 插件 | 对应原版 |
|---|---|---|
| `PcSettingsEditor` | PC 设置编辑器：滑块调整线上设置、任务 / 分红关标记、原始记录编辑与导出、名钻隧道防掉线、云存档监控 | `扩展插件/GTAOL_PC设置编辑器`（V3.4） |
| `HotkeyPassthrough` | 热键穿透：游戏在前台时让其他软件的全局快捷键照样生效 | `扩展插件/Others/HotKeySavior` |
| `UdpTrigger` | UDP 触发程序：生成 `kill.exe`、`fwon.exe` 这类一运行就让主程序执行指令的小程序 | `扩展插件/Others/UDP触发程序`、`udpset` |
| `WeaponWheel` | 武器选单优化器：把武器轮盘键按足 250 毫秒 | `扩展插件/Others/武器选单优化器` |
| `FlightTimer` | 飞行变强计时：用按键自动掐表 | `扩展插件/Others/飞行变强计时工具`（V1.1） |
| `EnsureStart` | 确保启动：游戏窗口没出现就重新启动 | `扩展插件/QuellKJCC/确保启动` |
| `KeyTester` | 按键间隔测试：看模拟按键的节奏 | `测试工具/fptest` |

UDP 触发程序本身（`QuellUdpTrigger.exe`）在仓库根目录的 `QuellGTA.UdpTrigger`，用 Native AOT 编译成不依赖 .NET 运行时的 1 MB 小程序，编译插件时自动生成（需要 Visual Studio 的 C++ 生成工具）。

## 插件放在哪里

- **随程序发布**：程序目录下的 `Plugins\<名字>\`。编译主程序时，本目录下的每个插件项目都会被编译并复制到那里。默认启用。
- **第三方**：`%AppData%\MageAngela\QuellGTA\Plugins\<名字>\`。默认不启用：主程序只读 `plugin.json` 列出来，用户在「插件」页确认后才加载。

启用、停用都在重启主程序后生效。

## 写一个插件

1. 新建一个 .NET 8 类库，目标框架 `net8.0-windows10.0.19041.0`，`UseWinUI`，引用 `QuellGTA.Plugins.Abstractions`（**不要**把它和 WinUI 的程序集复制到输出目录——运行时用主程序那一份）。放在本目录下的话，`Directory.Build.props` 已经替你设好了这些，项目文件只需要写程序集名。
2. 写一个实现 `IQuellPlugin` 的公开类：

   ```csharp
   public sealed class HelloPlugin : IQuellPlugin
   {
       private IPluginHost _host = null!;

       public void Initialize(IPluginHost host)
       {
           _host = host;
           host.Hotkeys.Register("hello", "打个招呼", "在日志里写一行。", "", down => { if (down) host.Log("你好！"); });
       }

       public UIElement? CreatePage()
       {
           var page = _host.Ui.CreatePage("你好", "一个示例插件。");
           page.Add(_host.Ui.Shortcut("hello"));
           page.Add(_host.Ui.Toggle("开关", "存在 [插件.hello] 节里。",
               () => _host.Settings.GetBool("开关", false), v => _host.Settings.SetBool("开关", v)));
           return page.Element;
       }

       public void Dispose() { }
   }
   ```

3. 在输出目录里放一个 `plugin.json`：

   ```json
   {
     "id": "hello",
     "name": "你好",
     "description": "一个示例插件。",
     "version": "1.0.0",
     "author": "你",
     "glyph": "",
     "assembly": "Hello.dll",
     "api": 1
   }
   ```

   `id` 只能用小写字母、数字和 `-`。`api` 写编译时 `PluginApi.Version` 的值，比主程序支持的新就不会被加载。

`IPluginHost` 提供的能力见 `QuellGTA.Plugins.Abstractions/PluginApi.cs`：日志、独立的设置节和数据目录、游戏进程和存档目录、主程序状态、执行主程序指令（和 UDP 指令相同）、注册快捷键（和主程序的快捷键一起管理、冲突检测）、模拟键鼠、RTSS 锁帧，以及一组和主程序风格一致的设置卡与对话框。

注意：

- 页面用代码搭（不要用 XAML 文件）：从动态加载的程序集里创建 XAML 页面拿不到类型信息。
- `Initialize` 和 `CreatePage` 在界面线程上调用；快捷键回调、游戏启动 / 退出事件在后台线程上，改界面要经过 `host.Dispatcher`。
- 插件和主程序一样以管理员权限运行，WinUI 的类型加载后无法卸载，所以插件不能热重载。

## 没有做成插件的原版小工具

| 原版 | 原因 |
|---|---|
| 根据任务启停（Build 0123 的主程序变体） | 它多出来的「指纹随任务切换」已经并进主程序（「骇入」页 → 自动指纹 → 随任务自动切换），其余部分就是主程序本身 |
| JoystickSim 安装器（ptj4q）、自动骇入指纹安装器（gfpc4q）、插件包（pp4q） | 安装外置 exe 用的，对应功能（虚拟手柄、自动指纹、自动骇入）已经内置在主程序里 |
| QuellKJCC、getLxcoo、SMAG 服务、SMAGBot、AuxSync | 差传服务和镜像站的运营端工具，不是给玩家用的；其中 SMAGBot 会读取启动器日志里的账号名并用写死在源码里的邮箱密码发出去、能被远程指令强制重启电脑，KJCC_CMD 会改写系统的 `steam://` 协议处理程序，都不适合移植 |
| QuellGTA_serverTest | 很早版本的联机测试工具，协议已经不用了 |
| gg（假 GTA 窗口） | 测试用的替身程序，移植版测试时用的是自己写的替身 |
