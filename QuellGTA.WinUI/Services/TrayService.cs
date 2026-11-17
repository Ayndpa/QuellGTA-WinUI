using System.Runtime.InteropServices;
using static QuellGTA.Native.OverlayNative;

namespace QuellGTA.Services;

/// <summary>
/// 托盘图标和右键菜单（原版「托盘_创建 / 托盘消息回调 / 托盘_气泡提示」）。
/// WinUI 3 没有自带的托盘控件，这里直接用 Shell_NotifyIcon，并通过子类化主窗口接收回调消息。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly nint _hwnd;
    private readonly SubclassProc _proc; // 保持委托存活
    private readonly uint _taskbarCreated;
    private nint _icon;
    private bool _added;

    /// <summary>左键单击 / 双击托盘图标。</summary>
    public event Action? Activated;

    /// <summary>构建右键菜单：返回 (编号, 文本, 是否勾选)，文本为 null 表示分隔线。</summary>
    public Func<IEnumerable<(int id, string? text, bool isChecked)>>? BuildMenu;

    /// <summary>右键菜单里选中了某一项。</summary>
    public event Action<int>? MenuInvoked;

    public TrayService(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = WindowProc;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        SetWindowSubclass(hwnd, _proc, 0x5147, 0);
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = "QuellGTA",
        szInfo = "",
        szInfoTitle = "",
    };

    public void Show()
    {
        if (_added) return;
        if (_icon == 0)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            _icon = LoadImageW(0, path, IMAGE_ICON, GetSystemMetrics(49), GetSystemMetrics(50), LR_LOADFROMFILE);
        }
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    public void Hide()
    {
        if (!_added) return;
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>托盘_气泡提示</summary>
    public void Balloon(string title, string text)
    {
        if (!_added) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = text.Length > 250 ? text[..250] : text;
        data.dwInfoFlags = 1; // NIIF_INFO
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == WM_APP_TRAY)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case WM_LBUTTONUP or WM_LBUTTONDBLCLK:
                    Activated?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            return 0;
        }
        if (msg == _taskbarCreated && _added)
        {
            // 资源管理器重启后托盘图标会丢，重新加回去
            _added = false;
            Show();
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        if (BuildMenu is null) return;
        var menu = CreatePopupMenu();
        try
        {
            foreach (var (id, text, isChecked) in BuildMenu())
            {
                if (text is null) AppendMenuW(menu, MF_SEPARATOR, 0, null);
                else AppendMenuW(menu, MF_STRING | (isChecked ? MF_CHECKED : 0), (nuint)id, text);
            }
            GetCursorPos(out var point);
            Native.NativeMethods.SetForegroundWindow(_hwnd); // 否则点别处菜单不会自动消失
            var command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, point.X, point.Y, _hwnd, 0);
            if (command != 0) MenuInvoked?.Invoke((int)command);
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        Hide();
        if (_icon != 0) DestroyIcon(_icon);
        _icon = 0;
    }
}

/// <summary>
/// 开机自动运行（原版用计划任务 QuellGTAStartUp，因为程序需要管理员权限，注册表启动项无法免 UAC 启动）。
/// </summary>
public static class AutoStart
{
    // 和原版的任务名区分开，两个版本的自启互不覆盖
    private const string TaskName = "QuellGTAWinUIStartUp";

    public static bool IsEnabled() => Shell.Run("schtasks.exe", $"/query /tn {TaskName}", wait: true) == 0;

    /// <summary>
    /// 用原版的任务模板（登录时触发、最高权限、不受电池供电限制）创建计划任务。
    /// 命令行直接用 schtasks /sc onlogon 创建的任务在笔记本用电池时不会启动，所以沿用 XML 方式。
    /// </summary>
    public static bool Enable(string dataDirectory)
    {
        var exe = Environment.ProcessPath ?? "";
        var template = Path.Combine(AppContext.BaseDirectory, "Assets", "StartUpTask.xml");
        if (exe.Length == 0 || !File.Exists(template)) return false;

        static string Xml(string text) => System.Security.SecurityElement.Escape(text);
        var xml = File.ReadAllText(template)
            .Replace("<Command>QuellGTA.exe</Command>", $"<Command>{Xml(exe)}</Command><Arguments>minimized</Arguments>")
            .Replace("MageAngelaQuellGTA", Xml(Path.GetDirectoryName(exe) ?? ""));
        var file = Path.Combine(dataDirectory, "QuellGTAStartUp.xml");
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode); // 模板声明的是 UTF-16
            Shell.Run("schtasks.exe", $"/delete /tn {TaskName} /f", wait: true);
            return Shell.Run("schtasks.exe", $"/create /xml \"{file}\" /tn {TaskName}", wait: true) == 0;
        }
        catch (Exception) { return false; }
        finally
        {
            try { File.Delete(file); } catch (Exception) { }
        }
    }

    public static bool Disable() => Shell.Run("schtasks.exe", $"/delete /tn {TaskName} /f", wait: true) == 0;
}
