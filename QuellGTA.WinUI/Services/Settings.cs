using System.Globalization;
using System.Text;

namespace QuellGTA.Services;

/// <summary>
/// 对应原版的「保存设置 / 读取设置」：一个 INI 文件，节名与键名尽量沿用原版。
/// 为避免和原版程序互相覆盖，移植版使用独立的 QuellGTA.WinUI.ini。
/// </summary>
public sealed class Settings
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> _data = new(StringComparer.OrdinalIgnoreCase);

    public string DataDirectory { get; }
    public string FilePath { get; }

    /// <param name="filePath">配置文件路径；留空使用默认位置。测试时用它指向临时文件。</param>
    public Settings(string? filePath = null)
    {
        DataDirectory = filePath is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MageAngela", "QuellGTA")
            : Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        FilePath = filePath ?? Path.Combine(DataDirectory, "QuellGTA.WinUI.ini");
        Load();
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        Dictionary<string, string>? section = null;
        foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1];
                if (!_data.TryGetValue(name, out section))
                    _data[name] = section = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0 || section is null) continue;
            section[line[..eq].Trim()] = line[(eq + 1)..];
        }
    }

    private void Save()
    {
        var sb = new StringBuilder();
        foreach (var (name, section) in _data)
        {
            sb.Append('[').Append(name).AppendLine("]");
            foreach (var (k, v) in section) sb.Append(k).Append('=').AppendLine(v);
            sb.AppendLine();
        }
        Directory.CreateDirectory(DataDirectory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, FilePath, true);
    }

    public string Get(string section, string key, string fallback)
    {
        lock (_gate)
            return _data.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : fallback;
    }

    public void Set(string section, string key, string value)
    {
        lock (_gate)
        {
            if (!_data.TryGetValue(section, out var s))
                _data[section] = s = new(StringComparer.OrdinalIgnoreCase);
            if (s.TryGetValue(key, out var old) && old == value) return;
            s[key] = value;
            try { Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public int GetInt(string section, string key, int fallback) =>
        int.TryParse(Get(section, key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public void SetInt(string section, string key, int value) =>
        Set(section, key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>原版用「是 / 否」保存布尔值。</summary>
    public bool GetBool(string section, string key, bool fallback) =>
        Get(section, key, fallback ? "是" : "否") == "是";

    public void SetBool(string section, string key, bool value) => Set(section, key, value ? "是" : "否");
}
