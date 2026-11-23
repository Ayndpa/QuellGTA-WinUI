using static QuellGTA.Native.NativeMethods;

namespace QuellGTA.Services;

/// <summary>
/// QuellCore 的第四部分：RTSS 锁帧控制（三个预设档位、热键循环）和用 RTSS 的 OSD 显示计时与准星。
/// </summary>
public sealed partial class QuellCore
{
    public RtssService Rtss { get; } = new();

    private Timer? _rtssTimer;
    private string _rtssLastOsd = "";
    private long _lastFrameLimit;

    private void InitializeRtss()
    {
        Overlay.SuppressTimer = RtssOsdTimer;
        Overlay.SuppressCrosshair = RtssOsdCrosshair;
        _rtssTimer = new Timer(_ => RtssOsdTick(), null, 1000, 100);
    }

    private void DisposeRtss()
    {
        _rtssTimer?.Dispose();
        Rtss.Dispose();
        if (RtssAutoClose && RtssService.IsRunning) RtssService.Close(RtssForceClose);
    }

    // ───────── 设置 ─────────

    /// <summary>开启 RTSS 锁帧控制。原版默认值：检测到装了 RTSS 就开。</summary>
    public bool RtssLimiter
    {
        get => Settings.GetBool("扩展", "锁帧使能", RtssService.IsInstalled);
        set => Settings.SetBool("扩展", "锁帧使能", value);
    }
    /// <summary>仅游戏进程：改的是 RTSS 里游戏进程自己的配置；关闭则改全局配置。</summary>
    public bool RtssOnlyGame
    {
        get => Settings.GetBool("扩展", "锁帧仅游戏", true);
        set => Settings.SetBool("扩展", "锁帧仅游戏", value);
    }
    /// <summary>在 OSD 的状态行里显示当前锁的帧数。</summary>
    public bool RtssShowLimit
    {
        get => Settings.GetBool("扩展", "锁帧OSD", true);
        set => Settings.SetBool("扩展", "锁帧OSD", value);
    }
    /// <summary>要用到 RTSS 而它没在运行时，自动把它启动起来。</summary>
    public bool RtssAutoStart
    {
        get => Settings.GetBool("扩展", "rtss自启", false);
        set => Settings.SetBool("扩展", "rtss自启", value);
    }
    /// <summary>本程序退出时顺便关闭 RTSS。</summary>
    public bool RtssAutoClose
    {
        get => Settings.GetBool("扩展", "rtss自关", false);
        set => Settings.SetBool("扩展", "rtss自关", value);
    }
    public bool RtssForceClose
    {
        get => Settings.GetBool("扩展", "rtss强关", false);
        set => Settings.SetBool("扩展", "rtss强关", value);
    }
    /// <summary>用 RTSS 显示计时（内置的计时浮窗随之隐藏）。独占全屏下内置浮窗显示不出来，RTSS 的 OSD 可以。</summary>
    public bool RtssOsdTimer
    {
        get => Settings.GetBool("扩展", "rtss计时", false);
        set { Settings.SetBool("扩展", "rtss计时", value); Overlay.SuppressTimer = value; }
    }
    /// <summary>用 RTSS 显示准星（内置的准星浮窗随之隐藏）。</summary>
    public bool RtssOsdCrosshair
    {
        get => Settings.GetBool("扩展", "rtss准星", false);
        set { Settings.SetBool("扩展", "rtss准星", value); Overlay.SuppressCrosshair = value; }
    }
    /// <summary>RTSS 准星用的字符，原版默认 *。</summary>
    public string RtssCrosshairText
    {
        get => Settings.Get("扩展", "准星RTSS字符", "*");
        set => Settings.Set("扩展", "准星RTSS字符", SanitizeOsd(value.Length > 4 ? value[..4] : value));
    }
    /// <summary>计时在 OSD 上的位置（RTSS 的坐标单位，从画面左上角算起）。</summary>
    public int RtssTimerX
    {
        get => Math.Clamp(Settings.GetInt("扩展", "rtss计时X", 10), 0, 4000);
        set => Settings.SetInt("扩展", "rtss计时X", Math.Clamp(value, 0, 4000));
    }
    public int RtssTimerY
    {
        get => Math.Clamp(Settings.GetInt("扩展", "rtss计时Y", 10), 0, 4000);
        set => Settings.SetInt("扩展", "rtss计时Y", Math.Clamp(value, 0, 4000));
    }

