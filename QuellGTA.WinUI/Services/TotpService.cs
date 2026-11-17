using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuellGTA.Services;

public sealed class TotpToken
{
    public string Name { get; set; } = "";
    /// <summary>Base32 编码的密钥。</summary>
    public string Secret { get; set; } = "";
}

/// <summary>
/// 电子令牌：标准 TOTP（HMAC-SHA1、30 秒、6 位），用来生成 Rockstar 账号两步验证的验证码。
/// 算法移植自原版「TOTP_验证码 / 计算TOTP验证码 / base32_解码」。
/// 存储方式与原版不同：密钥用 Windows 数据保护（DPAPI）加密后保存，只有当前 Windows 用户能解开，
/// 因此不需要原版那样另设密码，但文件拷到别的电脑或别的用户下无法使用。
/// </summary>
public sealed class TotpService
{
    private const int Period = 30, Digits = 6;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private readonly string _file;
    public List<TotpToken> Tokens { get; private set; } = [];

    /// <summary>加载失败时的原因（例如文件来自其他用户）。</summary>
    public string? LoadError { get; private set; }

    public TotpService(Settings settings)
    {
        _file = Path.Combine(settings.DataDirectory, "Totp.dat");
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_file)) return;
        try
        {
            var json = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(_file)));
            Tokens = JsonSerializer.Deserialize<List<TotpToken>>(json) ?? [];
        }
        catch (Exception ex)
        {
            LoadError = "令牌文件无法解密（可能来自其他电脑或用户）：" + ex.Message;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllBytes(_file, Dpapi.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Tokens))));
        LoadError = null;
    }

    /// <summary>
    /// 添加令牌。input 可以是 Base32 密钥，也可以是 otpauth://totp/... 链接（二维码里的内容）。
    /// 成功返回 null，失败返回原因。
    /// </summary>
    public string? Add(string name, string input)
    {
        input = input.Trim();
        var secret = input;
        if (input.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return "链接格式不正确。";
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].ToLowerInvariant(), p => Uri.UnescapeDataString(p[1]));
            if (!query.TryGetValue("secret", out secret!)) return "链接里没有密钥。";
            if (string.IsNullOrWhiteSpace(name))
                name = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        }

        secret = new string(secret.ToUpperInvariant().Where(c => c != ' ' && c != '-' && c != '=').ToArray());
        if (secret.Length < 8 || secret.Any(c => !Alphabet.Contains(c))) return "密钥不是有效的 Base32 文本。";
        name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim();
        if (Tokens.Any(t => t.Name == name)) return "已经有同名的令牌。";

        Tokens.Add(new TotpToken { Name = name, Secret = secret });
        try { Save(); }
        catch (Exception ex)
        {
            Tokens.RemoveAt(Tokens.Count - 1);
            return "保存失败：" + ex.Message;
        }
        return null;
    }

    public void Remove(TotpToken token)
    {
        if (!Tokens.Remove(token)) return;
        try { Save(); } catch (Exception) { }
    }

    /// <summary>
    /// 迁移到其他验证器：生成标准的 otpauth:// 链接，粘贴到任何支持 TOTP 的验证器里（或转成二维码扫描）即可。
    /// 链接里包含密钥本身，等同于把令牌交出去。
    /// </summary>
    public static string ExportUri(TotpToken token, string issuer = "Rockstar Games") =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(token.Name)}"
        + $"?secret={token.Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={Period}";

    /// <summary>网络时间与本机时间的差。使用网络时间时，验证码按「本机时间 + 这个差值」计算。</summary>
    public TimeSpan NetworkOffset { get; private set; }
    public bool UseNetworkTime { get; set; }

    public DateTimeOffset Now => UseNetworkTime ? DateTimeOffset.UtcNow + NetworkOffset : DateTimeOffset.UtcNow;

    /// <summary>
    /// 使用网络时间：电脑时钟不准时验证码会无效。这里向一个常用网站发一次请求，用它回应里的时间来校准，
    /// 不依赖系统的时间同步。返回是否成功。
    /// </summary>
    public async Task<bool> SyncNetworkTimeAsync()
    {
        foreach (var url in new[] { "https://www.baidu.com/", "https://www.microsoft.com/" })
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                var before = DateTimeOffset.UtcNow;
                using var response = await client.SendAsync(request);
                var after = DateTimeOffset.UtcNow;
                if (response.Headers.Date is not { } serverTime) continue;
                NetworkOffset = serverTime - (before + (after - before) / 2);
                return true;
            }
            catch (Exception) { }
        }
        return false;
    }

    /// <summary>当前验证码还剩多少秒过期。</summary>
    public static int SecondsRemaining(DateTimeOffset now) => Period - (int)(now.ToUnixTimeSeconds() % Period);

    public static string Code(string secret, DateTimeOffset now)
    {
        var counter = BitConverter.GetBytes(now.ToUnixTimeSeconds() / Period);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(Base32Decode(secret), counter);

        var offset = hash[^1] & 0x0F; // 最后一个字节的低 4 位是动态偏移
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString().PadLeft(Digits, '0');
    }

    private static byte[] Base32Decode(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int bits = 0, value = 0;
        foreach (var c in text.ToUpperInvariant())
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0) continue;
            value = (value << 5) | index;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            output.Add((byte)((value >> bits) & 0xFF));
            value &= (1 << bits) - 1;
        }
        return [.. output];
    }

    /// <summary>CryptProtectData / CryptUnprotectData 的最小封装（当前用户范围）。</summary>
    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public nint Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved,
            nint prompt, int flags, out Blob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved,
            nint prompt, int flags, out Blob output);

        [DllImport("kernel32.dll")]
        private static extern nint LocalFree(nint handle);

        private const int UiForbidden = 1;

        public static byte[] Protect(byte[] data) => Transform(data, true);
        public static byte[] Unprotect(byte[] data) => Transform(data, false);

        private static byte[] Transform(byte[] data, bool protect)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
                Blob output;
                var ok = protect
                    ? CryptProtectData(ref input, "QuellGTA TOTP", 0, 0, 0, UiForbidden, out output)
                    : CryptUnprotectData(ref input, 0, 0, 0, 0, UiForbidden, out output);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally { LocalFree(output.Data); }
            }
            finally { handle.Free(); }
        }
    }
}
