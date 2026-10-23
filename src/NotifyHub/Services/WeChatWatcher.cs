using NotifyHub.Models;
using NotifyHub.Services.Parsing;
using NotifyHub.Services.Uia;

namespace NotifyHub.Services;

/// <summary>
/// 微信监听：轮询 WeChatMainWndForPC 的会话列表。
/// 同时兼容进程名 WeChat（微信 3.x）与 Weixin（微信 4.x）。
/// 只放行好友私聊与未免打扰的群聊；公众号/服务通知/免打扰会话在接收阶段直接拦截。
/// </summary>
public sealed class WeChatWatcher : ChatAppWatcher
{
    private static readonly string[] Processes = { "WeChat", "Weixin" };
    private static readonly string[] Classes = { "WeChatMainWndForPC", "WeChatMainWndForPC1", "mmui::MainWindow" };

    public WeChatWatcher(LogService log, ConfigService config, MessageCursorStore cursorStore)
        : base("微信监听", Processes, Classes, log, config, cursorStore)
    {
    }

    protected override MessageSource Source => MessageSource.WeChat;

    protected override string SourceKey => "wechat";

    protected override bool IsEnabled(AppConfig config) => config.WeChatEnabled;

    protected override bool ShouldEmit(SessionRow row, AppConfig config)
    {
        // 类型判定（真实信号：未读证据 / 免打扰 / 官方会话规范名 / 文章链接）
        var kind = WeChatFilter.Classify(row);
        var preview = LogService.Truncate(row.Summary, 40);

        // 需求 4：自己发的消息一律不推送（与过滤开关无关的硬规则）。
        // 裸内容 + 无未读证据 = 自己发的（自己发的消息不进未读数；
        // 私聊的别人消息也是裸内容，但一定带未读证据，不能按“有没有发言人前缀”判）。
        if (kind == WeChatFilter.WeChatSessionKind.FromSelf)
        {
            // 每条消息都要留日志，不做时间节流
            Log.Write(LogLevel.Debug, Name, $"跳过自己发送的消息: {row.Contact} → {preview}");
            return false;
        }

        if (!WeChatFilter.ShouldBlock(row, config, out var reason))
        {
            // 放行：他人私聊 / 普通群聊
            Log.Info(Name, $"放行{WeChatFilter.KindName(kind)}: {row.Contact} → {LogService.Truncate(row.Summary, 50)}");
            return true;
        }

        Log.Write(LogLevel.Debug, Name, $"已过滤微信会话: {row.Contact}（{reason}）");
        return false;
    }

    protected override void PollOnce(AppConfig config)
    {
        var window = TryGetWindow(out _);
        if (window is null)
        {
            return;
        }

        var read = SessionListReader.Read(window);
        ApplyRows(read.Rows, read.Diagnostic);
    }
}
