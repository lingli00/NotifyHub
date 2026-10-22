using System.IO;
using System.IO.Pipes;
using System.Windows.Threading;
using NotifyHub.UI;

namespace NotifyHub.Services;

/// <summary>
/// 灵动岛的单实例转发通道：常驻实例开一个命名管道服务端；
/// 第二个进程用 --island / --island-done 启动时把命令转发过来后立即退出。
/// 协议：一行文本，"RUN|内容" 或 "DONE|内容"。
/// 供 WorkBuddy 等外部程序在执行后台任务时唤起状态条：
///   NotifyHub.exe --island "正在编译项目…"
///   NotifyHub.exe --island-done "构建成功"
/// </summary>
public static class IslandIpc
{
    private const string PipeName = "NotifyHub.IslandPipe";

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
    {
        return TrySend((done ? "DONE|" : "RUN|") + text.Replace('\n', ' '), log);
    }

    /// <summary>把媒体控制命令（toggle/play/pause/next/prev）转发给常驻实例。</summary>
    public static bool TryForwardMedia(string action, LogService log)
    {
        return TrySend("MEDIA|" + action, log);
    }

    private static bool TrySend(string line, LogService log)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(1200);

            using var writer = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };
            writer.WriteLine(line);

            return true;
        }
        catch (Exception ex)
        {
            log.Warn("Island", "转发灵动岛命令失败", ex.Message);
            return false;
        }
    }

    private static void ServerLoop(Dispatcher dispatcher, LogService log)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly);

                server.WaitForConnection();

                using var reader = new StreamReader(server);
                var line = reader.ReadLine();

                if (string.IsNullOrWhiteSpace(line) || line.Length > 500)
                {
                    continue;
                }

                // 媒体控制命令：MEDIA|toggle|play|pause|next|prev
                if (line.StartsWith("MEDIA|", StringComparison.Ordinal))
                {
                    var action = line[6..].Trim().ToLowerInvariant();
                    log.Info("Island", "收到媒体控制命令", action);

                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            UI.IslandController.Current?.MediaCommand(action);
                        }
                        catch (Exception ex)
                        {
                            log.Error("Island", "执行媒体控制失败", $"{ex.GetType().Name}: {ex.Message}");
                        }
                    }));

                    continue;
                }

                var done = line.StartsWith("DONE|", StringComparison.Ordinal);
                var text = done ? line[5..] : line[4..];

                log.Info("Island", done ? "收到完成信号" : "收到任务状态", text);

                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // 常驻模式走总控（带音乐监控/全屏检测/配置持久化）；独立模式直接用小岛
                        if (UI.IslandController.Current is { } controller)
                        {
                            controller.ShowTask(text, done);
                        }
                        else
                        {
                            UI.IslandWindow.ShowTask(text, done);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Error("Island", "显示灵动岛失败", $"{ex.GetType().Name}: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                // 服务端意外挂掉时退避后重建，保证通道一直在
                log.Warn("Island", "管道服务异常，3 秒后重建", ex.Message);
                Thread.Sleep(3000);
            }
        }
    }
}
