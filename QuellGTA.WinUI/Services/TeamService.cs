using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QuellGTA.Services;

/// <summary>联机状态，数值与原版「联机选择夹」的页码一致。</summary>
public enum TeamMode { Offline = 0, Connecting = 1, Client = 2, Host = 3 }

/// <summary>房主看到的成员列表里的一行。</summary>
public sealed record TeamMember(string Name, string Ping, string Team, string Version, string Address);

/// <summary>
/// 联机广播（原版的 ANE-Gen2 网络引擎）：房主开一个 TCP 服务器，队员连上来；
/// 同一小队里有人杀进程 / 断网 / 卡单时，其他人收到广播后同步执行。
/// 线路格式与原版完全一致，可以和原版 0032 以后的版本互相连接：
///   &lt;A,num&gt;识别码&lt;A,size&gt;内容字节数&lt;A,data&gt;BASE64(内容)&lt;A,end&gt;
///   内容（GBK）= &lt;A,name&gt;昵称&lt;A,team&gt;小队&lt;A,act&gt;操作&lt;A,text&gt;附加内容&lt;A,end&gt;
/// 操作：HBC / HBS 心跳，VER 版本，KP 杀进程，FW1 阻止联网，BC 禁用网卡，SOLO 卡单，MSG 消息。
/// </summary>
public sealed class TeamService : IDisposable
{
    /// <summary>向对方报告的版本号，沿用移植所依据的原版 build。</summary>
    public const string BuildVersion = "0216";
    private const string HostRuleName = "QuellGTA_Host";
    private const int MaxClients = 10000;

    private static readonly Encoding Gbk;

    static TeamService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

    private readonly Settings _settings;
    private readonly LogService _log;
    private readonly SoundService _sound;
    private readonly object _gate = new();
    private readonly Timer _clock;

    // 房主
    private TcpListener? _listener;
    private readonly List<Connection> _clients = [];

    // 队员
    private Connection? _server;
    private CancellationTokenSource? _clientCts;
    private int _handshakeWait;        // 等待握手包时间（秒）
    private int _serverTtl;            // 上次客户握手（秒）
    private int _heartbeatCountdown;   // 距下一次心跳（秒）
    private long _pingSentAt = -1;     // 估算延迟[2]
    private int _lastPing = -1;        // 估算延迟[1]
    private long _lastMessage;

    public TeamMode Mode { get; private set; }
    /// <summary>界面上显示的一行状态，例如「Public（共 2 人）· 35 ms（全服在线 8 人）」。</summary>
    public string Status { get; private set; } = "";

    /// <summary>状态或成员列表变化（可能在后台线程触发）。</summary>
    public event Action? Changed;
    /// <summary>收到同小队成员的操作广播：(操作, 附加内容, 发起人)。</summary>
    public event Action<string, string, string>? ActionReceived;

    public TeamService(Settings settings, LogService log, SoundService sound)
    {
        _settings = settings;
        _log = log;
        _sound = sound;
        _clock = new Timer(_ => Tick(), null, 1000, 1000);
    }

    // ───────── 设置 ─────────

    public string Nickname
    {
        get => _settings.Get("联机", "昵称", "");
        set => _settings.Set("联机", "昵称", Limit(value.Trim(), 8));
    }
    /// <summary>小队口令。相同口令的成员才会互相同步，「Public」是公开小队。</summary>
    public string Team
    {
        get => _settings.Get("联机", "小队", "Public");
        private set => _settings.Set("联机", "小队", value);
    }
    public int Port
    {
        get => Math.Clamp(_settings.GetInt("联机", "端口", 26435), 1, 65535);
        set => _settings.SetInt("联机", "端口", Math.Clamp(value, 1, 65535));
    }
    /// <summary>上次加入的地址，「域名:端口」或「IP:端口」。</summary>
    public string Address
    {
        get => _settings.Get("联机", "域名", "");
        set => _settings.Set("联机", "域名", value.Trim());
    }

    // 「广播内容」菜单：原版把四个开关存成一个数值（默认 1），这里按位拆开。位的顺序是移植时自定的。
    private bool Flag(int bit) => (_settings.GetInt("联机", "使能广播", 1) & bit) != 0;
    private void SetFlag(int bit, bool on)
    {
        var value = _settings.GetInt("联机", "使能广播", 1);
        _settings.SetInt("联机", "使能广播", on ? value | bit : value & ~bit);
    }
    public bool BroadcastKill { get => Flag(1); set => SetFlag(1, value); }
    public bool BroadcastBlock { get => Flag(2); set => SetFlag(2, value); }
    public bool BroadcastNic { get => Flag(4); set => SetFlag(4, value); }
    public bool BroadcastSolo { get => Flag(8); set => SetFlag(8, value); }

