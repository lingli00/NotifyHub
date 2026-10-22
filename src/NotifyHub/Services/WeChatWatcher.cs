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
        if (!WeChatFilter.ShouldBlock(row, config, out var reason))
        {
            return true;
        }

        LogThrottled(LogLevel.Debug, $"已过滤微信会话: {row.Contact}（{reason}）", TimeSpan.FromSeconds(30));
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
