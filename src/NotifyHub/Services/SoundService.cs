using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>
/// 两套互相独立的提示音：
///   1) 消息通知提示音：仅在聊天消息通过过滤、真正弹窗时播放；
///   2) WorkBuddy 任务完成提示音：任务完成态出现时播放。
/// 音效由程序内合成 WAV（按音色缓存到 %AppData%\NotifyHub\sounds），
/// 不依赖微信/QQ 客户端铃声，也几乎不增加安装包体积。
/// 播放用 WPF MediaPlayer，音量按各自配置独立控制。
/// v0.2.2：音色可选（消息 5 种 / 完成 4 种）、合成增益加大、支持试听。
/// </summary>
public sealed class SoundService : IDisposable
{
    private readonly LogService _log;
    private readonly MediaPlayer _messagePlayer = new();
    private readonly MediaPlayer _taskDonePlayer = new();
    private readonly object _gate = new();
    private bool _prepared;

    public SoundService(LogService log) => _log = log;

    /// <summary>消息通知提示音（微信/QQ/ntfy 放行消息）。</summary>
    public void PlayMessage(AppConfig config)
    {
        if (!config.MessageSoundEnabled || config.MessageSoundVolume <= 0)
        {
            return;
        }

        Play(_messagePlayer, MessageTone(config.MessageSoundStyle), config.MessageSoundVolume);
    }

    /// <summary>WorkBuddy 任务完成提示音（与消息提示音完全独立）。</summary>
    public void PlayTaskDone(AppConfig config)
    {
        if (!config.TaskSoundEnabled || config.TaskSoundVolume <= 0)
        {
            return;
        }

        Play(_taskDonePlayer, TaskTone(config.TaskSoundStyle), config.TaskSoundVolume);
    }

    /// <summary>设置页试听：直接用指定的合成音符播一次。</summary>
    public void Preview((double Freq, int Ms, double Vol)[] notes, int volume)
        => Play(new MediaPlayer(), notes, volume);

    /// <summary>音色名 → 合成器（消息类）。</summary>
    public static (double Freq, int Ms, double Volume)[] MessageTone(string style) => style switch
    {
        // 清脆：高音两连，短促明亮
        "Crystal" => new[] { (1568.0, 70, 0.34), (0.0, 30, 0.0), (2093.0, 110, 0.30) },
        // 水泡：低到高的滑音，圆润
        "Bubble" => new[] { (392.0, 60, 0.5), (523.25, 60, 0.5), (659.25, 60, 0.5), (783.99, 130, 0.4) },
        // 木琴：双音同奏，木质衰减
        "Marimba" => new[] { (523.25, 120, 0.34), (0.0, 30, 0.0), (783.99, 160, 0.32) },
        // 电子：方波感的双 blip
        "Digital" => new[] { (1244.5, 60, 0.3), (0.0, 40, 0.0), (1244.5, 60, 0.3), (0.0, 40, 0.0), (1661.2, 90, 0.3) },
        // 默认 叮咚
        _ => new[] { (880.0, 90, 0.42), (0.0, 40, 0.0), (1174.7, 140, 0.38) },
    };

    /// <summary>音色名 → 合成器（完成类）。</summary>
    public static (double Freq, int Ms, double Volume)[] TaskTone(string style) => style switch
    {
        // 成功双音：E5 → B5
        "Success" => new[] { (659.25, 110, 0.4), (987.77, 190, 0.4) },
        // 铃声：高频两响，急衰减
        "Bell" => new[] { (1760.0, 90, 0.3), (0.0, 60, 0.0), (1760.0, 200, 0.26) },
        // 和弦：C-E-G 同时起
        "Chord" => new[] { (523.25, 260, 0.26), (659.25, 260, 0.26), (783.99, 300, 0.26) },
        // 默认 上行三音 E5-A5-E6
        _ => new[] { (659.25, 95, 0.4), (880.0, 95, 0.42), (1318.5, 170, 0.44) },
    };

    public void Dispose()
    {
        try
        {
            _messagePlayer.Close();
            _taskDonePlayer.Close();
        }
        catch
        {
            // 退出时忽略
        }
    }

    private void Play(MediaPlayer player, (double Freq, int Ms, double Vol)[] notes, int volume)
    {
        try
        {
            var path = EnsurePrepared(notes, out var ok);
            if (!ok)
            {
                return;
            }

            if (player.Source is null)
            {
                player.Open(new Uri(path));
            }

            player.Volume = Math.Clamp(volume, 0, 100) / 100.0;
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch (Exception ex)
        {
            _log.Warn("Sound", "播放提示音失败", ex.Message);
        }
    }

    /// <summary>按音符序列合成/取缓存 WAV，返回路径；ok=false 表示失败。</summary>
    private string EnsurePrepared((double Freq, int Ms, double Vol)[] notes, out bool ok)
    {
        ok = false;
        var path = string.Empty;

        try
        {
            var dir = Path.Combine(AppPaths.Root, "sounds");
            Directory.CreateDirectory(dir);

            // 文件名按音符序列指纹：换音色自动换文件，互不覆盖
            var sig = string.Join("|", notes.Select(n => $"{n.Freq:0.##}-{n.Ms}-{n.Vol}"));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sig)))[..12];
            path = Path.Combine(dir, $"tone-{hash}.wav");

            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, BuildWav(notes));
            }

            _prepared = true;
            ok = true;
            return path;
        }
        catch (Exception ex)
        {
            if (!_prepared)
            {
                _log.Warn("Sound", "提示音初始化失败", ex.Message);
            }

            return path;
        }
    }

    private static byte[] BuildWav((double Freq, int Ms, double Volume)[] notes, int sampleRate = 44100)
    {
        var samples = new List<short>(sampleRate);

        foreach (var (freq, ms, volume) in notes)
        {
            var count = Math.Max(1, sampleRate * ms / 1000);

            for (var i = 0; i < count; i++)
            {
                if (freq <= 0 || volume <= 0)
                {
                    samples.Add(0);
                    continue;
                }

                var t = (double)i / sampleRate;
                var attack = Math.Min(1.0, i / (sampleRate * 0.006));
                var release = Math.Min(1.0, (count - i) / (sampleRate * 0.02));
                var decay = Math.Exp(-2.8 * i / count);
                var envelope = attack * release * decay;

                var value = Math.Sin(2 * Math.PI * freq * t)
                            + 0.16 * Math.Sin(2 * Math.PI * freq * 2 * t)
                            + 0.06 * Math.Sin(2 * Math.PI * freq * 3 * t);

                // v0.2.2：合成增益 0.55 → 0.85，同样 80% 音量下明显更响
                var scaled = value * volume * envelope * 0.85;
                samples.Add((short)(Math.Clamp(scaled, -1.0, 1.0) * short.MaxValue));
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        var dataBytes = samples.Count * 2;
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);                       // PCM 头长度
        writer.Write((short)1);                 // PCM
        writer.Write((short)1);                 // 单声道
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);           // 字节率
        writer.Write((short)2);                 // 块对齐
        writer.Write((short)16);                // 位深
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);

        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