    /// <summary>取指定字符数：原版按字节截断，这里按 GBK 字节数截断且不切开汉字。</summary>
    private static string Limit(string text, int maxBytes)
    {
        while (text.Length > 0 && Gbk.GetByteCount(text) > maxBytes) text = text[..^1];
        return text;
    }

    /// <summary>昵称和小队里不能出现协议用到的尖括号。</summary>
    private static string Clean(string text) => text.Replace("<", "").Replace(">", "");

    /// <summary>
    /// 生成昵称：优先从 Rockstar 启动器日志里取当前登录的用户名，取不到就从原版的昵称库里随机一个。
    /// </summary>
    public string GenerateNickname()
    {
        var name = "";
        try
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Rockstar Games", "Launcher", "launcher.log");
            if (File.Exists(log))
            {
                string text;
                using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    text = reader.ReadToEnd();
                name = Between(text, "User is '", "'");
                if (name.Length == 0) name = Between(text, "-epicusername=\"", "\"");
            }
        }
        catch (Exception) { }

        if (name.Length == 0)
        {
            var pool = Lang.C(38).Split('|', StringSplitOptions.RemoveEmptyEntries);
            name = (pool.Length > 0 ? pool[Random.Shared.Next(pool.Length)] : "Player")
                   + (DateTime.Now.Second + Random.Shared.Next(1, 40));
        }
        Nickname = Clean(name);
        return Nickname;
    }

    private static string Between(string text, string start, string end)
    {
        var a = text.IndexOf(start, StringComparison.Ordinal);
        if (a < 0) return "";
        a += start.Length;
        var b = text.IndexOf(end, a, StringComparison.Ordinal);
        return b > a ? text[a..b] : "";
    }

    private void EnsureIdentity()
    {
        if (Nickname is "" or "Unknown") _log.Write(Lang.C(39) + GenerateNickname());
        if (Team is "" or "Temp")
        {
            Team = "Public";
            _log.Write(Lang.C(51), 1);
        }
    }

    // ───────── 线路格式 ─────────

    public static byte[] Encode(string user, string team, string act, string text)
    {
        var payload = Gbk.GetBytes($"<A,name>{user}<A,team>{team}<A,act>{act}<A,text>{text}<A,end>");
        var id = DateTime.Now.Second + Random.Shared.Next(9, 1000);
        return Gbk.GetBytes($"<A,num>{id}<A,size>{payload.Length}<A,data>{Convert.ToBase64String(payload)}<A,end>");
    }

    public readonly record struct Packet(string User, string Team, string Act, string Text);

    /// <summary>联机接收数据：处理粘包 / 半包，按识别码去掉重发的包，校验长度。每个连接一个实例。</summary>
    public sealed class Decoder
    {
        private readonly StringBuilder _buffer = new();
        private int _lastId = int.MinValue;

        /// <summary>校验失败（长度对不上）时触发。</summary>
        public Action? Corrupted;

        public List<Packet> Feed(ReadOnlySpan<byte> data)
        {
            var packets = new List<Packet>();
            _buffer.Append(Encoding.Latin1.GetString(data)); // 外层只有 ASCII
            var text = _buffer.ToString();

            // 防止爬虫或别的软件乱发数据把缓冲区撑爆
            if (!text.Contains("<A,") || text.Length > 256 * 1024)
            {
                _buffer.Clear();
                return packets;
            }

            const string end = "<A,end>";
            int position = 0, next;
            while ((next = text.IndexOf(end, position, StringComparison.Ordinal)) >= 0)
            {
                var frame = text[position..(next + end.Length)];
                position = next + end.Length;

                if (!int.TryParse(Field(frame, "<A,num>"), out var id)) continue;
                if (id == _lastId) continue; // 重发的包
                _lastId = id;
                _ = int.TryParse(Field(frame, "<A,size>"), out var size);

                byte[] payload;
                try { payload = Convert.FromBase64String(Field(frame, "<A,data>")); }
                catch (FormatException) { payload = []; }
                if (payload.Length == 0 || payload.Length != size)
                {
                    Corrupted?.Invoke();
                    _lastId = int.MinValue;
                    continue;
                }
                var body = Gbk.GetString(payload);
                packets.Add(new Packet(Field(body, "<A,name>"), Field(body, "<A,team>"),
                    Field(body, "<A,act>"), Field(body, "<A,text>")));
            }
            _buffer.Remove(0, position);
            return packets;
        }
    }

    /// <summary>取某个标签后面、下一个同类标签之前的内容。prefix 是标签的前三个字符（&lt;A, 或 &lt;Q,）。</summary>
    private static string Field(string text, string tag)
    {
        var start = text.IndexOf(tag, StringComparison.Ordinal);
        if (start < 0) return "";
        start += tag.Length;
        var end = text.IndexOf(tag[..3], start, StringComparison.Ordinal);
        return end < 0 ? "" : text[start..end];
    }

    private sealed class Connection(TcpClient client, string address)
    {
        public TcpClient Client { get; } = client;
        public string Address { get; } = address;
        public Decoder Decoder { get; } = new();
        public string Name = "Unknown", Ping = "-", Team = "Temp", Version = "";
        public int Ttl = 6;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public async Task<bool> SendAsync(byte[] data)
        {
            try
            {
                await _writeLock.WaitAsync();
                try
                {
                    using var timeout = new CancellationTokenSource(5000);
                    await Client.GetStream().WriteAsync(data, timeout.Token);
                    return true;
                }
                finally { _writeLock.Release(); }
            }
            catch (Exception) { return false; }
        }

        public void Close()
        {
            try { Client.Close(); } catch (Exception) { }
        }
    }

    private static async Task ReadLoopAsync(Connection connection, Action<Packet> handle, CancellationToken token)
    {
        var buffer = new byte[8192];
        try
        {
            var stream = connection.Client.GetStream();
            while (!token.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, token);
                if (count <= 0) break;
                foreach (var packet in connection.Decoder.Feed(buffer.AsSpan(0, count))) handle(packet);
            }
        }
        catch (Exception) { }
    }

    private void SetMode(TeamMode mode, string status)
    {
        Mode = mode;
        Status = status;
        _settings.SetInt("状态", "联机状态", (int)mode);
        Changed?.Invoke();
    }

    private void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    // ───────── 房主 ─────────

    /// <summary>建房：在指定端口开服务器。队员需要能连到这台电脑（同一局域网，或做了端口映射 / 内网穿透）。</summary>
    public bool StartHost()
    {
        if (Mode != TeamMode.Offline) return false;
        EnsureIdentity();
        var port = Port;
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            _listener = listener;
        }
        catch (Exception ex)
        {
            _log.Write($"开启服务器失败（端口 {port} 可能被占用）：{ex.Message}", 3);
            return false;
        }

        // 原版：放行本程序的入站连接，否则别人连不进来
        var exe = Environment.ProcessPath ?? "";
        Shell.Run("netsh.exe", $"advfirewall firewall delete rule name=\"{HostRuleName}\"", wait: true);
        if (exe.Length > 0)
            Shell.Run("netsh.exe",
                $"advfirewall firewall add rule name=\"{HostRuleName}\" dir=in program=\"{exe}\" action=allow enable=yes", wait: false);

        _log.Write(Lang.C(54) + port, 1);
        SetMode(TeamMode.Host, HostStatus());
        _ = AcceptLoopAsync(_listener);
        return true;
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception) { return; } // 服务器已停止

            client.NoDelay = true;
            var connection = new Connection(client, client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString());
            connection.Decoder.Corrupted = () => _log.Write(Lang.C(82), 2);
            bool accepted;
            lock (_gate)
            {
                accepted = Mode == TeamMode.Host && _clients.Count < MaxClients;
                if (accepted) _clients.Add(connection);
            }
            if (!accepted)
            {
                connection.Close();
                _log.Write(Lang.C(124), 1);
                continue;
            }
            SetStatus(HostStatus());
            _ = ServeClientAsync(connection);
        }
    }

    private async Task ServeClientAsync(Connection connection)
    {
        await ReadLoopAsync(connection, packet => HandleAsHost(connection, packet), CancellationToken.None);
        connection.Close();
        bool removed;
        lock (_gate) removed = _clients.Remove(connection);
        if (!removed) return;
        if (connection.Name != "Unknown") _log.Write(connection.Name + Lang.C(123), 1);
        if (Mode == TeamMode.Host) SetStatus(HostStatus());
    }

    /// <summary>取小队人数：房主自己也算一个。</summary>
    private int TeamCount(string team)
    {
        lock (_gate) return _clients.Count(c => c.Team == team) + (Team == team ? 1 : 0);
    }

    private int TotalCount()
    {
        lock (_gate) return _clients.Count + 1;
    }

    private string HostStatus() => $"{Lang.C(102)}{TotalCount()}{Lang.C(103)}{TeamCount(Team)}";

    public List<TeamMember> GetMembers()
    {
        var list = new List<TeamMember> { new(Nickname, "Host", Team, BuildVersion, "127.0.0.1:" + Port) };
        lock (_gate)
            list.AddRange(_clients.Select(c => new TeamMember(c.Name, c.Ping, c.Team, c.Version, c.Address)));
        return list;
    }

    /// <summary>服务数据处理</summary>
    private void HandleAsHost(Connection from, Packet packet)
    {
        var (user, team, act, text) = packet;
        switch (act)
        {
            case "HBC":
                var changed = false;
                if (from.Name == "Unknown")
                {
                    _log.Write(user + Lang.C(101), 1);
                    _ = from.SendAsync(Encode(Nickname, Team, "VER", BuildVersion));
                    changed = true;
                }
                if (from.Name != user) { from.Name = user; changed = true; }
                if (from.Team != team) { from.Team = team; changed = true; }
                var ping = Field(text, "<Q,ping>");
                from.Ping = ping + "ms";
                from.Ttl = 12;
                _ = from.SendAsync(Encode(Nickname, team, "HBS",
                    $"<Q,tun>{TeamCount(team)}<Q,aun>{TotalCount()}<Q,end>"));
                if (changed) SetStatus(HostStatus()); else Changed?.Invoke();
                break;

            case "VER":
                from.Version = text;
                _log.Write(user + Lang.C(106) + text + VersionVerdict(text), 1);
                Changed?.Invoke();
                break;

            case "KP" or "FW1" or "BC" or "SOLO" or "MSG" when team != "Temp":
                // 先转发给同小队的其他成员，再在本机执行
                Relay(from, Encode(user, team, act, text), team);
                if (team != Team) break;
                if (act == "MSG")
                {
                    _log.Write($"{user}{Lang.C(100)}{text} ]{Lang.C(139)}", 2);
                    _sound.Play(7);
                }
                else
                {
                    ActionReceived?.Invoke(act, text, user);
                }
                break;
        }
    }

    private void Relay(Connection? except, byte[] data, string team)
    {
        Connection[] targets;
        lock (_gate) targets = _clients.Where(c => c != except && c.Team == team).ToArray();
        foreach (var target in targets) _ = target.SendAsync(data);
    }

    private static string VersionVerdict(string other)
    {
        _ = int.TryParse(BuildVersion, out var mine);
        _ = int.TryParse(other, out var theirs);
        return mine < theirs ? Lang.C(96) : mine == theirs ? Lang.C(98) : Lang.C(99);
    }

    // ───────── 队员 ─────────

    /// <summary>解析「域名:端口」或「IP:端口」。只写了地址时使用设置里的端口。</summary>
    public static bool TryParseAddress(string input, int defaultPort, out string host, out int port, out string error)
    {
        host = "";
        port = defaultPort;
        error = "";
        input = input.Trim();
        var parts = input.Split(':');
        if (input.Length == 0 || parts.Length > 2 || parts[0].Length == 0 || input.Contains('/'))
        {
            error = Lang.C(111);
            return false;
        }
        if (parts.Length == 2 && (!int.TryParse(parts[1], out port) || port is < 1 or > 65535))
        {
            error = Lang.C(117);
            return false;
        }
        host = parts[0];
        return true;
    }

    /// <summary>加入房间。返回 null 表示已开始连接，否则返回不能连接的原因。</summary>
    public string? Join(string address)
    {
        if (Mode != TeamMode.Offline) return "请先离开当前房间。";
        if (!TryParseAddress(address, Port, out var host, out var port, out var error)) return error;
        EnsureIdentity();
        Address = address;
        var cts = new CancellationTokenSource();
        lock (_gate) _clientCts = cts;
        SetMode(TeamMode.Connecting, Lang.C(107));
        _ = ConnectAsync(host, port, reconnecting: false, cts.Token);
        return null;
    }

    private async Task ConnectAsync(string host, int port, bool reconnecting, CancellationToken token)
    {
        var attempt = 1;
        while (!token.IsCancellationRequested)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(8000);
                await client.ConnectAsync(host, port, timeout.Token);
            }
            catch (Exception)
            {
                client.Dispose();
                if (token.IsCancellationRequested) return;
                if (!reconnecting)
                {
                    // 首次连接失败直接放弃；掉线之后的重连会一直重试（原版「自动重连」）
                    _log.Write(Lang.C(109), 2);
                    Leave(silent: true);
                    return;
                }
                if (++attempt >= 99) attempt = 1;
                SetStatus($"{Lang.C(120)}{attempt}{Lang.C(121)}");
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { return; }
                continue;
            }

            var connection = new Connection(client, $"{host}:{port}");
            connection.Decoder.Corrupted = () => _log.Write(Lang.C(82), 2);
            lock (_gate)
            {
                if (token.IsCancellationRequested) { connection.Close(); return; }
                _server = connection;
                _handshakeWait = 6;
                _lastPing = -1;
                _pingSentAt = -1;
            }
            SetStatus(Lang.C(108));
            await connection.SendAsync(Encode(Nickname, Team, "HBC", "<Q,ping>-<Q,end>"));
            await ReadLoopAsync(connection, HandleAsClient, token);
            connection.Close();

            bool wasConnected;
            lock (_gate)
            {
                if (_server != connection) return; // 已经由别处处理（离开、握手超时）
                _server = null;
                wasConnected = Mode == TeamMode.Client;
            }
            if (token.IsCancellationRequested) return;
            if (!wasConnected)
            {
                _log.Write(Lang.C(417), 2);
                Leave(silent: true);
                return;
            }
            // 已经握手成功之后掉线：提示并自动重连
            _sound.Play(2);
            _log.Write(Lang.C(122), 2);
            SetMode(TeamMode.Connecting, Lang.C(119));
            reconnecting = true;
            attempt = 1;
            try { await Task.Delay(1000, token); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>客户数据处理</summary>
    private void HandleAsClient(Packet packet)
    {
        var (user, team, act, text) = packet;
        switch (act)
        {
            case "HBS":
                var measured = _pingSentAt >= 0 ? (int)Math.Abs(Environment.TickCount64 - _pingSentAt) : -1;
                var shown = _lastPing >= 0 && measured >= 0 ? (measured + _lastPing) / 2 : measured;
                shown = Math.Min(shown, 999);
                _lastPing = measured;
                var teamCount = Field(text, "<Q,tun>");
                var allCount = Field(text, "<Q,aun>");
                var status = $"{team}{Lang.C(84)}{teamCount}{Lang.C(85)}· "
                             + (shown < 0 ? $"{Lang.C(86)}{allCount}{Lang.C(85)}" : $"{shown}{Lang.C(88)}{allCount}{Lang.C(85)}");
                bool first;
                lock (_gate)
                {
                    _serverTtl = 12;
                    first = Mode != TeamMode.Client;
                    if (first) _heartbeatCountdown = 5;
                }
                if (first)
                {
                    _sound.Play(3);
                    _log.Write(Lang.C(89), 2);
                    SetMode(TeamMode.Client, status);
                    _ = _server?.SendAsync(Encode(Nickname, Team, "VER", BuildVersion)); // 询问版本
                }
                else
                {
                    SetStatus(status);
                }
                break;

            case "VER":
                _log.Write(Lang.C(95) + text + VersionVerdict(text), 1);
                break;

            case "MSG":
                _log.Write($"{user}{Lang.C(100)}{text} ]{Lang.C(139)}", 2);
                _sound.Play(7);
                break;

            case "KP" or "FW1" or "BC" or "SOLO" when team != "Temp" && team == Team:
                ActionReceived?.Invoke(act, text, user);
                break;
        }
    }

    private void SendHeartbeat()
    {
        var server = _server;
        if (server is null) return;
        _pingSentAt = Environment.TickCount64;
        _ = server.SendAsync(Encode(Nickname, Team, "HBC", $"<Q,ping>{_lastPing}<Q,end>"));
    }

    /// <summary>每秒一次：心跳、握手超时、踢掉失联的成员（原版「界面时钟」里的联机部分）。</summary>
    private void Tick()
    {
        try
        {
            switch (Mode)
            {
                case TeamMode.Host:
                    Connection[] dead;
                    lock (_gate)
                    {
                        foreach (var client in _clients) client.Ttl--;
                        dead = _clients.Where(c => c.Ttl <= 0).ToArray();
                    }
                    foreach (var client in dead) client.Close(); // 读循环结束后会把它从列表里移除
                    break;

                case TeamMode.Connecting:
                    Connection? waiting;
                    lock (_gate)
                    {
                        waiting = _server;
                        if (waiting is null || --_handshakeWait > 0) break;
                        _server = null; // 标记为已处理，ConnectAsync 不再重复提示
                    }
                    waiting.Close();
                    _log.Write(Lang.C(416), 2);
                    Leave(silent: true);
                    break;

                case TeamMode.Client:
                    bool timedOut, beat;
                    lock (_gate)
                    {
                        timedOut = --_serverTtl <= 0;
                        beat = --_heartbeatCountdown <= 0;
                        if (beat) _heartbeatCountdown = 5;
                    }
                    if (timedOut)
                    {
                        _log.Write(Lang.C(17), 2);
                        _server?.Close(); // ConnectAsync 的读循环随之结束并进入自动重连
                    }
                    else if (beat)
                    {
                        SendHeartbeat();
                    }
                    break;
            }
        }
        catch (Exception) { }
    }

    // ───────── 公共操作 ─────────

    /// <summary>离开房间 / 停止服务器。</summary>
    public void Leave(bool silent = false)
    {
        TcpListener? listener;
        Connection[] clients;
        Connection? server;
        CancellationTokenSource? cts;
        TeamMode previous;
        lock (_gate)
        {
            previous = Mode;
            listener = _listener;
            _listener = null;
            clients = [.. _clients];
            _clients.Clear();
            server = _server;
            _server = null;
            cts = _clientCts;
            _clientCts = null;
            Mode = TeamMode.Offline;
        }
        cts?.Cancel();
        server?.Close();
        foreach (var client in clients) client.Close();
        if (listener is not null)
        {
            try { listener.Stop(); } catch (Exception) { }
            Shell.Run("netsh.exe", $"advfirewall firewall delete rule name=\"{HostRuleName}\"", wait: false);
        }
        if (!silent && previous == TeamMode.Host) _log.Write(Lang.C(183), 1);
        if (!silent && previous == TeamMode.Client) _log.Write("已离开房间。", 1);
        SetMode(TeamMode.Offline, "");
    }

    /// <summary>
    /// 发送广播操作：把自己的操作告诉同小队的其他成员。返回是否真的发出去了
    /// （发出去的话，调用方在断网前要稍等一下，否则广播还没离开本机网络就断了）。
    /// </summary>
    public bool Broadcast(string act, string text = "")
    {
        var team = Team;
        if (team == "Temp") return false;
        switch (Mode)
        {
            case TeamMode.Client:
                var server = _server;
                if (server is null) return false;
                _ = server.SendAsync(Encode(Nickname, team, act, text));
                _log.Write(Lang.C(68), 1);
                return true;

            case TeamMode.Host:
                int count;
                lock (_gate) count = _clients.Count(c => c.Team == team);
                if (count == 0) return false;
                Relay(null, Encode(Nickname, team, act, text), team);
                _log.Write(Lang.C(68), 1);
                return true;

            default:
                return false;
        }
    }

    /// <summary>换小队。留空或 Temp 会回到公开小队。</summary>
    public void SetTeam(string team)
    {
        team = Limit(Clean(team.Trim()), 8);
        if (team is "" or "Temp") team = "Public";
        Team = team;
        if (Mode == TeamMode.Client)
        {
            SetStatus($"{Lang.C(216)}{team} ...");
            SendHeartbeat(); // 立刻告诉房主自己换了小队
        }
        else if (Mode == TeamMode.Host)
        {
            SetStatus(HostStatus());
        }
        else
        {
            Changed?.Invoke();
        }
    }

    /// <summary>向同小队成员发一条消息（不超过 64 个字符）。成功返回 null，否则返回原因。</summary>
    public string? SendMessage(string message)
    {
        message = Clean(message.Trim());
        if (message.Length == 0) return "消息内容为空。";
        if (message.Length > 64) message = message[..64];
        if (Mode is not (TeamMode.Client or TeamMode.Host)) return "还没有加入房间。";
        if (Environment.TickCount64 - _lastMessage < 1000) return Lang.C(233);
        _lastMessage = Environment.TickCount64;

        var data = Encode(Nickname, Team, "MSG", message);
        if (Mode == TeamMode.Client) _ = _server?.SendAsync(data);
        else Relay(null, data, Team);
        _log.Write($"{Nickname}{Lang.C(100)}{message} ]{Lang.C(139)}", 1);
        _sound.Play(7);
        return null;
    }

    public void Dispose()
    {
        _clock.Dispose();
        if (Mode != TeamMode.Offline) Leave(silent: true);
    }
}