    /// <summary>三个帧数预设：0 = 不限制，-1 = 这一档停用（循环时跳过）。原版默认 30 | 停用 | 不限制。</summary>
    public int[] RtssPresets
    {
        get
        {
            var defaults = new[] { 30, -1, 0 };
            var parts = Settings.Get("本地", "锁帧预设", "30|-1|0").Split('|');
            if (parts.Length != 3) return defaults;
            for (var i = 0; i < 3; i++)
                if (int.TryParse(parts[i], out var value) && value is >= -1 and <= 999) defaults[i] = value;
            return defaults;
        }
        set => Settings.Set("本地", "锁帧预设", string.Join('|', value.Take(3).Select(v => Math.Clamp(v, -1, 999))));
    }

    // ───────── 锁帧 ─────────

    /// <summary>最近一次应用或读到的帧数上限；-1 表示还不知道。</summary>
    public int RtssCurrentLimit { get; private set; } = -1;
    /// <summary>锁帧总开关的状态；null 表示读不到（RTSS 没装）。</summary>
    public bool? RtssLimiterActive { get; private set; }

    private string RtssProfile => RtssOnlyGame ? Game.ProcessName : "";

    public static string FrameLimitText(int fps) => fps switch { 0 => "Unlimited", -1 => "...", _ => fps + " FPS" };

    /// <summary>从 RTSS 读回当前状态（原版「RTSS_信息获取」）。</summary>
    public void RefreshRtssState()
    {
        RtssLimiterActive = Rtss.LimiterEnabled;
        RtssCurrentLimit = Rtss.GetFramerateLimit(RtssProfile) ?? -1;
        Notify();
    }

    private void EnsureRtssRunning()
    {
        if (!RtssAutoStart || RtssService.IsRunning) return;
        if (RtssService.Start()) Log.Write(Lang.C(83) + "RTSS.exe", 1);
    }

    /// <summary>应用帧数上限并打开锁帧总开关（原版「RTSS_应用帧数」）。0 = 不限制。</summary>
    public bool ApplyFrameLimit(int fps)
    {
        if (!RtssLimiter) { Log.Write(Lang.C(457)); return false; }
        fps = Math.Clamp(fps, 0, 999);
        if (!Rtss.SetFramerateLimit(RtssProfile, fps) || !Rtss.SetLimiterEnabled(true))
        {
            Log.Write("RTSS 锁帧：没有找到 RTSS，请先安装。", 2);
            return false;
        }
        Sound.Play(fps == 0 ? 16 : 18);
        EnsureRtssRunning();
        RtssCurrentLimit = fps;
        RtssLimiterActive = true;
        Log.Write("RTSS 锁帧：" + FrameLimitText(fps), 1);
        Notify();
        return true;
    }

    /// <summary>打开或关闭 RTSS 的锁帧总开关，不改帧数。</summary>
    public bool SetRtssLimiterActive(bool active)
    {
        if (!Rtss.SetLimiterEnabled(active)) { Log.Write("RTSS 锁帧：没有找到 RTSS，请先安装。", 2); return false; }
        RtssLimiterActive = active;
        Notify();
        return true;
    }

    /// <summary>
    /// 切到下一个预设（原版「RTSS_循环预设」）：当前帧数等于哪一档，就换到它后面第一个没停用的档；
    /// 都对不上时从第一档开始。
    /// </summary>
    public void CycleFrameLimit()
    {
        if (!RtssLimiter) { Log.Write(Lang.C(457)); return; }
        if (Now - Interlocked.Exchange(ref _lastFrameLimit, Now) < 200) return;
        if (RtssManualInput)
        {
            // 「手动输入帧数」模式：快捷键不切预设，而是弹窗让你输入
            FrameLimitInputRequested?.Invoke();
            return;
        }
        if (RtssCurrentLimit < 0) RtssCurrentLimit = Rtss.GetFramerateLimit(RtssProfile) ?? -1;
        if (NextPreset(RtssPresets, RtssCurrentLimit) is { } next) ApplyFrameLimit(next);
    }

    /// <summary>锁帧快捷键的方式：false 循环预设，true 每次弹窗手动输入帧数（原版「锁帧仅输入」）。</summary>
    public bool RtssManualInput
    {
        get => Settings.GetBool("扩展", "锁帧仅输入", false);
        set => Settings.SetBool("扩展", "锁帧仅输入", value);
    }

