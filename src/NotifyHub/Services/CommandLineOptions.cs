namespace NotifyHub.Services;

public enum AppMode
{
    Gui,
    Demo,
    SelfTest,
    SelfTestUi,
    Dump,
    Locate,
    Sessions,
    Island,
    IslandDone,
    Media,
    Settings,
    IslandStream,
    MarqueeTest,
    Help,
}

/// <summary>命令行参数。默认（无参数 / 双 exe）进 GUI 托盘模式。</summary>
public sealed record CommandLineOptions(
    AppMode Mode,
    int Seconds,
    string? LocateTarget = null,
    string? IslandText = null,
    string? MediaAction = null)
{
    public static CommandLineOptions Parse(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = (args[i] ?? string.Empty).Trim().ToLowerInvariant();
            switch (arg)
            {
                case "--demo":
                    return new CommandLineOptions(AppMode.Demo, 14);
                case "--selftest":
                    return new CommandLineOptions(AppMode.SelfTest, 0);
                case "--selftest-ui":
                    return new CommandLineOptions(AppMode.SelfTestUi, 8);
                case "--dump":
                case "--dump-uia":
                    return new CommandLineOptions(AppMode.Dump, 0);
                case "--locate":
                // --locate 联系人名：激活微信并搜索定位到该聊天（测试/手动用）
                var target = i + 1 < args.Length ? args[i + 1]?.Trim() : null;
                return new CommandLineOptions(AppMode.Locate, 0, string.IsNullOrEmpty(target) ? null : target);
                case "--sessions":
                // --sessions：读一次微信会话列表并打印解析结果（FromSelf/未读/免打扰），
                // 用于真机核验“自己发的消息不推 / 好友消息能识别”（需求 4/5）
                return new CommandLineOptions(AppMode.Sessions, 0);
                case "--island":
                    // --island 文本：唤起居中悬浮状态条（任务执行中）
                    var text = i + 1 < args.Length ? args[i + 1]?.Trim() : null;
                    return new CommandLineOptions(AppMode.Island, 0, IslandText: string.IsNullOrEmpty(text) ? "正在执行任务…" : text);
                case "--island-done":
                    // --island-done 文本：播放完成动画后自动消失
                    var doneText = i + 1 < args.Length ? args[i + 1]?.Trim() : null;
                    return new CommandLineOptions(AppMode.IslandDone, 0, IslandText: string.IsNullOrEmpty(doneText) ? "任务完成" : doneText);
                case "--media":
                    // --media toggle|play|pause|next|prev：控制当前播放器（转发给常驻实例）
                    var media = i + 1 < args.Length ? args[i + 1]?.Trim().ToLowerInvariant() : null;
                    return new CommandLineOptions(AppMode.Media, 0, MediaAction: media);
                case "--settings":
                    // --settings：仅打开设置窗口（排版验证/人工检查用，不启动监听）
                    return new CommandLineOptions(AppMode.Settings, 0);
                case "--island-stream":
                    // --island-stream：从标准输入逐行读状态并转发（长连接，更新延迟 <100ms）
                    return new CommandLineOptions(AppMode.IslandStream, 0);
                case "--marquee-test":
                    // --marquee-test：离屏渲染跑马灯各形态（短文本居中/长文本滚动）自检
                    return new CommandLineOptions(AppMode.MarqueeTest, 0);
                case "--help":
                case "-h":
                case "/?":
                    return new CommandLineOptions(AppMode.Help, 0);
            }
        }

        return new CommandLineOptions(AppMode.Gui, 0);
    }
}
