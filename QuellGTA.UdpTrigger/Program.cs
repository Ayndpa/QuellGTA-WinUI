using System.Net;
using System.Net.Sockets;
using System.Text;

// UDP 触发程序：把自己的文件名（不含扩展名）发给本机 QuellGTA 的 UDP 指令端口，然后退出。
// 端口读同目录的 UDPSender.ini（和原版同一个文件、同样的键）：
//   [Ports]
//   Destination=55378   ; QuellGTA 接收指令的端口（「设置 → 外部控制 → UDP 指令」）
//   Self=55377          ; 本程序自己用的端口，没写时在 55300–55377 里随机挑一个
// 原版用一个易语言窗口程序完成，这里是没有窗口的原生程序。

var exe = Environment.ProcessPath ?? "";
var command = Path.GetFileNameWithoutExtension(exe);
var ini = Path.Combine(AppContext.BaseDirectory, "UDPSender.ini");
var destination = ReadPort(ini, "Destination") ?? 55378;
var self = ReadPort(ini, "Self") ?? Random.Shared.Next(55300, 55378);

try
{
    using var udp = BindSocket(self);
    // 原版发送的是 ANSI 文本；指令都是英文，按 UTF-8 发结果相同
    var bytes = Encoding.UTF8.GetBytes(command);
    udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, destination));
    return 0;
}
catch (Exception)
{
    return 1;
}

static UdpClient BindSocket(int port)
{
    try { return new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); }
    catch (SocketException) { return new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); } // 端口被占用（比如同时运行两个）时随便用一个
}

static int? ReadPort(string path, string key)
{
    if (!File.Exists(path)) return null;
    var inPorts = false;
    foreach (var raw in File.ReadLines(path))
    {
        var line = raw.Trim();
        if (line.StartsWith('[')) { inPorts = line.Equals("[Ports]", StringComparison.OrdinalIgnoreCase); continue; }
        if (!inPorts) continue;
        var eq = line.IndexOf('=');
        if (eq <= 0 || !line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
        return int.TryParse(line[(eq + 1)..].Trim(), out var port) && port is > 0 and < 65535 ? port : null;
    }
    return null;
}
