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

            // 文件名按音符序列指纹：换音色自动换文件，互不覆盖。
            // 指纹里带合成参数（增益 + 峰值归一化目标 + 增益上限）——
            // v0.2.14 改了峰值归一化，旧缓存文件不能继续用。
            var sig = string.Join("|", notes.Select(n => $"{n.Freq:0.##}-{n.Ms}-{n.Vol}"))
                      + $"|g{WaveGain:0.##}|p{PeakTarget:0.##}|m{MaxPeakGain:0.##}";
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

    /// <summary>
    /// 合成增益（v0.2.4 需求 9）：用户反馈提示音比正常音量低约 10%~20%——
    /// v0.2.2 曾把增益从 0.55 调到 0.85，仍差一截；这里回到满刻度 1.0
    /// （等效 +17.6%，正落在用户反馈的区间内），音符音量维持 v0.2.3 调校值不变，
    /// 避免叠加放大过了头（叠起来会是 +40%）。旧指纹缓存的 WAV 自动作废。
    /// </summary>
    private const double WaveGain = 1.0;

    /// <summary>
    /// 峰值归一化目标（v0.2.14 需求 6）：合成后把整篇 WAV 拉到该峰值。
    ///
    /// 为什么还要这一层：各音符的 volume 是 0.26~0.5 的音色调校值，乘上用户音量
    /// （默认 80%）后实际响度只有系统的四成左右——用户反馈"提示音只有正常音量的
    /// 一半，很难听见"。这里在合成阶段把峰值拉到 0.92，等效 +6~8dB，
    /// **不动系统全局音量**；设置里的音量滑块仍然叠加在这之上。
    /// </summary>
    private const double PeakTarget = 0.92;

    /// <summary>
    /// 峰值归一化的增益上限：防止把近乎静音的音色放大成噪声。
    /// 3.0 是实测出来的——默认音色合成后峰值只有 0.35，拉到 0.92 需要 2.64 倍，
    /// 留一点余量但不至于把静音段放大成电流声。
    /// </summary>
    private const double MaxPeakGain = 3.0;

    /// <summary>合成增益（供自检断言用，运行时不可改）。</summary>
    internal static double SynthesisGain => WaveGain;

    /// <summary>峰值归一化目标（供自检断言用）。</summary>
    internal static double PeakNormalizeTarget => PeakTarget;

    /// <summary>
    /// 把整篇样本的峰值拉到 <see cref="PeakTarget"/>（v0.2.14 需求 6）。
    /// 只放大不压低（峰值超标的音符本就该被削），且增益封顶 <see cref="MaxPeakGain"/>。
    /// </summary>
    private static void NormalizePeak(List<short> samples)
    {
        if (samples.Count == 0)
        {
            return;
        }

        var peak = 0;
        foreach (var sample in samples)
        {
            var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        if (peak <= 0)
        {
            return;
        }

        var target = (int)Math.Round(PeakTarget * short.MaxValue);
        var gain = Math.Min(MaxPeakGain, target / (double)peak);
        if (gain <= 1.0)
        {
            return; // 已经够响，不再放大
        }

        for (var i = 0; i < samples.Count; i++)
        {
            samples[i] = (short)Math.Clamp((int)Math.Round(samples[i] * gain), short.MinValue, short.MaxValue);
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

                var scaled = value * volume * envelope * WaveGain;
                samples.Add((short)(Math.Clamp(scaled, -1.0, 1.0) * short.MaxValue));
            }
        }

        // v0.2.14 需求 6：合成完统一拉到目标峰值（提示音偏小的根因是音符调校音量太小）
        NormalizePeak(samples);

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