    /// <summary>手动输入模式下按了锁帧键：界面应弹出输入框，然后调用 ApplyFrameLimit。</summary>
    public event Action? FrameLimitInputRequested;

    /// <summary>循环预设的选择逻辑，单独拿出来方便测试。没有可用的档位时返回 null。</summary>
    public static int? NextPreset(int[] presets, int current)
    {
        var active = current < 0 ? -1 : Array.IndexOf(presets, current);
        for (var step = 1; step <= 3; step++)
        {
            var index = active < 0 ? step - 1 : (active + step) % 3;
            if (active >= 0 && index == active) break;
            if (presets[index] != -1) return presets[index];
        }
        return null;
    }

    // ───────── OSD ─────────

    /// <summary>OSD 文字里不能出现尖括号（会被当成排版标签）和控制字符；RTSS 的字体也不一定有中文，只留可打印的 ASCII。</summary>
    private static string SanitizeOsd(string text) =>
        new(text.Where(c => c is >= ' ' and <= '~' and not '<' and not '>').ToArray());

    /// <summary>拼出要显示在 OSD 上的内容，格式参照原版「RTSS_刷新显示_线程」的竖排 / 横排样式。两项都没开时返回空串。</summary>
    public string BuildOsdText()
    {
        var text = "";
        if (RtssOsdTimer && Overlay.TimerEnabled)
        {
            var status = "";
            if (NetBlocked) status += "BLOCK ";
            if (NicDisabled) status += "NIC ";
            if (SoloActive) status += "SOLO ";
            if (RtssLimiter && RtssShowLimit && RtssLimiterActive == true && RtssCurrentLimit > 0) status += RtssCurrentLimit + " ";
            var time = Overlay.FormatTimer(Overlay.TimerMilliseconds);
            var dot = time.LastIndexOf('.');
            var size = Overlay.TimerFontSize * 8;
            var signature = SanitizeOsd(Overlay.TimerSignature);
            var head = $"<P={RtssTimerX},{RtssTimerY}><C0={Overlay.TimerColor}><C1={Overlay.OsdColor}><C2={Overlay.SignatureColor}><S0={size}><S1=-{size / 2}>";
            text += Overlay.TimerHorizontal
                // 横排：签名、状态、时间排成一行（原版的横排样式）
                ? head + $"<S0><C2>{(signature.Length > 0 ? signature + "   " : "")}<C><C1>{status}<S><C><C0><S0>  {time[..dot]}<S><S1> {time[(dot + 1)..]}<S><C>"
                : head + $"<C1><S1>{status}<S><C><Br><C0><S0>{time[..dot]}<S><S1> {time[(dot + 1)..]}<S><C>"
                       + (signature.Length > 0 ? $"<Br><C2><S1>{signature}<S><C>" : "");
        }
        if (RtssOsdCrosshair && Overlay.CrosshairEnabled && (!Overlay.CrosshairOnlyForeground || Game.IsForeground()))
        {
            // 原版的换算：屏幕中心加偏移、减去半个准星，再乘 0.5 换成 RTSS 的坐标单位
            var size = Overlay.CrosshairSize;
            var x = Math.Max(0, (int)((GetSystemMetrics(0) / 2 + Overlay.CrosshairOffsetX - size / 2 + 1) * 0.5));
            var y = Math.Max(0, (int)((GetSystemMetrics(1) / 2 + Overlay.CrosshairOffsetY - size / 2 + 1) * 0.5));
            text += $"<P={x},{y}><C0={Overlay.CrosshairColor}><S0={size * 10}><C0><S0>{RtssCrosshairText}<S><C>";
        }
        return text;
    }

    private int _rtssBusy;

    private void RtssOsdTick()
    {
        if (Interlocked.Exchange(ref _rtssBusy, 1) == 1) return;
        try
        {
            var text = GameRunning || !string.IsNullOrEmpty(_rtssLastOsd) ? BuildOsdText() : "";
            if (!GameRunning) text = "";
            if (text == _rtssLastOsd) return;
            if (text.Length == 0) { Rtss.ClearOsd(); _rtssLastOsd = ""; return; }
            // 写不进去（RTSS 没运行）时不记下来，下一次接着试
            if (Rtss.UpdateOsd(text)) _rtssLastOsd = text;
        }
        catch (Exception) { }
        finally { Volatile.Write(ref _rtssBusy, 0); }
    }
}
