using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;
using NotifyHub.UI;

namespace NotifyHub.Services;

/// <summary>
/// 灵动岛的进程间通道（命名管道）。
///
/// 两条路径：
///  1) 单次命令：NotifyHub.exe --island "正在编译…" / --island-done "构建成功"
///     进程起来 → 连管道 → 发一行 → 退出。冷启动由 .NET 单文件自解压决定（约 150~300ms），
///     所以"一次性命令"的延迟上限就是进程启动时间，改不动。
///  2) 常驻流（推荐，延迟 &lt;100ms）：
///     echo 一行 → 同一个管道连接上连续推送，端到端只取决于管道与 UI 线程调度（实测个位数毫秒）。
///     NotifyHub.exe --island-stream 从标准输入逐行读 RUN|... / DONE|... / MEDIA|...
///
/// 服务端每个连接一个读取任务，支持一个客户端长连接连发多行；
/// 客户端持有可复用的长连接，断开后自动重连一次。
/// </summary>
public static class IslandIpc
{
    private const string PipeName = "NotifyHub.IslandPipe";
    private const int MaxServerInstances = 8;

    private static readonly object ClientGate = new();
    private static NamedPipeClientStream? _client;
    private static StreamWriter? _writer;

    /// <summary>启动管道服务端（后台线程，收到命令后经 dispatcher 切 UI 线程）。</summary>
    public static void StartServer(Dispatcher dispatcher, LogService log)
    {
        var thread = new Thread(() => ServerLoop(dispatcher, log))
        {
            IsBackground = true,
            Name = "NotifyHub.IslandPipe",
        };
        thread.Start();
    }

    /// <summary>把灵动岛命令转发给常驻实例。成功返回 true。</summary>
    public static bool TryForward(string text, bool done, LogService log)
        => TrySend((done ? "DONE|" : "RUN|") + Flatten(text), log);

    /// <summary>把媒体控制命令（toggle/play/pause/next/prev）转发给常驻实例。</summary>
    public static bool TryForwardMedia(string action, LogService log)
        => TrySend("MEDIA|" + action, log);

    /// <summary>
    /// 常驻流模式：从标准输入逐行读取命令并转发，直到 stdin 关闭。
    /// 每次状态更新只做一次管道写入（不含进程启动），用于满足"更新延迟 &lt;100ms"。
    /// 返回转发成功的行数。
    /// </summary>
    public static int RunStreamForwarder(LogService log)
    {
        var count = 0;

        try
        {
            using var stdin = Console.OpenStandardInput();
            using var reader = new StreamReader(stdin, Encoding.UTF8);

            string? raw;
            while ((raw = reader.ReadLine()) is not null)
            {
                var line = raw.Trim();

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                // 允许直接写纯文本：默认当成"执行中"
                if (!line.StartsWith("RUN|", StringComparison.Ordinal)
                    && !line.StartsWith("DONE|", StringComparison.Ordinal)
                    && !line.StartsWith("MEDIA|", StringComparison.Ordinal))
                {
                    line = "RUN|" + Flatten(line);
                }

                if (TrySend(line, log))
                {
                    count++;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn("Island", "状态流已中断", ex.Message);
        }

        return count;
    }

    private static string Flatten(string text)
        => (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');

    // ---------------- 客户端（长连接复用） ----------------

    private static bool TrySend(string line, LogService log)
    {
        lock (ClientGate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    EnsureClient();
                    _writer!.WriteLine(line);
                    _writer.Flush();
                    return true;
                }
                catch (Exception ex)
                {
                    ResetClient();

                    if (attempt == 1)
                    {
                        log.Warn("Island", "转发灵动岛命令失败", ex.Message);
                    }
                }
            }

            return false;
        }
    }

    private static void EnsureClient()
    {
        if (_client is { IsConnected: true } && _writer is not null)
        {
            return;
        }

        ResetClient();

        var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None);
        client.Connect(1200);

        _client = client;
        _writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
    }

    private static void ResetClient()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _client?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _writer = null;
        _client = null;
    }

    // ---------------- 服务端 ----------------

    private static void ServerLoop(Dispatcher dispatcher, LogService log)
    {
        while (true)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    MaxServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);

                server.WaitForConnection();

                // 交接给独立任务：这个连接后续还会连发很多行状态，
                // 不能占住 Accept 循环（否则第二个客户端连不上）
                var connected = server;
                server = null;
                _ = Task.Run(() => ReadClient(connected, dispatcher, log));
            }
            catch (Exception ex)
            {
                log.Warn("Island", "管道服务异常，2 秒后重建", ex.Message);

                try
                {
                    server?.Dispose();
                }
                catch
                {
                    // 忽略
                }

                Thread.Sleep(2000);
            }
        }
    }

    private static void ReadClient(NamedPipeServerStream server, Dispatcher dispatcher, LogService log)
    {
        try
        {
            using (server)
            using (var reader = new StreamReader(server, Encoding.UTF8))
            {
                while (server.IsConnected)
                {
                    var line = reader.ReadLine();

                    if (line is null)
                    {
                        break; // 对端关闭
                    }

                    if (string.IsNullOrWhiteSpace(line) || line.Length > 2000)
                    {
                        continue;
                    }

                    HandleLine(line, dispatcher, log);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn("Island", "读取状态流失败", ex.Message);
        }
    }

    private static void HandleLine(string line, Dispatcher dispatcher, LogService log)
    {
        // 媒体控制命令：MEDIA|toggle|play|pause|next|prev
        if (line.StartsWith("MEDIA|", StringComparison.Ordinal))
        {
            var action = line[6..].Trim().ToLowerInvariant();

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    IslandController.Current?.MediaCommand(action);
                }
                catch (Exception ex)
                {
                    log.Error("Island", "执行媒体控制失败", $"{ex.GetType().Name}: {ex.Message}");
                }
            }));

            return;
        }

        var done = line.StartsWith("DONE|", StringComparison.Ordinal);
        var text = done ? line[5..] : line[4..];

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // 常驻模式走总控（带音乐监控/全屏检测/配置持久化）；独立模式直接用小岛
                if (IslandController.Current is { } controller)
                {
                    controller.ShowTask(text, done);
                }
                else
                {
                    IslandWindow.ShowTask(text, done);
                }
            }
            catch (Exception ex)
            {
                log.Error("Island", "显示灵动岛失败", $"{ex.GetType().Name}: {ex.Message}");
            }
        }));

        // 执行中的状态是高频道（每秒可能十几条），只在切换/完成时落盘，
        // 避免日志 I/O 反过来拖慢状态更新链路。
        if (done)
        {
            log.Info("Island", "完成信号", text.Length > 60 ? text[..60] + "…" : text);
        }
    }
}
