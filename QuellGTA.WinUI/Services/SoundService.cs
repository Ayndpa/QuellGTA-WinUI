using Windows.Media.Core;
using Windows.Media.Playback;

namespace QuellGTA.Services;

/// <summary>
/// 对应原版「播放声音(序号)」。提示音来自原工程内嵌的 s1 ~ s25 资源（MP3）。
/// 原版需要先用内嵌的 lame.exe 解码成 WAV 才能播放，这里直接用系统的 MediaPlayer 播放。
/// </summary>
public sealed class SoundService(Settings settings)
{
    public const int NicDisabled = 1, Kill = 4, Block = 5, Restore = 6, SoloOn = 8, SoloAutoEnd = 9,
        MacroOn = 15, MacroOff = 16, Hover = 17;

    /// <summary>提示音音量 0 ~ 100，0 为静音。原版默认 50。</summary>
    public int Volume
    {
        get => Math.Clamp(settings.GetInt("联机", "提示音", 50), 0, 100);
        set => settings.SetInt("联机", "提示音", Math.Clamp(value, 0, 100));
    }

    public void Play(int index)
    {
        var volume = Volume;
        if (volume <= 0) return;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", $"s{index}.mp3");
        if (!File.Exists(path)) return;
        try
        {
            var player = new MediaPlayer
            {
                Volume = volume / 100.0,
                Source = MediaSource.CreateFromUri(new Uri(path)),
            };
            player.MediaEnded += (s, _) => s.Dispose();
            player.MediaFailed += (s, _) => s.Dispose();
            player.Play();
        }
        catch (Exception) { }
    }
}
