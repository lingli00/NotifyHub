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
        // v0.2.17 工单A：一次判定同时给出"拦不拦 / 为什么 / 来源渠道标签 / 内容类型"。
        // 判定拆成两个维度：渠道标签（官方/免打扰/好友群聊/自己发送）只作风险权重，
        // 内容类型（图片/视频/表情/语音/文件）决定好友群聊多媒体是否放行。
        var decision = WeChatFilter.Decide(row, config);
        var content = WeChatFilter.ContentName(decision.Content);
        var preview = LogService.Truncate(row.Summary, 40);

        if (!decision.Blocked)
        {
            // 放行：他人私聊 / 普通群聊（含图片/视频/表情包等多媒体）
            Log.Info(Name,
                $"放行{WeChatFilter.KindName(WeChatFilter.Classify(row))}: {row.Contact} → {preview}",
                $"渠道={decision.Source} 内容={content}{(decision.Reason.Length > 0 ? " " + decision.Reason : "")}");
            return true;
        }

        Log.Write(LogLevel.Debug, Name,
            $"已过滤微信会话: {row.Contact}（{decision.Reason}）",
            $"渠道={decision.Source} 内容={content}");
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
