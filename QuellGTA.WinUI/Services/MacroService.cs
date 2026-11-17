using System.Text;
using System.Text.Json;

namespace QuellGTA.Services;

/// <summary>一个自编宏。原版分成「自编宏甲 / 乙」和「多槽位自编宏」两套，这里合并成一个宏库。</summary>
public sealed class MacroDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "新建宏";
    /// <summary>Mscript 文本，每行一个操作：KeyDn|键码、KeyUp|键码、Delay|毫秒、MbDn|按钮、MbUp|按钮。</summary>
    public string Script { get; set; } = "";
    /// <summary>执行次数，原版范围 1 ~ 99999。</summary>
    public int Repeat { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public string Hotkey { get; set; } = "";
}

public readonly record struct MacroStep(string Op, int Value)
{
    private static string Button(int code) => code switch { 1 => "左键", 2 => "右键", 3 => "中键", _ => "未知按钮" };

    /// <summary>生成宏操作描述</summary>
    public string Describe() => Op switch
    {
        "KeyDn" => "按住 " + Services.Hotkey.KeyName(Value),
        "KeyUp" => "松开 " + Services.Hotkey.KeyName(Value),
        "Delay" => $"等待 {Value} 毫秒",
        "MbDn" => "按住鼠标" + Button(Value),
        "MbUp" => "松开鼠标" + Button(Value),
        _ => "未知操作",
    };
}

/// <summary>
/// 自编宏：解析和执行原版的 Mscript 序列（文件头为「# QuellGTA-Mscript」），与原版导出的序列文件互相兼容。
/// </summary>
public sealed class MacroService
{
    public const string Header = "# QuellGTA-Mscript";
    private static readonly string[] Ops = ["KeyDn", "KeyUp", "Delay", "MbDn", "MbUp"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _file;
    private readonly Dictionary<string, CancellationTokenSource> _running = [];

    public List<MacroDefinition> Macros { get; private set; } = [];

    /// <summary>原版内置的三个预设（名称, 资源文件名）。</summary>
    public static readonly (string name, string file)[] Presets =
    [
        ("开门点火（By 415）", "dooreng"),
        ("收车（By 415）", "carback"),
        ("双管喷卡左轮2子弹（Q群收录）", "twogun"),
    ];

    public MacroService(Settings settings)
    {
        _file = Path.Combine(settings.DataDirectory, "Macros.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
                Macros = JsonSerializer.Deserialize<List<MacroDefinition>>(File.ReadAllText(_file, Encoding.UTF8)) ?? [];
        }
        catch (Exception) { Macros = []; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(Macros, Json), new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string ReadPreset(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Macros", file + ".txt");
        return File.Exists(path) ? Normalize(File.ReadAllText(path, Encoding.UTF8)) : "";
    }

    /// <summary>去掉文件头和空行，统一换行符，方便在编辑框里显示。</summary>
    public static string Normalize(string script) =>
        string.Join("\r\n", script.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0 && l != Header));

    /// <summary>导出为原版能直接导入的序列文本。</summary>
    public static string Export(string script) => Header + "\r\n" + Normalize(script);

    /// <summary>解析脚本。无法识别的行会被跳过（与原版一致），并通过 invalidLines 报告行号。</summary>
    public static List<MacroStep> Parse(string script, List<int>? invalidLines = null)
    {
        var steps = new List<MacroStep>();
        var lines = script.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('|');
            if (parts.Length == 2 && Ops.Contains(parts[0]) && int.TryParse(parts[1], out var value) && value >= 0)
                steps.Add(new MacroStep(parts[0], value));
            else
                invalidLines?.Add(i + 1);
        }
        return steps;
    }

    public bool IsRunning(string id)
    {
        lock (_running) return _running.ContainsKey(id);
    }

    public bool Stop(string id)
    {
        lock (_running)
        {
            if (!_running.Remove(id, out var cts)) return false;
            cts.Cancel();
            return true;
        }
    }

    public void StopAll()
    {
        lock (_running)
        {
            foreach (var cts in _running.Values) cts.Cancel();
            _running.Clear();
        }
    }

    /// <summary>
    /// 在后台执行宏。完成（未被中止）时调用 completed。
    /// 与原版不同的是：中止或结束时会把还按着的键和鼠标按钮松开，避免卡键。
    /// </summary>
    public bool Start(MacroDefinition macro, Func<bool> allowed, Action completed)
    {
        var steps = Parse(macro.Script);
        if (steps.Count == 0) return false;
        var cts = new CancellationTokenSource();
        lock (_running)
        {
            if (_running.ContainsKey(macro.Id)) return false;
            _running[macro.Id] = cts;
        }
        var repeat = Math.Clamp(macro.Repeat, 1, 99999);
        var thread = new Thread(() =>
        {
            var keys = new HashSet<int>();
            var buttons = new HashSet<int>();
            try
            {
                for (var n = 0; n < repeat && !cts.IsCancellationRequested; n++)
                    foreach (var step in steps)
                    {
                        if (cts.IsCancellationRequested) break;
                        if (step.Op != "Delay" && !allowed()) { cts.Cancel(); break; } // 游戏不在前台就停下
                        switch (step.Op)
                        {
                            case "KeyDn": InputSim.KeyDown(step.Value); keys.Add(step.Value); break;
                            case "KeyUp": InputSim.KeyUp(step.Value); keys.Remove(step.Value); break;
                            case "MbDn": InputSim.MouseButton(step.Value, false); buttons.Add(step.Value); break;
                            case "MbUp": InputSim.MouseButton(step.Value, true); buttons.Remove(step.Value); break;
                            case "Delay": cts.Token.WaitHandle.WaitOne(step.Value); break;
                        }
                    }
            }
            finally
            {
                foreach (var key in keys) InputSim.KeyUp(key);
                foreach (var button in buttons) InputSim.MouseButton(button, true);
                bool finished;
                lock (_running) finished = _running.Remove(macro.Id) && !cts.IsCancellationRequested;
                if (finished) completed();
            }
        })
        { IsBackground = true, Name = "QuellGTA.Macro" };
        thread.Start();
        return true;
    }
}
