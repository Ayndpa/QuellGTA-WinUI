using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;

namespace QuellGTA.Services;

/// <summary>一条日志。重要等级沿用原版：0 普通、1 操作结果、2 重要、3 警告。</summary>
public sealed record LogEntry(string Time, string Text, int Level)
{
    public string Glyph => Level switch { >= 3 => "", 2 => "", 1 => "", _ => "" };
    public double Emphasis => Level >= 2 ? 1.0 : Level == 1 ? 0.9 : 0.65;
}

/// <summary>对应原版「写到日志」。可在任意线程调用，条目会被转到界面线程。</summary>
public sealed class LogService
{
    private const int MaxEntries = 500;
    private DispatcherQueue? _dispatcher;
    private readonly List<LogEntry> _pending = [];

    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>重要等级 ≥ 2 的日志会触发，用于界面顶部的提示条。</summary>
    public event Action<LogEntry>? Important;

    public void Attach(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        List<LogEntry> backlog;
        lock (_pending) { backlog = [.. _pending]; _pending.Clear(); }
        foreach (var e in backlog) Append(e);
    }

    /// <summary>禁用记录日志：开启后所有日志都被丢弃。</summary>
    public bool Disabled { get; set; }

    /// <summary>每天自动存日志：设为目录后，每条日志都会追加写到该目录下当天的文件里；null 为关闭。</summary>
    public string? DailyDirectory { get; set; }

    private readonly object _fileGate = new();

    public void Write(string text, int level = 0)
    {
        if (Disabled) return;
        var entry = new LogEntry(DateTime.Now.ToString("HH:mm:ss"), text, level);
        if (DailyDirectory is { } directory)
        {
            try
            {
                lock (_fileGate)
                {
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(Path.Combine(directory, $"QuellGTA_{DateTime.Now:yyyyMMdd}.log"),
                        $"[{entry.Time}] ({level}) {text}{Environment.NewLine}");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var dq = _dispatcher;
        if (dq is null) { lock (_pending) _pending.Add(entry); return; }
        if (dq.HasThreadAccess) Append(entry);
        else dq.TryEnqueue(() => Append(entry));
    }

    private void Append(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        if (entry.Level >= 2) Important?.Invoke(entry);
    }

    public string Export() =>
        string.Join(Environment.NewLine, Entries.Select(e => $"[{e.Time}] ({e.Level}) {e.Text}"));
}
