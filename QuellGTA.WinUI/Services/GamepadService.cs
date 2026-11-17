using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace QuellGTA.Services;

/// <summary>
/// 虚拟手柄，移植自原版的插件 QuellGTA_JoystickSim（GPL-3.0，https://github.com/mageangela/QuellGTA_JoystickSim）。
/// 原版是一个单独的 JoystickSim.exe，主程序通过命名管道给它发「push|方向|行程」；移植版直接在进程内创建虚拟手柄。
/// 依赖 ViGEmBus 驱动（https://github.com/nefarius/ViGEmBus），没装驱动时所有操作都返回失败。
/// </summary>
public sealed class GamepadService(Settings settings, LogService log) : IDisposable
{
    public const string DriverUrl = "https://github.com/nefarius/ViGEmBus/releases";

    public static readonly string[] Directions = ["up", "down", "left", "right", "upleft", "upright", "downleft", "downright"];
    public static readonly string[] DirectionArrows = ["↑", "↓", "←", "→", "↖", "↗", "↙", "↘"];

    private readonly object _gate = new();
    private ViGEmClient? _client;
    private IDualShock4Controller? _ds4;
    private IXbox360Controller? _xbox;

    /// <summary>虚拟手柄的类型：ps4（原版默认）或 xbox。改了之后下次创建时生效。</summary>
    public bool UseXbox
    {
        get => settings.Get("扩展", "推摇杆类型", "ps4") == "xbox";
        set => settings.Set("扩展", "推摇杆类型", value ? "xbox" : "ps4");
    }

    public bool Connected { get { lock (_gate) return _client is not null; } }

    /// <summary>ViGEmBus 驱动装了没有。</summary>
    public static bool DriverInstalled()
    {
        try
        {
            using var client = new ViGEmClient();
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>创建并插上虚拟手柄（原版「JoySim_启动程序(真)」）。已经创建过就什么都不做。</summary>
    public bool Connect()
    {
        lock (_gate)
        {
            if (_client is not null) return true;
            try
            {
                _client = new ViGEmClient();
                if (UseXbox)
                {
                    _xbox = _client.CreateXbox360Controller();
                    _xbox.Connect();
                }
                else
                {
                    _ds4 = _client.CreateDualShock4Controller();
                    _ds4.Connect();
                }
            }
            catch (Exception ex)
            {
                Release();
                log.Write(Lang.C(525) + " " + ex.Message, 2);
                return false;
            }
        }
        Thread.Sleep(500); // 等系统和游戏认出这只手柄
        log.Write(Lang.C(529), 1);
        return true;
    }

    /// <summary>摇杆归中并拔掉虚拟手柄（原版「JoySim_启动程序(假)」）。</summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            if (_client is null) return;
            try { Set("center", 0); } catch (Exception) { }
            Thread.Sleep(200);
            Release();
        }
        log.Write(Lang.C(530), 1);
    }

    private void Release()
    {
        try { _ds4?.Disconnect(); } catch (Exception) { }
        try { _xbox?.Disconnect(); } catch (Exception) { }
        _ds4 = null;
        _xbox = null;
        try { _client?.Dispose(); } catch (Exception) { }
        _client = null;
    }

    /// <summary>把左摇杆推向某个方向。strength 0–255。手柄还没创建时会先创建。</summary>
    public bool Push(string direction, int strength)
    {
        if (!Connect()) return false;
        lock (_gate)
        {
            try { Set(direction, (byte)Math.Clamp(strength, 0, 255)); return true; }
            catch (Exception ex) { log.Write("模拟手柄：操作失败：" + ex.Message, 2); return false; }
        }
    }

    /// <summary>左摇杆归中。手柄没创建时不做任何事。</summary>
    public void Center()
    {
        lock (_gate)
        {
            if (_client is null) return;
            try { Set("center", 0); } catch (Exception) { }
        }
    }

    /// <summary>方向 → 摇杆坐标，换算方式与 JoystickSim 的 SetStickPosition 相同。</summary>
    private void Set(string direction, byte strength)
    {
        var (dx, dy) = direction switch
        {
            "left" => (-1, 0), "right" => (1, 0), "up" => (0, 1), "down" => (0, -1),
            "upleft" => (-1, 1), "upright" => (1, 1), "downleft" => (-1, -1), "downright" => (1, -1),
            _ => (0, 0),
        };
        if (_ds4 is not null)
        {
            // DS4：0–255，中点 128，Y 轴向下为正
            var offset = strength / 2;
            _ds4.SetAxisValue(DualShock4Axis.LeftThumbX, (byte)Math.Clamp(128 + dx * offset, 0, 255));
            _ds4.SetAxisValue(DualShock4Axis.LeftThumbY, (byte)Math.Clamp(128 - dy * offset, 0, 255));
        }
        else if (_xbox is not null)
        {
            // Xbox 360：-32768–32767，Y 轴向上为正
            var value = (short)(strength / 255.0 * short.MaxValue);
            _xbox.SetAxisValue(Xbox360Axis.LeftThumbX, (short)(dx * value));
            _xbox.SetAxisValue(Xbox360Axis.LeftThumbY, (short)(dy * value));
            _xbox.SubmitReport();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_client is null) return;
            try { Set("center", 0); } catch (Exception) { }
            Release();
        }
    }
}
