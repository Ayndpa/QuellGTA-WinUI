using System.Diagnostics;
using Microsoft.UI.Xaml;
using QuellGTA.Plugins;

namespace QuellGTA.Plugin.PcSettingsEditor;

/// <summary>
/// GTAOL PC设置编辑器（原版 V3.4）：编辑线上设置文件 pc_settings.bin。
///
/// 原版的五个页面在这里对应：
/// - 首页的换机列表（刷新存档列表t）→ 页面顶部的存档列表，选中的存档供下面所有功能使用；
/// - 传统编辑 → 「常用设置」「任务状态」两个标签；首页的快捷任务也放在「任务状态」里；
/// - 隧道防掉 → 「名钻隧道」；监控存档 → 「云存档」；手动编辑 → 「手动编辑」。
/// 首页的「清洗存档」主程序已有（「修复」页，换机导出时在「迁移」页），这里不重复。
/// 没有移植：检查更新、播放音乐、插件安装 / 卸载 / 自删除、繁体界面转换、释放 RTSScon.exe（锁帧由主程序完成）。
///
/// 两个编辑标签共用同一份内存里的文件内容（_doc），改动先留在内存里，点保存才写盘。
/// 名钻隧道和云存档监控各自在后台定时读文件，不受页面开关影响。
/// </summary>
public sealed class PcSettingsEditorPlugin : IQuellPlugin
{
    internal IPluginHost Host { get; private set; } = null!;

    /// <summary>当前选中的 pc_settings.bin（后台循环也读它）。</summary>
    private volatile string _path = "";
    internal string FilePath => _path;
    internal PcSettingsFile? Doc { get; private set; }
    internal string LoadError { get; private set; } = "";
    internal bool Dirty { get; set; }
    internal List<ProfileEntry> Profiles { get; private set; } = [];
    internal int SelectedProfile { get; set; } = -1;
    internal int SelectedTab { get; set; }
    private bool _scanned;

    /// <summary>本次运行里已经备份过的文件：每个文件只在第一次保存前备份一次。</summary>
    private readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>后台状态变化（任何线程上触发），页面据此刷新。</summary>
    internal event Action? StateChanged;

    public void Initialize(IPluginHost host) => Host = host;

    public UIElement? CreatePage()
    {
        if (!_scanned)
        {
            _scanned = true;
            RescanProfiles();
        }
        return new EditorPage(this).Element;
    }

    // ───────── 文件 ─────────

    /// <summary>刷新存档列表并自动选择最近使用的那个（原版 刷新存档列表t）。</summary>
    internal void RescanProfiles()
    {
        Profiles = ProfileEntry.Scan();
        var keep = Profiles.FindIndex(p => p.SettingsFile.Equals(_path, StringComparison.OrdinalIgnoreCase));
        SelectedProfile = keep >= 0 ? keep : ProfileEntry.Newest(Profiles);
        if (keep < 0 && SelectedProfile >= 0) Open(Profiles[SelectedProfile].SettingsFile);
    }

    /// <summary>打开文件，丢弃内存里没保存的改动。</summary>
    internal void Open(string path)
    {
        _path = path;
        Reload();
    }

    internal void Reload()
    {
        Dirty = false;
        LoadError = "";
        Doc = null;
        if (_path.Length == 0) return;
        try { Doc = PcSettingsFile.Load(_path); }
        catch (FileNotFoundException) { LoadError = "文件不存在，可能这个存档还没有进过线上模式。"; }
        catch (DirectoryNotFoundException) { LoadError = "文件不存在。"; }
        catch (Exception ex) { LoadError = "读取失败：" + ex.Message; }
    }

    internal bool IsReadOnly => File.Exists(_path) && File.GetAttributes(_path).HasFlag(FileAttributes.ReadOnly);

    /// <summary>锁定 / 解锁 = 设置 / 清除只读属性（原版 _锁定按钮_被单击 / _解锁按钮_被单击，原版解锁会清掉全部属性）。</summary>
    internal string SetReadOnly(bool locked)
    {
        try
        {
            var attrs = File.GetAttributes(_path);
            File.SetAttributes(_path, locked ? attrs | FileAttributes.ReadOnly : attrs & ~FileAttributes.ReadOnly);
            return locked ? "成功！文件已设为只读，需要修改请先解锁。" : "成功！文件已解除只读。";
        }
        catch (Exception) { return "失败！文件可能被占用，或者不存在。"; }
    }

    /// <summary>
    /// 游戏是否在运行。除了主程序设置的游戏版本，也像原版 _自动保存时钟_周期事件 一样看两个版本的进程名。
    /// </summary>
    internal bool GameRunning()
    {
        if (Host.Game.IsRunning) return true;
        foreach (var name in new[] { "GTA5_Enhanced", "GTA5" })
        {
            var processes = Process.GetProcessesByName(name);
            var found = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (found) return true;
        }
        return false;
    }

