using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuellGTA.Plugins;

namespace QuellGTA.Plugin.UdpTrigger;

/// <summary>
/// UDP 触发程序（原版「UDP触发程序」+「UDP端口设置 / udpset」）。
/// 触发程序 QuellUdpTrigger.exe 把自己的文件名作为指令发给本程序的 UDP 端口，复制一份改个名就是一个按钮。
/// 本插件负责把它复制成选中的各条指令，并写好同目录的 UDPSender.ini（目标端口取本程序当前的 UDP 端口）。
/// </summary>
public sealed class UdpTriggerPlugin : IQuellPlugin
{
    private static readonly (string command, string title)[] Commands =
    [
        ("kill", "杀掉进程"), ("fwon", "阻止联网"), ("fwoff", "恢复网络"), ("bancard", "禁用网卡"), ("single", "战局卡单"),
        ("paste", "粘贴文字"), ("winmini", "切出游戏（最小化）"), ("timerst", "计时：开始 → 结束 → 重置"),
        ("timerstart", "开始计时"), ("timerstop", "结束计时"), ("timerzero", "计时归零"),
        ("joinbot1", "加入战局甲"), ("joinbot2", "加入战局乙"),
    ];

    private IPluginHost _host = null!;

    private string TriggerExe => Path.Combine(_host.Info.Directory, "QuellUdpTrigger.exe");

    private string Folder
    {
        get => _host.Settings.Get("目录", Path.Combine(_host.DataDirectory, "触发程序"));
        set => _host.Settings.Set("目录", value.Trim());
    }
    /// <summary>触发程序自己用的端口，原版 udpset 建议在 55300–55377 里挑一个。</summary>
    private int SelfPort
    {
        get => Math.Clamp(_host.Settings.GetInt("自身端口", 55377), 1, 65534);
        set => _host.Settings.SetInt("自身端口", Math.Clamp(value, 1, 65534));
    }
    private bool Selected(string command) => _host.Settings.GetBool("选中." + command, command is "kill" or "fwon" or "fwoff");
    private void Select(string command, bool value) => _host.Settings.SetBool("选中." + command, value);

    public void Initialize(IPluginHost host) => _host = host;

    public UIElement? CreatePage()
    {
        var ui = _host.Ui;
        var page = ui.CreatePage("UDP 触发程序",
            "生成一组小程序，每个都以一条指令命名，例如 kill.exe、fwon.exe。运行它就会让本程序执行这条指令，可以放在桌面、交给 Stream Deck 或其他软件调用。它们通过本机 UDP 端口和本程序通信，不需要安装 .NET。");

        var udp = _host.State.UdpEnabled
            ? ui.Info("UDP 指令已开启", $"本程序在 127.0.0.1:{_host.State.UdpPort} 接收指令。", 1)
            : ui.Info("UDP 指令没有开启", "请先到「设置 → 外部控制」打开「UDP 指令」，否则触发程序发出的指令没有人接收。", 2);
        page.Add(udp);
        if (!File.Exists(TriggerExe))
            page.Add(ui.Info("缺少触发程序", "插件目录里没有 QuellUdpTrigger.exe（编译时需要 Visual Studio 的 C++ 生成工具来生成它）。", 3));

        page.Section("要生成的指令");
        var boxes = new List<(string command, CheckBox box)>();
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 240, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (command, title) in Commands)
        {
            var box = new CheckBox { Content = $"{command}　{title}", IsChecked = Selected(command), MinWidth = 0 };
            box.Click += (_, _) => Select(command, box.IsChecked == true);
            boxes.Add((command, box));
            grid.Children.Add(box);
        }
        page.Add(ui.Row("指令", "勾选要生成的指令。", null, ""));
        page.Add(grid);

        page.Section("位置和端口");
        page.Add(ui.Text("生成到", "文件夹不存在时自动创建。", () => Folder, v => Folder = v, "", 380));
        page.Add(ui.Number("触发程序自己的端口", "原版「UDP端口设置」里的「自身端口」，一般不用改，默认 55377。", 1, 65534, () => SelfPort, v => SelfPort = (int)v));
        page.Add(ui.Buttons("生成", "复制触发程序并写入 UDPSender.ini（目标端口是本程序当前的 UDP 端口）。已有的同名文件会被覆盖。",
            ("生成", async () => await GenerateAsync(boxes.Where(b => b.box.IsChecked == true).Select(b => b.command).ToList())),
            ("打开文件夹", OpenFolder)));
        page.Add(ui.Row("用法", "也可以自己复制 QuellUdpTrigger.exe 改名（文件名就是指令，不分大小写），旁边放一个 UDPSender.ini：[Ports] Destination=本程序的 UDP 端口，Self=触发程序自己的端口。", null, ""));
        return page.Element;
    }

    private async Task GenerateAsync(List<string> commands)
    {
        if (commands.Count == 0) { await _host.Ui.MessageAsync("UDP 触发程序", "没有勾选任何指令。"); return; }
        if (!File.Exists(TriggerExe)) { await _host.Ui.MessageAsync("UDP 触发程序", "插件目录里缺少 QuellUdpTrigger.exe，无法生成。"); return; }
        try
        {
            var folder = Folder;
            Directory.CreateDirectory(folder);
            foreach (var command in commands) File.Copy(TriggerExe, Path.Combine(folder, command + ".exe"), overwrite: true);
            File.WriteAllText(Path.Combine(folder, "UDPSender.ini"),
                $"[Ports]\r\nDestination={_host.State.UdpPort}\r\nSelf={SelfPort}\r\n", new UTF8Encoding(false));
            _host.Log($"已生成 {commands.Count} 个触发程序：{folder}", PluginLogLevel.Success);
            var note = _host.State.UdpEnabled ? "" : "\n\n注意：本程序的 UDP 指令还没有开启。";
            if (await _host.Ui.ConfirmAsync("已生成", $"生成了 {string.Join("、", commands.Select(c => c + ".exe"))}。{note}", "打开文件夹")) OpenFolder();
        }
        catch (Exception ex)
        {
            await _host.Ui.MessageAsync("生成失败", ex.Message);
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception) { }
    }

    public void Dispose() { }
}
