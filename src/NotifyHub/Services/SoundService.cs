using System.IO;
using System.Windows.Media;
using NotifyHub.Models;

namespace NotifyHub.Services;

/// <summary>
/// 两套互相独立的提示音：
///   1) 消息通知提示音：仅在聊天消息通过过滤、真正弹窗时播放；
///   2) WorkBuddy 任务完成提示音：任务完成态出现时播放。
/// 音效由程序内合成 WAV（首次启动生成到 %AppData%\NotifyHub\sounds），
/// 不依赖微信/QQ 客户端铃声，也几乎不增加安装包体积。
/// 播放用 WPF MediaPlayer，音量按各自配置独立控制。
/// </summary>
public sealed class SoundService : IDisposable
{
    private readonly LogService _log;
    private readonly MediaPlayer _messagePlayer = new();
    private readonly MediaPlayer _taskDonePlayer = new();
    private bool _prepared;

    public SoundService(LogService log) => _log = log;

    /// <summary>消息通知提示音（微信/QQ/ntfy 放行消息）。</summary>
    public void PlayMessage(AppConfig config)
    {
        if (!config.MessageSoundEnabled || config.MessageSoundVolume <= 0)
        {
            return;
        }

        Play(_messagePlayer, "message.wav", config.MessageSoundVolume);
    }

    /// <summary>WorkBuddy 任务完成提示音（与消息提示音完全独立）。</summary>
    public void PlayTaskDone(AppConfig config)
    {
        if (!config.TaskSoundEnabled || config.TaskSoundVolume <= 0)
        {
            return;
        }

        Play(_taskDonePlayer, "task-done.wav", config.TaskSoundVolume);
    }

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

    private void Play(MediaPlayer player, string fileName, int volume)
    {
        try
        {
            if (!EnsurePrepared(fileName, out var path))
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

    private bool EnsurePrepared(string fileName, out string path)
    {
        path = string.Empty;

        try
        {
            var dir = Path.Combine(AppPaths.Root, "sounds");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, fileName);

            if (!File.Exists(path))
            {
                var bytes = fileName.Contains("task", StringComparison.OrdinalIgnoreCase)
                    ? BuildTaskDoneTone()
                    : BuildMessageTone();
                File.WriteAllBytes(path, bytes);
            }

            _prepared = true;
            return true;
        }
        catch (Exception ex)
        {
            if (!_prepared)
            {
                _log.Warn("Sound", "提示音初始化失败", ex.Message);
            }

            return false;
        }
    }

    /// <summary>消息音：两声柔和的“叮咚”（880Hz → 1174Hz）。</summary>
    private static byte[] BuildMessageTone() => BuildWav(new[]
    {
        (880.0, 90, 0.42),
        (0.0, 40, 0.0),
        (1174.0, 140, 0.36),
    });

    /// <summary>任务完成音：三音上行（E5 → A5 → E6），明显的“办完了”正向反馈。</summary>
    private static byte[] BuildTaskDoneTone() => BuildWav(new[]
    {
        (659.25, 95, 0.38),
        (880.0, 95, 0.40),
        (1318.5, 170, 0.42),
    });

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

                var scaled = value * volume * envelope * 0.55;
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