    /// <summary>
    /// 把内存里的内容写回文件。classic 为真时先按「传统编辑」的规则把滑块和任务状态全部写一遍（原版 _保存按钮_被单击）；
    /// 为假时原样写出（原版 _保存按钮m_被单击）。返回 null 表示成功，否则是失败原因。
    /// 第一次写某个文件前先复制一份 .bak（和主程序「修复」页的「备份设置」同名，那里可以恢复）。
    /// </summary>
    internal string? Save(bool classic, bool force = false)
    {
        if (Doc is null || _path.Length == 0) return "还没有打开文件，请先在上面选择存档。";
        if (!force && GameRunning())
            return "游戏正在运行。游戏退出时会重写 pc_settings.bin，现在保存的内容会被覆盖。请先退出游戏再保存，或者打开「游戏退出后保存」。";
        if (classic) PcLayout.ApplyClassic(Doc);
        try
        {
            Backup(_path);
            Doc.Save(_path);
            Dirty = false;
            return null;
        }
        catch (UnauthorizedAccessException) { return "文件保存失败：文件已被锁定（只读），请先解锁。"; }
        catch (Exception ex) { return "文件保存失败：" + ex.Message; }
    }

    /// <summary>快捷任务（原版 _全福按钮_被单击 等）：直接改文件，两个角色都写。</summary>
    internal string? Quick(Action<PcSettingsFile> edit)
    {
        if (_path.Length == 0) return "存档打开失败，您可能还没有选择存档！";
        if (!File.Exists(_path)) return "文件不存在，可能这个存档还没有进过线上模式。";
        if (GameRunning()) return "游戏正在运行，退出时会重写 pc_settings.bin，请先退出游戏。";
        try
        {
            var file = PcSettingsFile.Load(_path);
            edit(file);
            Backup(_path);
            file.Save(_path);
        }
        catch (UnauthorizedAccessException) { return "文件保存失败：文件已被锁定（只读），请先解锁。"; }
        catch (Exception ex) { return "文件保存失败：" + ex.Message; }
        Reload();
        return null;
    }

    private void Backup(string path)
    {
        if (!File.Exists(path) || !_backedUp.Add(path)) return;
        var bak = path + ".bak";
        if (File.Exists(bak)) File.SetAttributes(bak, File.GetAttributes(bak) & ~FileAttributes.ReadOnly);
        File.Copy(path, bak, true);
        File.SetAttributes(bak, File.GetAttributes(bak) & ~FileAttributes.ReadOnly);
        Host.Log("已备份 " + bak);
    }

    // ───────── 游戏退出后保存（原版 _自动保存时钟_周期事件，每秒看一次） ─────────

    private CancellationTokenSource? _exitWatch;
    internal bool SaveOnExit => _exitWatch is not null;

