using System.Text;

namespace QuellGTA.Services;

/// <summary>
/// 差事候车：盯着一个「差事克隆 Bot」的状态页，轮到可以上车时提醒你（或自动加入它的战局），差事开始时提醒你卡单
/// （或自动卡单）。移植自原版的「刷新候车jc / _候车时钟jc_周期事件」。
/// 状态页是一个网页，里面用 &lt;BotState&gt;…&lt;/BotState&gt; 标出 Bot 当前的状态，用 &lt;SteamJVP&gt;…&lt;/SteamJVP&gt; 标出它的战局。
/// 状态：Waitjoin 可以加入、Preparing 准备中、RunJob 正在启动差事、JobStart 差事已开始、Offline 离线。
/// </summary>
public sealed class WaitService(Settings settings, QuellCore core)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private CancellationTokenSource? _cts;
    private string _lastState = "null";
    private bool _joined, _sawJoinWindow;
    private readonly List<string> _lines = [];

    public bool Running => _cts is not null;
    public string Url { get; private set; } = "";

    /// <summary>候车记录有新内容，或开始 / 结束了候车。</summary>
    public event Action? Changed;

    public bool AutoJoin
    {
        get => settings.GetBool("扩展", "候车自动加入", false);
        set => settings.SetBool("扩展", "候车自动加入", value);
    }
    public bool AutoSolo
    {
        get => settings.GetBool("扩展", "候车自动卡单", false);
        set => settings.SetBool("扩展", "候车自动卡单", value);
    }
    /// <summary>用过的状态页地址，最近的在前。</summary>
    public string[] History
    {
        get => settings.Get("扩展", "候车历史地址", "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        private set => settings.Set("扩展", "候车历史地址", string.Join('|', value));
    }

    public void ClearHistory() => History = [];

    public string Transcript
    {
        get { lock (_lines) return string.Join(Environment.NewLine, _lines); }
    }

    private void Note(string text)
    {
        lock (_lines)
        {
            _lines.Add($"{DateTime.Now:HH:mm:ss} {text}");
            if (_lines.Count > 200) _lines.RemoveAt(0);
        }
        Changed?.Invoke();
    }

    private static bool IsUrl(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>开始候车。成功返回 null，否则返回原因。</summary>
    public string? Start(string url)
    {
        url = url.Trim();
        if (Running) return "已经在候车了。";
        if (!IsUrl(url)) return Lang.C(573);
        if (!core.GameRunning) return Lang.C(582);

        Url = url;
        History = History.Where(h => h != url).Prepend(url).Take(10).ToArray();
        _lastState = "null";
        _joined = _sawJoinWindow = false;
        lock (_lines) _lines.Clear();
        var cts = _cts = new CancellationTokenSource();
        Note(Lang.C(566));
        _ = Task.Run(() => LoopAsync(cts.Token));
        return null;
    }

    public void Stop(bool silent = false)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        cts.Cancel();
        if (!silent) Note(Lang.C(567));
        else Changed?.Invoke();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int delay;
            try { delay = await RefreshAsync(token); }
            catch (Exception) { delay = 6000; }
            if (delay < 0) { Stop(); return; }
            try { await Task.Delay(delay, token); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task<string?> FetchAsync(string tag, CancellationToken token)
    {
        try
        {
            var page = Encoding.UTF8.GetString(await Http.GetByteArrayAsync(Url, token));
            var start = page.IndexOf($"<{tag}>", StringComparison.OrdinalIgnoreCase);
            var end = page.IndexOf($"</{tag}>", StringComparison.OrdinalIgnoreCase);
            return start < 0 || end <= start ? null : page[(start + tag.Length + 2)..end].Trim();
        }
        catch (Exception) when (!token.IsCancellationRequested) { return null; }
    }

    /// <summary>查一次状态并处理。返回多少毫秒后再查，负数表示候车应当结束。</summary>
    private async Task<int> RefreshAsync(CancellationToken token)
    {
        if (!core.GameRunning)
        {
            Note(Lang.C(584));
            return -1;
        }
        var state = await FetchAsync("BotState", token);
        if (state is null)
        {
            Note(Lang.C(573));
            return _lastState == "null" ? -1 : 6000; // 第一次就取不到，说明地址不对，直接结束
        }
        if (state == _lastState) return NextDelay(state);

        var delay = 6000;
        switch (state)
        {
            case "Waitjoin":
                Note($"[{Lang.C(561)}] {Lang.C(568)}");
                if (AutoJoin)
                {
                    await JoinAsync(token);
                    Note(Lang.C(578));
                }
                _sawJoinWindow = true;
                core.Sound.Play(24);
                delay = 3000;
                break;

            case "Offline":
                Note($"[{Lang.C(562)}] {Lang.C(569)}");
                delay = 10000;
                break;

            case "RunJob":
                core.Sound.Play(25);
                Note($"[{Lang.C(563)}] {(_joined ? Lang.C(570) : Lang.C(574))}");
                delay = _joined ? 1000 : 6000;
                break;

            case "JobStart":
                if (_joined)
                {
                    Note($"[{Lang.C(564)}] {Lang.C(571)}");
                    if (AutoSolo)
                    {
                        if (!core.SoloActive)
                        {
                            core.ToggleSolo();
                            Note(Lang.C(579));
                        }
                        else
                        {
                            Note(Lang.C(580));
                        }
                    }
                }
                else
                {
                    Note($"[{Lang.C(564)}] {Lang.C(574)}");
                }
                if (_sawJoinWindow)
                {
                    // 这一轮的上车窗口已经过去了
                    if (!_joined) Note(Lang.C(575));
                    _lastState = state;
                    return -1;
                }
                break;

            case "Preparing":
                Note($"[{Lang.C(565)}] {Lang.C(572)}");
                break;

            default:
                Note(Lang.C(583));
                return 6000; // 不认识的状态不记下来，下次继续判断
        }
        _lastState = state;
        return delay;
    }

    private int NextDelay(string state) => state switch
    {
        "Waitjoin" => 3000,
        "Offline" => 10000,
        "RunJob" when _joined => 1000,
        _ => 6000,
    };

    /// <summary>加入 Bot 的战局（手动点「加入战局」或自动加入时调用）。</summary>
    public async Task JoinAsync(CancellationToken token = default)
    {
        if (Url.Length == 0) return;
        var jvp = await FetchAsync("SteamJVP", token);
        if (jvp is null || !BotService.IsValidJvp(jvp))
        {
            Note(Lang.C(557));
            return;
        }
        _joined = true;
        await core.Bots.JoinTargetAsync(jvp);
    }

    /// <summary>当前 Bot 是否正处在「可以加入」的状态（不是的话加入多半会失败）。</summary>
    public bool CanJoinNow => _lastState == "Waitjoin";
}
