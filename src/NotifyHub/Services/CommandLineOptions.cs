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
    IslandShot,
    StripShot,
    Island,
    IslandDone,
    Media,
    Settings,
    IslandStream,
    MarqueeTest,
    FpsTest,
    EnvReport,
    NoticeShot,
    Snapshot,
    OpenSettings,
    Feedback,
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
                case "--island-shot":
                // --island-shot：灵动岛离屏渲染成 island-music.png / island-task.png（排版核对）
                return new CommandLineOptions(AppMode.IslandShot, 0);
                case "--strip-shot":
                // --strip-shot：收纳细条的四种指示器形态各出一张 PNG（需求 4 验收）
                return new CommandLineOptions(AppMode.StripShot, 0);
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
                case "--open-settings":
                    // --open-settings：让常驻实例把设置窗口叫到前台（v0.2.15 需求 4）
                    return new CommandLineOptions(AppMode.OpenSettings, 0);
                case "--feedback":
                    // --feedback：打开"误拦截反馈"窗（v0.2.17 工单A）：选一条最近被拦截的
                    // 记录或手动填写，提交成 JSON 反馈给开发者
                    return new CommandLineOptions(AppMode.Feedback, 0);
                case "--island-stream":
                    // --island-stream：从标准输入逐行读状态并转发（长连接，更新延迟 <100ms）
                    return new CommandLineOptions(AppMode.IslandStream, 0);
                case "--marquee-test":
                    // --marquee-test：离屏渲染跑马灯各形态（短文本居中/长文本滚动）自检
                    return new CommandLineOptions(AppMode.MarqueeTest, 0);
                case "--notice-shot":
                    // --notice-shot：岛内通知（三种来源配色）离屏渲染成 PNG（需求 3 验收）
                    return new CommandLineOptions(AppMode.NoticeShot, 0);
                case "--fps-test":
                    // --fps-test：量 5 秒内 CompositionTarget.Rendering 的实际帧率（需求 1）
                    return new CommandLineOptions(AppMode.FpsTest, 5);
                case "--snapshot":
                    // --snapshot：导出一条 Bug 快照并打开查看页截图（需求 6 验收）
                    return new CommandLineOptions(AppMode.Snapshot, 0);
                case "--env-report":
                    // --env-report：打印系统版本 / DPI / 缩放 / 关键 API 探测（需求 7 兼容性核对）
                    return new CommandLineOptions(AppMode.EnvReport, 0);
                case "--help":
                case "-h":
                case "/?":
                    return new CommandLineOptions(AppMode.Help, 0);
            }
        }

        return new CommandLineOptions(AppMode.Gui, 0);
    }
}