    /// <summary>
    /// 游戏退出时会把 pc_settings.bin 重写一遍，所以要在游戏运行时改设置，就得等它退出后再写。
    /// 打开后，检测到游戏从「运行中」变成「没运行」时自动按「传统编辑」保存一次（内容是界面上的当前值）。
    /// </summary>
    internal void SetSaveOnExit(bool on)
    {
        _exitWatch?.Cancel();
        _exitWatch = null;
        if (!on) return;
        var cts = _exitWatch = new CancellationTokenSource();
        var token = cts.Token;
        Task.Run(async () =>
        {
            var last = false;
            while (!token.IsCancellationRequested)
            {
                var running = GameRunning();
                if (last && !running) Host.Dispatcher.TryEnqueue(SaveAfterExit);
                last = running;
                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    private void SaveAfterExit()
    {
        var error = Save(classic: true, force: true);
        if (error is null) Host.Log("游戏已退出，已保存 pc_settings.bin。", PluginLogLevel.Success);
        else Host.Log("游戏退出后保存失败：" + error, PluginLogLevel.Error);
        StateChanged?.Invoke();
    }

    // ───────── 名钻隧道防掉线（原版 _信号灯时钟d_周期事件，每 50 毫秒） ─────────
    //
    // 增强版在等待下一段差事下载时，后台帧率很高会把加载超时缩短到不足 2 秒，名钻隧道两段差事之间就容易被踢。
    // 这里盯着所选角色的「任务进行中」字节：变成非 0（载入已完成）就解除锁帧，变回 0（差事未载入）就锁 30 帧，
    // 让加载超时变长。原版还有一个显示红绿灯的 OSD 小窗口和调整本程序音量的滑块，这里不需要：
    // 状态显示在页面和日志里，提示音音量用主程序的设置。

    private CancellationTokenSource? _tunnel;
    /// <summary>0 未启动或还没读到，1 载入已完成，-1 差事未载入。</summary>
    internal volatile int TunnelState;
    internal bool TunnelRunning => _tunnel is not null;

    internal int TunnelCharacter
    {
        get => Host.Settings.GetInt("隧道角色", 1) == 2 ? 2 : 1;
        set => Host.Settings.SetInt("隧道角色", value);
    }
    internal bool TunnelSound
    {
        get => Host.Settings.GetBool("隧道声音", true);
        set => Host.Settings.SetBool("隧道声音", value);
    }
    internal bool TunnelRtss
    {
        get => Host.Settings.GetBool("隧道锁帧", true);
        set => Host.Settings.SetBool("隧道锁帧", value);
    }

    internal void StartTunnel()
    {
        if (_tunnel is not null) return;
        var cts = _tunnel = new CancellationTokenSource();
        TunnelState = 0;
        Host.Log("名钻隧道防掉线：已启动。");
        var token = cts.Token;
        Task.Run(async () =>
        {
            var last = 0; // 原版的静态变量「上次」从 0 开始，所以启动后第一次读到就会提示一次
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(50, token); }
                catch (OperationCanceledException) { return; }
                byte[]? b;
                try { b = File.Exists(_path) ? PcSettingsFile.Load(_path).GetBytes(PcLayout.RecordA(TunnelCharacter), 0, 1) : null; }
                catch (Exception) { continue; } // 游戏正在写文件
                if (b is null) continue;
                var state = b[0] != 0 ? 1 : -1;
                if (state == last || token.IsCancellationRequested) continue;
                last = state;
                TunnelState = state;
                OnTunnelChanged(state == 1);
            }
        });
        StateChanged?.Invoke();
    }

    /// <summary>原版 信号灯控制d / 音效控制d / 锁帧控制d。在后台线程上调用。</summary>
    private void OnTunnelChanged(bool loaded)
    {
        Host.Log(loaded ? "名钻隧道：载入已完成" : "名钻隧道：差事未载入",
            loaded ? PluginLogLevel.Success : PluginLogLevel.Warning);
        var rtssDone = TunnelRtss && Host.SetFrameLimit(loaded ? 0 : 30);
        // 锁帧成功时主程序自己会播放锁帧 / 解除的提示音，这里就不再重复播放
        if (TunnelSound && !rtssDone) Host.PlaySound(loaded ? 16 : 18);
        StateChanged?.Invoke();
    }

    /// <summary>停止时解除锁帧（原版 _停止按钮d_被单击 里的 RTSS_应用帧数 (0)；只在用了锁帧时才动 RTSS）。</summary>
    internal void StopTunnel()
    {
        if (_tunnel is null) return;
        _tunnel.Cancel();
        _tunnel = null;
        TunnelState = 0;
        if (TunnelRtss) Task.Run(() => Host.SetFrameLimit(0));
        Host.Log("名钻隧道防掉线：已停止。");
        StateChanged?.Invoke();
    }

    // ───────── 监控云存档（原版 _存档时钟_周期事件，每秒） ─────────

    private CancellationTokenSource? _cloud;
    internal bool CloudRunning => _cloud is not null;
    private readonly List<string> _cloudLog = [];

    internal bool CloudSound
    {
        get => Host.Settings.GetBool("云存档声音", true);
        set => Host.Settings.SetBool("云存档声音", value);
    }

    internal string CloudLogText
    {
        get { lock (_cloudLog) return string.Join(Environment.NewLine, _cloudLog); }
    }

    internal void ClearCloudLog()
    {
        lock (_cloudLog) _cloudLog.Clear();
        StateChanged?.Invoke();
    }

    private void CloudLog(string text)
    {
        lock (_cloudLog)
        {
            _cloudLog.Add($"{DateTime.Now:HH:mm:ss}    {text}");
            if (_cloudLog.Count > 500) _cloudLog.RemoveAt(0);
        }
        StateChanged?.Invoke();
    }

    internal void StartCloud()
    {
        if (_cloud is not null) return;
        var cts = _cloud = new CancellationTokenSource();
        var token = cts.Token;
        CloudLog("开始监测云存档");
        Task.Run(async () =>
        {
            string lastPath = "", lastStamp = "";
            while (!token.IsCancellationRequested)
            {
                var path = _path;
                if (path != lastPath) (lastPath, lastStamp) = (path, ""); // 换了存档就重新开始比较
                string? stamp = null;
                try { if (File.Exists(path)) stamp = PcLayout.CloudStamp(PcSettingsFile.Load(path)); }
                catch (Exception) { }
                if (stamp is not null)
                {
                    if (lastStamp.Length > 0 && stamp != lastStamp && !token.IsCancellationRequested)
                    {
                        CloudLog("监测到云存档保存成功！");
                        Host.Log("监测到云存档保存成功！", PluginLogLevel.Success);
                        if (CloudSound) Host.PlaySound(21); // 和主程序「网络」页的云存档提示同一个声音
                    }
                    lastStamp = stamp;
                }
                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    internal void StopCloud()
    {
        if (_cloud is null) return;
        _cloud.Cancel();
        _cloud = null;
        CloudLog("停止监测云存档");
    }

    /// <summary>原版 __启动窗口_将被销毁：防掉线还开着时解除锁帧。</summary>
    public void Dispose()
    {
        SetSaveOnExit(false);
        StopCloud();
        if (_tunnel is not null)
        {
            _tunnel.Cancel();
            _tunnel = null;
            if (TunnelRtss) Task.Run(() => Host.SetFrameLimit(0)).Wait(TimeSpan.FromSeconds(3));
        }
    }
}
