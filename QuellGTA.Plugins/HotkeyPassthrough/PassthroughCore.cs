namespace QuellGTA.Plugin.HotkeyPassthrough;

// 热键穿透里与界面、Win32 都无关的部分：组合键的表示、HKsavior.ini 的读写、按键匹配、重发顺序。
// 这个文件不引用任何 WinUI / P/Invoke，单元测试直接把它编进一个控制台程序里跑。

/// <summary>修饰键。数值沿用原版 HKsavior.ini：1 Alt、2 Ctrl、4 Shift（注意和主程序热键的 Ctrl=1、Alt=2 不同）。</summary>
[Flags]
public enum ComboMods { None = 0, Alt = 1, Ctrl = 2, Shift = 4 }

/// <summary>一个要穿透的组合键：修饰键 + 主键（虚拟键码）。</summary>
public readonly record struct Combo(ComboMods Mods, int Vk)
{
    /// <summary>
    /// 能不能当主键：键盘上的普通键（1~254），但不能是修饰键本身、Win 键，也不能是鼠标键
    /// （底层键盘钩子看不到鼠标键，也没法用键盘输入重发）。
    /// </summary>
    public static bool IsValidKey(int vk) => vk is > 0x06 and < 0xFF
        && vk is not (0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C);

    public bool IsValid => IsValidKey(Vk) && ((int)Mods & ~7) == 0;

    /// <summary>原版一行的格式：修饰键|键代码，例如 2|120 是 Ctrl + F9。</summary>
    public string Serialize() => $"{(int)Mods}|{Vk}";

    public static bool TryParse(string line, out Combo combo)
    {
        combo = default;
        var parts = line.Trim().Split('|');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0].Trim(), out var mods) || !int.TryParse(parts[1].Trim(), out var vk)) return false;
        if (mods is < 0 or > 7) return false;
        combo = new Combo((ComboMods)mods, vk);
        return combo.IsValid;
    }

    /// <summary>显示用的名字，写法和主程序的快捷键一致：Ctrl + Alt + Shift + 键名。</summary>
    public override string ToString() => string.Join(" + ", Parts());

    /// <summary>拆成一个个键名，界面上画成按键图标用。</summary>
    public IEnumerable<string> Parts()
    {
        if (Mods.HasFlag(ComboMods.Ctrl)) yield return "Ctrl";
        if (Mods.HasFlag(ComboMods.Alt)) yield return "Alt";
        if (Mods.HasFlag(ComboMods.Shift)) yield return "Shift";
        yield return KeyNames.Name(Vk);
    }
}

/// <summary>键名，和主程序 Hotkey.KeyName 同一套写法（插件拿不到主程序的类，只好抄一份）。</summary>
public static class KeyNames
{
    public static string Name(int vk) => vk switch
    {
        0x08 => "Backspace", 0x09 => "Tab", 0x0C => "Clear", 0x0D => "Enter", 0x13 => "Pause", 0x14 => "CapsLock",
        0x1B => "Esc", 0x20 => "Space", 0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓", 0x2C => "PrintScreen", 0x2D => "Insert", 0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        0x5D => "菜单键",
        >= 0x60 and <= 0x69 => "小键盘 " + (vk - 0x60),
        0x6A => "小键盘 *", 0x6B => "小键盘 +", 0x6D => "小键盘 -", 0x6E => "小键盘 .", 0x6F => "小键盘 /",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x90 => "NumLock", 0x91 => "ScrollLock",
        0xAD => "静音", 0xAE => "音量-", 0xAF => "音量+",
        0xB0 => "下一曲", 0xB1 => "上一曲", 0xB2 => "停止", 0xB3 => "播放/暂停",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
        0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        _ => $"键 0x{vk:X2}",
    };
}

/// <summary>
/// HKsavior.ini：第一行是条数，之后每行「修饰键|键代码」。
/// 原版读取时不管第一行写的是多少，直接跳过第一行、逐行解析，格式不对的行忽略；这里一样，
/// 只是第一行本身就是「m|vk」时（手写的文件漏了条数）也收下，并去掉重复项。
/// </summary>
public static class ComboFile
{
    public static List<Combo> Parse(string text)
    {
        var list = new List<Combo>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // 第一行的条数没有「|」，自然解析不出来，等于跳过
        foreach (var line in lines)
            if (Combo.TryParse(line, out var combo) && !list.Contains(combo)) list.Add(combo);
        return list;
    }

    /// <summary>和原版「保存报表」写出的内容一致：条数，然后每条一行，行尾不留换行。</summary>
    public static string Serialize(IReadOnlyCollection<Combo> combos)
    {
        var lines = new List<string> { combos.Count.ToString() };
        lines.AddRange(combos.Select(c => c.Serialize()));
        return string.Join("\r\n", lines);
    }
}

/// <summary>
/// 按键匹配，对应原版「事件键监测」：自己记 Shift / Ctrl / Alt（左右各一个虚拟键码，160~165）是否按着，
/// 主键按下且当前修饰键和列表里某一项完全相同时命中。
/// 和原版不同的是按住不放产生的自动重复不再命中（原版靠「正在进行」挡掉大部分，但慢一点的重复仍会再触发一次）。
/// 只在钩子线程上调用，不需要加锁；Combos 整体替换，读的时候拿到的总是完整的一份。
/// </summary>
public sealed class ComboMatcher
{
    private readonly bool[] _down = new bool[256];
    private bool _lShift, _rShift, _lCtrl, _rCtrl, _lAlt, _rAlt;

    public volatile Combo[] Combos = [];

    public ComboMods CurrentMods =>
        (_lAlt || _rAlt ? ComboMods.Alt : 0) | (_lCtrl || _rCtrl ? ComboMods.Ctrl : 0) | (_lShift || _rShift ? ComboMods.Shift : 0);

    /// <summary>钩子重装后调用：之前漏掉的松开事件不会再来，干脆全部当作松开。</summary>
    public void Reset()
    {
        Array.Clear(_down);
        _lShift = _rShift = _lCtrl = _rCtrl = _lAlt = _rAlt = false;
    }

    /// <summary>喂一个按键事件，命中时返回对应的组合键。</summary>
    public Combo? OnKey(int vk, bool down)
    {
        if (vk is <= 0 or >= 0xFF) return null; // 255 是原版钩子自检用的假按键
        switch (vk)
        {
            case 0xA0: _lShift = down; return null;
            case 0xA1: _rShift = down; return null;
            case 0xA2: _lCtrl = down; return null;
            case 0xA3: _rCtrl = down; return null;
            case 0xA4: _lAlt = down; return null;
            case 0xA5: _rAlt = down; return null;
        }
        var wasDown = _down[vk];
        _down[vk] = down;
        if (!down || wasDown) return null;
        var mods = CurrentMods;
        foreach (var combo in Combos)
            if (combo.Vk == vk && combo.Mods == mods) return combo;
        return null;
    }
}

/// <summary>重发时的一步：按下或松开一个键，之后等多少毫秒。</summary>
public readonly record struct KeyStep(int Vk, bool Down, int DelayAfter);

/// <summary>
/// 重发顺序，对应原版「热键模拟」：依次按下 Alt(164)、Shift(160)、Ctrl(162)，每个之后等 50 毫秒；
/// 按下主键，等 50 毫秒后松开；再依次松开 Alt、Shift、Ctrl，每个之后等 50 毫秒。
/// </summary>
public static class InjectionPlan
{
    public const int Gap = 50;

    public static List<KeyStep> Build(Combo combo)
    {
        var mods = new List<int>();
        if (combo.Mods.HasFlag(ComboMods.Alt)) mods.Add(0xA4);
        if (combo.Mods.HasFlag(ComboMods.Shift)) mods.Add(0xA0);
        if (combo.Mods.HasFlag(ComboMods.Ctrl)) mods.Add(0xA2);

        var steps = new List<KeyStep>();
        steps.AddRange(mods.Select(vk => new KeyStep(vk, true, Gap)));
        steps.Add(new KeyStep(combo.Vk, true, Gap));
        steps.Add(new KeyStep(combo.Vk, false, 0));
        steps.AddRange(mods.Select(vk => new KeyStep(vk, false, Gap)));
        return steps;
    }

    /// <summary>需要带扩展键标志的键（方向键、Insert/Delete、Home/End、翻页、右 Ctrl/Alt、小键盘 /、NumLock）。</summary>
    public static bool IsExtended(int vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D
        or 0xA3 or 0xA5 or 0x6F or 0x90 or >= 0xAD and <= 0xB3;
}
