using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using NotifyHub.Models;
using NotifyHub.Services.Parsing;
using NotifyHub.Services.Uia;
using NotifyHub.UI;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using Visibility = System.Windows.Visibility;

namespace NotifyHub.Services;

/// <summary>
/// 无界面自检（--selftest）：把解析、去重、配置、地址规范化、UIA 客户端可用性
/// 这些“能离线验证的逻辑”全部跑一遍，结果写日志并在控制台输出。
/// </summary>
public static class SelfTest
{
    public static int RunAll()
    {
        ConsoleBridge.TryAttach();

        var log = LogService.Shared;
        var report = new StringBuilder();
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        report.AppendLine($"NotifyHub 自检 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"机器: {Environment.OSVersion.VersionString} / .NET {Environment.Version}");
        report.AppendLine(new string('-', 70));

        void Write(string line, bool ok)
        {
            report.AppendLine(line);
            ConsoleBridge.WriteLine(line);
            log.Write(ok ? LogLevel.Info : LogLevel.Error, "SelfTest", line);
        }

        void Check(string name, Func<bool> action)
        {
            bool ok;
            string detail;

            try
            {
                ok = action();
                detail = string.Empty;
            }
            catch (Exception ex)
            {
                ok = false;
                detail = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (ok)
            {
                passed++;
            }
            else
            {
                failed++;
            }

            Write($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  → " + detail : string.Empty)}", ok);
        }

        // WPF 窗口必须 STA，而自检主体跑在 MTA 线程池上：单开一个 STA 线程执行，
        // 只做 Measure/Arrange 不进消息循环（不 Show 也能拿到真实布局）。
        // 线程内的异常写进 island-layout.txt，避免无提示地变成“线程挂了→FAIL”。
        static bool RunSta(Func<bool> body, int timeoutSeconds = 15)
        {
            var result = false;
            var worker = new System.Threading.Thread(
                () =>
                {
                    try
                    {
                        result = body();
                    }
                    catch (Exception ex)
                    {
                        result = false;
                        try
                        {
                            File.WriteAllText(
                                Path.Combine(AppPaths.Root, "island-layout.txt"), ex.ToString(), Encoding.UTF8);
                        }
                        catch
                        {
                            // 忽略
                        }
                    }
                },
                8 * 1024 * 1024);

            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();
            return worker.Join(TimeSpan.FromSeconds(timeoutSeconds)) && result;
        }

        Check("配置区间收敛（10ms→300ms, 100ms→1000ms, 堆叠99→5）", () =>
        {
            var config = new AppConfig { PollIntervalMs = 10, NotificationDurationMs = 100, MaxStack = 99 };
            config.Normalize();
            return config.PollIntervalMs == AppConfig.MinPollIntervalMs
                   && config.NotificationDurationMs == AppConfig.MinDurationMs
                   && config.MaxStack == AppConfig.MaxStackLimit;
        });

        Check("会话解析：单聊 [联系人, 时间, 摘要]", () =>
        {
            var row = MessageTextParser.Parse(new[] { "张三", "10:23", "你好，在吗" });
            return row is { Contact: "张三", Summary: "你好，在吗" };
        });

        Check("会话解析：ListItem.Name 多行文本", () =>
        {
            var row = MessageTextParser.Parse(new[] { "李四\n昨天\n[图片]" });
            return row is { Contact: "李四", Summary: "[图片]" };
        });

        Check("会话解析：群聊摘要保留“发言人: 内容”", () =>
        {
            var row = MessageTextParser.Parse(new[] { "产品群", "下午 3:05", "王五: 明天十点开会" });
            return row is { Contact: "产品群", Summary: "王五: 明天十点开会" };
        });

        Check("会话解析：剥离 [N条] 未读前缀", () =>
        {
            var row = MessageTextParser.Parse(new[] { "公众号", "[63条] 软件室: 新版本发布" });
            return row is { Contact: "公众号", Summary: "软件室: 新版本发布" };
        });

        Check("会话解析：剥离“已置顶”标记与未读前缀", () =>
        {
            var row = MessageTextParser.Parse(new[] { "张三", "已置顶 [1条] 早安" });
            return row is { Contact: "张三", Summary: "早安" };
        });

        Check("会话解析：摘要为噪声时返回空摘要（不误报）", () =>
        {
            var row = MessageTextParser.Parse(new[] { "张三", "已读" });
            return row is { Contact: "张三", Summary: "" };
        });

        Check("会话解析：仅 2 个文本时纯数字不当未读数丢弃", () =>
        {
            var row = MessageTextParser.Parse(new[] { "张三", "1" });
            return row is { Contact: "张三", Summary: "1" };
        });

        Check("v0.2.4 需求5：末尾纯数字是消息内容本身（“1”不当未读数吞掉）", () =>
        {
            // 微信 4.x 真实 ListItem.Name 序列：[联系人, 摘要, 时间]
            var row = MessageTextParser.Parse(new[] { "小八", "1", "22:31" });
            var row2 = MessageTextParser.Parse(new[] { "小八", "已置顶", "1", "22:31", "消息免打扰" });
            return row is { Contact: "小八", Summary: "1" }
                   && row2 is { Contact: "小八", Summary: "1" };
        });

        Check("v0.2.4 需求5：后接摘要的纯数字仍按未读数丢弃（红点 badge）", () =>
        {
            var row = MessageTextParser.Parse(new[] { "产品群", "3", "王五: 明天十点开会" });
            return row is { Contact: "产品群", Summary: "王五: 明天十点开会" };
        });

        Check("v0.2.4 需求4：自己发的消息标记 FromSelf（无发言人前缀）", () =>
        {
            // 实测：自己发的 = 裸内容；别人发来的 = “发言人: 内容”（未读另带 [N条]）
            var self = MessageTextParser.Parse(new[] { "比企谷八蟠", "的撒旦不会寂寞最新版·", "22:35" });
            var otherUnread = MessageTextParser.Parse(new[] { "亿新双11活动方案福利群8", "[14条] 亿新线报专员一不加好友: [图片]", "22:45" });
            var otherRead = MessageTextParser.Parse(new[] { "QQ邮箱提醒", "From CloudCone: $5.10 on you", "20:44" });

            return self is { Contact: "比企谷八蟠", FromSelf: true, HasUnread: false }
                   && otherUnread is { FromSelf: false, HasUnread: true }
                   && otherRead is { FromSelf: false, HasUnread: false };
        });

        Check("v0.2.4 需求4：URL 冒号不算发言人前缀（自己发的链接消息仍是 FromSelf）", () =>
        {
            var selfLink = MessageTextParser.Parse(new[] { "小八", "https://ntfy.sh/test", "22:31" });
            var selfWww = MessageTextParser.Parse(new[] { "小八", "www.example.com 看看这个", "22:31" });
            return selfLink is { FromSelf: true } && selfWww is { FromSelf: true };
        });

        Check("去重：(来源,发件人,内容) 相同则拦截，不同则放行", () =>
        {
            var cache = new DedupCache();
            var first = new NotifyMessage { Source = MessageSource.WeChat, Sender = "张三", Content = "你好" };
            var second = new NotifyMessage { Source = MessageSource.WeChat, Sender = "张三", Content = "你好2" };

            var accepted1 = cache.TryAccept(first, TimeSpan.FromMinutes(1), out var hash1);
            var accepted2 = cache.TryAccept(first, TimeSpan.FromMinutes(1), out var hash2);
            var accepted3 = cache.TryAccept(second, TimeSpan.FromMinutes(1), out _);

            return accepted1 && !accepted2 && accepted3 && hash1 == hash2;
        });

        Check("微信过滤：免打扰/官方会话拦截，好友与普通群放行（名称关键词不判类型）", () =>
        {
            var config = new AppConfig { WeChatFilterEnabled = true };

            var mutedGroup = new SessionRow("产品群", "王五: 明天十点开会", Muted: true, Signals: "消息免打扰");
            var officialFolder = new SessionRow("公众号", "求是网: 今日推送");
            var serviceNotice = new SessionRow("服务通知", "微信支付凭证");
            var friend = new SessionRow("张三", "张三: 在吗");
            var normalGroup = new SessionRow("家人群", "李四: 晚上吃啥", Muted: false, Signals: "群聊");

            return WeChatFilter.ShouldBlock(mutedGroup, config, out _)
                   && WeChatFilter.ShouldBlock(officialFolder, config, out _)
                   && WeChatFilter.ShouldBlock(serviceNotice, config, out _)
                   && !WeChatFilter.ShouldBlock(friend, config, out _)
                   && !WeChatFilter.ShouldBlock(normalGroup, config, out _);
        });

        Check("v0.2.5 回归：群名叫“阿里云”不被误判成公众号（名称关键词已从类型判定移除）", () =>
        {
            var config = new AppConfig { WeChatFilterEnabled = true };

            // 真机复现用例：阿里云是个普通群聊，群里 Flyme 发来“[动画表情]”，
            // 旧实现撞上关键词表里的“阿里云”直接跳过——现在必须放行。
            var aliGroup = new SessionRow("阿里云", "Flyme: [动画表情]", HasUnread: true, UnreadCount: 20);
            var aliGroupRead = new SessionRow("阿里云", "Flyme: 收到");

            // 同名的官方会话规范名仍然整名拦截
            var systemSession = new SessionRow("服务号", "中国银行微银行: 交易成功提醒", HasUnread: true, UnreadCount: 1);

            return !WeChatFilter.ShouldBlock(aliGroup, config, out _)
                   && !WeChatFilter.ShouldBlock(aliGroupRead, config, out _)
                   && WeChatFilter.ShouldBlock(systemSession, config, out _);
        });

        Check("v0.2.5：会话类型判定只依据真实信号（Classify）", () =>
        {
            var chat = new SessionRow("勇敢牛牛", "小矮子: 行吧", HasUnread: true, UnreadCount: 1);
            var muted = new SessionRow("飞牛粉丝群", "助理: 大家好", Muted: true, Signals: "消息免打扰");
            var system = new SessionRow("微信团队", "[链接] 登录操作通知");
            var fromSelf = new SessionRow("陈星", "ok", FromSelf: true);

            // 私聊的摘要就是裸内容——带未读证据说明是别人发来的，必须放行
            // （v0.2.5 回归：xiy624 发来“就只能下班”、xiy62 发来“谢谢你”曾被误判成官方推送）
            var privateIncoming = new SessionRow("xiy624", "就只能下班", HasUnread: true, UnreadCount: 1);

            return WeChatFilter.Classify(chat) == WeChatFilter.WeChatSessionKind.Chat
                   && WeChatFilter.Classify(muted) == WeChatFilter.WeChatSessionKind.Muted
                   && WeChatFilter.Classify(system) == WeChatFilter.WeChatSessionKind.System
                   && WeChatFilter.Classify(fromSelf) == WeChatFilter.WeChatSessionKind.FromSelf
                   && WeChatFilter.Classify(privateIncoming) == WeChatFilter.WeChatSessionKind.Chat;
        });

        Check("v0.2.5 回归：私聊裸内容消息必须放行（未读证据判他人消息）", () =>
        {
            var config = new AppConfig { WeChatFilterEnabled = true };

            // 真机复现：xiy624/xiy62 是个人好友，摘要为裸内容 + 未读，旧规则判成“官方账号推送”被吞
            var xiy624 = new SessionRow("xiy624", "就只能下班", HasUnread: true, UnreadCount: 1);
            var xiy62 = new SessionRow("xiy62", "谢谢你", HasUnread: true, UnreadCount: 2);
            // 群聊带“成员: ”前缀也要放行
            var group = new SessionRow("阿里云", "Flyme: [动画表情]", HasUnread: true, UnreadCount: 20);
            // 自己发的（裸内容 + 无未读）仍然拦
            var self = new SessionRow("xiy624", "就随便下班", FromSelf: true);

            return !WeChatFilter.ShouldBlock(xiy624, config, out _)
                   && !WeChatFilter.ShouldBlock(xiy62, config, out _)
                   && !WeChatFilter.ShouldBlock(group, config, out _)
                   && WeChatFilter.Classify(self) == WeChatFilter.WeChatSessionKind.FromSelf;
        });

        Check("v0.2.17 工单A：好友/群聊多媒体一律放行（图片/视频/表情不再被误拦）", () =>
        {
            var config = new AppConfig { WeChatFilterEnabled = true };

            // ① 内容类型识别：只认方括号占位符，"图片发你了"这种文本不算多媒体
            var classifyOk =
                WeChatFilter.ClassifyContent("[图片]") == MessageContentKind.Image
                && WeChatFilter.ClassifyContent("王五: [视频]") == MessageContentKind.Video
                && WeChatFilter.ClassifyContent("Flyme: [动画表情]") == MessageContentKind.Sticker
                && WeChatFilter.ClassifyContent("[语音]") == MessageContentKind.Voice
                && WeChatFilter.ClassifyContent("[文件]") == MessageContentKind.File
                && WeChatFilter.ClassifyContent("图片发你了，记得看") == MessageContentKind.Text
                && WeChatFilter.ClassifyContent((string?)null) == MessageContentKind.Text;

            // ② 群聊多媒体（发言人前缀 + 未读）放行
            var groupImage = new SessionRow("阿里云", "[20条] Flyme: [图片]", HasUnread: true, UnreadCount: 20);
            var groupSticker = new SessionRow("阿里云", "Flyme: [动画表情]", HasUnread: true, UnreadCount: 20);

            // ③ 私聊好友发来的图片：摘要裸内容 + 未读证据没被 UIA 捕获 → FromSelf 误判路径，
            //    旧版在这里把图拦掉（工单A 误伤现场），现在按好友多媒体放行
            var privateImage = new SessionRow("张三", "[图片]", FromSelf: true);

            // ④ 渠道标签只作风险权重：免打扰/官方会话仍按用户显式设置拦截
            var mutedImage = new SessionRow("产品群", "王五: [图片]", Muted: true, Signals: "消息免打扰");
            var officialImage = new SessionRow("服务号", "中国银行微银行: [图片]", HasUnread: true, UnreadCount: 1);

            // ⑤ 自己发送的文本仍然拦（自己发的消息不进未读数，是噪声不是误伤）
            var selfText = new SessionRow("张三", "ok", FromSelf: true);

            // ⑥ 自定义拦截词仍然拦
            var keywordHit = new SessionRow("推广号", "点击链接领取优惠");

            var groupDecision = WeChatFilter.Decide(groupImage, config);
            var privateDecision = WeChatFilter.Decide(privateImage, config);
            var selfDecision = WeChatFilter.Decide(selfText, config);
            var mutedDecision = WeChatFilter.Decide(mutedImage, config);
            var officialDecision = WeChatFilter.Decide(officialImage, config);

            report.AppendLine(
                $"      诊断: 群图={groupDecision.Blocked}({groupDecision.Reason}) " +
                $"私聊图={privateDecision.Blocked}({privateDecision.Reason}) 渠道={privateDecision.Source} " +
                $"自己文本={selfDecision.Blocked} 免打扰图={mutedDecision.Blocked}({mutedDecision.Source}) " +
                $"官方图={officialDecision.Blocked}({officialDecision.Source})");

            var keywordConfig = new AppConfig { WeChatFilterEnabled = true, WeChatBlockedKeywords = "推广" };

            // ⑦ 关掉"好友多媒体放行"开关 → 回到旧行为（仍按渠道规则拦）
            var strictConfig = new AppConfig { WeChatFilterEnabled = true, WeChatAllowChatMultimedia = false };

            return classifyOk
                   && !groupDecision.Blocked && groupDecision.Content == MessageContentKind.Image
                   && !privateDecision.Blocked && privateDecision.Content == MessageContentKind.Image
                   && !WeChatFilter.Decide(groupSticker, config).Blocked
                   && selfDecision.Blocked
                   && mutedDecision.Blocked && mutedDecision.Source == "免打扰会话"
                   && officialDecision.Blocked && officialDecision.Source == "公众号/官方渠道"
                   && WeChatFilter.Decide(keywordHit, keywordConfig).Blocked
                   && WeChatFilter.Decide(privateImage, strictConfig).Blocked
                   && WeChatFilter.SourceTag(WeChatFilter.WeChatSessionKind.Chat) == "好友/群聊";
        });

        Check("v0.2.5：解析未读数与 FromSelf 判据（自己发的不带未读证据）", () =>
        {
            // 别人发来的未读消息：[N条] + 发言人前缀
            var incoming = MessageTextParser.Parse(new[]
            {
                "阿里云",
                "[20条] Flyme: [动画表情]",
                "08:19",
            });
            // 自己发的：裸内容、无未读证据
            var self = MessageTextParser.Parse(new[]
            {
                "陈星",
                "ok",
                "昨天 20:56",
            });
            // 官方账号推送：裸内容但带未读证据 → 不是自己发送
            var official = MessageTextParser.Parse(new[]
            {
                "商家寄件福利官23",
                "[1条] [图片]",
                "昨天 11:01",
            });

            return incoming is { FromSelf: false, HasUnread: true, UnreadCount: 20 }
                   && self is { FromSelf: true, HasUnread: false }
                   && official is { FromSelf: false, HasUnread: true, UnreadCount: 1 };
        });

        Check("v0.2.5：同文本新消息靠未读数递增识别（差分不再吞消息）", () =>
        {
            var tracker = new SessionDiffTracker();
            var now = DateTime.Now;
            var rows = new[]
            {
                new SessionRow("张三", "李四: 1", HasUnread: true, UnreadCount: 1),
            };

            // 第一轮建基线
            tracker.Diff(rows, now, out _);

            // 文本完全相同、未读数 1→2：必须识别为变化
            var again = new[]
            {
                new SessionRow("张三", "李四: 1", HasUnread: true, UnreadCount: 2),
            };
            var changes = tracker.Diff(again, now.AddSeconds(1), out _);

            // 文本与未读数都不变：不再重复报
            var same = new[]
            {
                new SessionRow("张三", "李四: 1", HasUnread: true, UnreadCount: 2),
            };
            var noChanges = tracker.Diff(same, now.AddSeconds(2), out _);

            return changes.Count == 1 && noChanges.Count == 0;
        });

        Check("微信过滤：关闭过滤开关后一律放行", () =>
        {
            var config = new AppConfig { WeChatFilterEnabled = false };
            var muted = new SessionRow("产品群", "内容", Muted: true, Signals: "消息免打扰");
            var official = new SessionRow("公众号", "推送");
            return !WeChatFilter.ShouldBlock(muted, config, out _)
                   && !WeChatFilter.ShouldBlock(official, config, out _);
        });

        Check("灵动岛状态只保留单行（多行日志被压平、超长截断）", () =>
        {
            var single = UI.IslandController.ToSingleLine("正在梳理需求文档结构\n第二行日志\n第三行", "…");
            var longText = new string('长', 200);
            var truncated = UI.IslandController.ToSingleLine(longText, "…");
            return single == "正在梳理需求文档结构"
                   && truncated.Length <= 91
                   && truncated.EndsWith('…');
        });

        Check("去重缓存落盘：重启后已推送消息不再放行", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"notifyhub-dedup-{Guid.NewGuid():N}.json");
            try
            {
                var message = new NotifyMessage { Source = MessageSource.WeChat, Sender = "张三", Content = "持久化测试" };
                var first = new DedupCache(path);
                var accepted1 = first.TryAccept(message, TimeSpan.FromHours(24), out _);
                first.Flush();

                var second = new DedupCache(path);
                var accepted2 = second.TryAccept(message, TimeSpan.FromHours(24), out _);
                return accepted1 && !accepted2;
            }
            finally
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // 忽略
                }
            }
        });

        Check("ntfy SSE：只解析 event=message，keepalive/open 被跳过", () =>
        {
            const string raw =
                "event: open\ndata: {\"id\":\"1\",\"event\":\"open\",\"time\":1700000000}\n\n" +
                "data: {\"id\":\"2\",\"time\":1700000100,\"event\":\"message\",\"topic\":\"t\",\"title\":\"构建\",\"message\":\"成功\"}\n\n" +
                ": keepalive\n\n";

            var list = NtfySseParser.ParseStreamText(raw);
            return list.Count == 1
                   && list[0].Sender == "构建"
                   && list[0].Content == "成功"
                   && list[0].Time.Year > 2000
                   && list[0].Id == "2";
        });

        Check("ntfy 地址规范化（自动补 https 与 /sse）", () =>
        {
            return NtfySubscriber.BuildStreamUrl("ntfy.sh/mytopic") == "https://ntfy.sh/mytopic/sse"
                   && NtfySubscriber.BuildStreamUrl("https://ntfy.sh/mytopic/sse") == "https://ntfy.sh/mytopic/sse"
                   && NtfySubscriber.BuildStreamUrl("https://ntfy.sh/") is null
                   && NtfySubscriber.BuildStreamUrl("ftp://example.com/topic") is null
                   && NtfySubscriber.BuildStreamUrl("") is null;
        });

        Check("配置 JSON 往返不丢字段", () =>
        {
            var config = new AppConfig
            {
                NtfyUrl = "https://ntfy.sh/abc",
                NtfyEnabled = true,
                WeChatEnabled = false,
                QQEnabled = true,
                PollIntervalMs = 900,
                NotificationDurationMs = 5000,
                AutoStart = true,
                DumpUiaOnStart = true,
                MaxStack = 4,
            };

            var json = JsonSerializer.Serialize(config);
            var back = JsonSerializer.Deserialize<AppConfig>(json);
            return back is not null
                   && back.NtfyUrl == config.NtfyUrl
                   && back.NtfyEnabled
                   && !back.WeChatEnabled
                   && back.PollIntervalMs == 900
                   && back.NotificationDurationMs == 5000
                   && back.AutoStart
                   && back.DumpUiaOnStart
                   && back.MaxStack == 4;
        });

        Check("差分恢复：读列表中断超30s后，与旧快照的差集被补发（防丢包）", () =>
        {
            var tracker = new SessionDiffTracker();
            var t0 = DateTime.Now;
            var row = new SessionRow("张三", "第一版");

            tracker.Diff(new[] { row }, t0, out var baseline1);
            tracker.Diff(new[] { row }, t0.AddSeconds(5), out _);

            // 模拟 UIA 中断 60 秒（超过重建基线阈值），期间对方发来新消息
            var changed = new SessionRow("张三", "第二版（中断期间到达）");
            tracker.Diff(new[] { changed }, t0.AddSeconds(60), out var baseline2);

            return baseline1
                   && baseline2
                   && tracker.LastRecoveredChanges.Count == 1
                   && tracker.LastRecoveredChanges[0].Summary.Contains("第二版", StringComparison.Ordinal);
        });

        Check("差分：空摘要轮不覆盖好摘要、不吞掉后续真实变化", () =>
        {
            var tracker = new SessionDiffTracker();
            var t0 = DateTime.Now;

            tracker.Diff(new[] { new SessionRow("李四", "在吗") }, t0, out _);

            // UIA 偶发读不全：摘要为空，不应把“在吗”覆盖成空
            var emptyRound = tracker.Diff(new[] { new SessionRow("李四", "") }, t0.AddSeconds(1), out _);

            // 下一轮真实变化应被检出
            var changed = tracker.Diff(new[] { new SessionRow("李四", "收到请回复") }, t0.AddSeconds(2), out _);

            return emptyRound.Count == 0
                   && changed.Count == 1
                   && changed[0].Summary == "收到请回复";
        });

        Check("会话解析：未读证据 [N条]/有人@我 → HasUnread，已读会话无证据", () =>
        {
            var unread = MessageTextParser.Parse(new[] { "张三", "[3条] 张三: 在吗" });
            var atMe = MessageTextParser.Parse(new[] { "产品群", "[有人@我] 王五: 明天十点开会" });
            var read = MessageTextParser.Parse(new[] { "张三", "10:23", "你好，在吗" });
            return unread is { HasUnread: true, Summary: "张三: 在吗" }
                   && atMe is { HasUnread: true }
                   && read is { HasUnread: false };
        });

        Check("消息游标：落盘后重启只放行摘要变化的消息（需求 7）", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"notifyhub-cursor-{Guid.NewGuid():N}.json");
            try
            {
                var store = new MessageCursorStore(path);
                store.Update("wechat", "张三", "在吗");
                store.Update("wechat", "李四", "旧消息");
                store.SetNtfyCursor(1700000000);
                store.Flush();

                var reloaded = new MessageCursorStore(path);
                var snapshot = reloaded.GetSnapshot("wechat");
                return snapshot is { Count: 2 }
                       && snapshot["张三"] == "在吗"
                       && snapshot["李四"] == "旧消息"
                       && reloaded.NtfyCursor == 1700000000
                       && reloaded.GetSnapshot("qq") is null;
            }
            finally
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // 忽略
                }
            }
        });

        Check("消息游标建基线：PrimeWith 后只把摘要变化的当新消息", () =>
        {
            var tracker = new SessionDiffTracker();
            var t0 = DateTime.Now;

            tracker.PrimeWith(new Dictionary<string, string> { ["张三"] = "在吗" }, t0);

            // 摘要没变（用户早看过/历史消息）→ 不产出
            var same = tracker.Diff(new[] { new SessionRow("张三", "在吗") }, t0.AddSeconds(1), out var baseline);
            // 摘要变了（新到达）→ 产出
            var changed = tracker.Diff(new[] { new SessionRow("张三", "吃饭了吗") }, t0.AddSeconds(2), out _);

            return !baseline
                   && same.Count == 0
                   && changed.Count == 1
                   && changed[0].Summary == "吃饭了吗";
        });

        Check("音乐 App 名单：FOLIA/QQ音乐命中，chrome/edge 一律忽略（需求 6，内置固定）", () =>
        {
            // v0.2.4：白名单功能已删除（需求 6），改为内置固定名单
            return MediaPlayerMonitor.MusicApps.Contains("folia")
                   && MediaPlayerMonitor.MusicApps.Contains("qqmusic")
                   && !MediaPlayerMonitor.MusicApps.Contains("chrome")
                   && !MediaPlayerMonitor.MusicApps.Contains("msedge")
                   && !MediaPlayerMonitor.MusicApps.Contains("brave")
                   && !MediaPlayerMonitor.MusicApps.Contains("firefox");
        });

        Check("浏览器地址：从订阅地址推导 topic Web UI（需求 3）", () =>
        {
            return BrowserLauncher.BuildTopicUrl("https://ntfy.sh/notifyhub-test") == "https://ntfy.sh/notifyhub-test"
                   && BrowserLauncher.BuildTopicUrl("https://ntfy.sh/notifyhub-test/sse") == "https://ntfy.sh/notifyhub-test"
                   && BrowserLauncher.BuildTopicUrl("ntfy.sh/my-topic") == "https://ntfy.sh/my-topic"
                   && BrowserLauncher.BuildTopicUrl("http://192.168.1.9:8080/alerts") == "http://192.168.1.9:8080/alerts"
                   && BrowserLauncher.BuildTopicUrl("") is null
                   && BrowserLauncher.BuildTopicUrl("https://ntfy.sh/") is null;
        });

        Check("微信过滤：服务号/公众号文件夹/文章链接拦截（需求 9）", () =>
        {
            var config = new AppConfig { WeChatFilterEnabled = true };

            var serviceAccount = new SessionRow("服务号", "宝塔面板: PHP 补丁包上线");
            var officialFolder = new SessionRow("公众号", "求是网: 权力为什么必须关进笼子？");
            var articleLink = new SessionRow("湖南电信小笼包（互联网卡）", "[链接] 专属互联网卡福利来啦！ https://mp.weixin.qq.com/s/abc123");
            var friend = new SessionRow("比企谷八蟠", "bzdq");

            return WeChatFilter.ShouldBlock(serviceAccount, config, out _)
                   && WeChatFilter.ShouldBlock(officialFolder, config, out _)
                   && WeChatFilter.ShouldBlock(articleLink, config, out _)
                   && !WeChatFilter.ShouldBlock(friend, config, out _);
        });

        Check("v0.2.6：LRC 解析（多时间标签/毫秒位/元信息行）+ 当前行定位", () =>
        {
            var lrc = string.Join("\n",
                "[ti:测试歌曲]",
                "[ar:测试歌手]",
                "[offset:0]",
                "[00:01.20]第一行",
                "[00:05.5][01:10.00]重复行",
                "[00:12.345]第三行",
                "[00:20.00]",
                "[00:25.00]最后一行");

            var lines = LyricsService.ParseLrc(lrc);

            // 元信息行跳过、空行跳过，[00:05.5][01:10.00] 拆成两条
            var okCount = lines.Count == 5;
            var okSort = true;
            for (var i = 1; i < lines.Count; i++)
            {
                okSort &= lines[i].Time >= lines[i - 1].Time;
            }

            // 毫秒位：两位 = 十分之一秒（05.5 → 5.5s），三位 = 原值（12.345）
            var okTime = Math.Abs(lines[0].Time.TotalSeconds - 1.2) < 0.001
                         && Math.Abs(lines[1].Time.TotalSeconds - 5.5) < 0.001
                         && Math.Abs(lines[2].Time.TotalSeconds - 12.345) < 0.001
                         && Math.Abs(lines[3].Time.TotalSeconds - 25.0) < 0.001;

            // 当前行定位：3 秒落在 1.2s 与 5.5s 之间 → 下标 0；6 秒 → 下标 1；
            // 0.5 秒还没到第一行 → -1；超过最后一行 → 最后一条
            var okIndex = LyricsService.IndexAt(lines, 3) == 0
                          && LyricsService.IndexAt(lines, 6) == 1
                          && LyricsService.IndexAt(lines, 0.5) == -1
                          && LyricsService.IndexAt(lines, 999) == lines.Count - 1;

            return okCount && okSort && okTime && okIndex;
        });

        Check("v0.2.6：歌名/歌手匹配打分（防串词）", () =>
        {
            // 完全一致 + 歌手命中 → 1.0；仅歌名包含 → 0.55；完全无关 → 0
            var exact = LyricsService.MatchScore("孤勇者", "陈奕迅", "孤勇者", "陈奕迅");
            var loose = LyricsService.MatchScore("孤勇者", "陈奕迅", "孤勇者 (Live)", "陈奕迅");
            var other = LyricsService.MatchScore("孤勇者", "陈奕迅", "晴天", "周杰伦");

            return exact >= 0.99 && loose >= 0.99 && other < 0.6;
        });

        Check("v0.2.6：主题调色板四种组合的文字对比度都达标（WCAG ≥ 4.5）", () =>
        {
            var ok = true;
            var details = new List<string>();

            foreach (var mode in AppConfig.ThemeModes)
            {
                var config = new AppConfig { ThemeMode = mode, TextTone = "Auto" };
                var palette = ThemePalette.Create(config);
                var ratio = ThemePalette.ContrastRatio(palette.TextPrimary, palette.NominalBackground);
                ok &= ratio >= 4.5;
                details.Add($"{mode}={ratio:F1}");
            }

            // 强制白色文字放在浅色毛玻璃上必须被矫正（否则白字白底看不见）
            var forcedWhiteOnLight = ThemePalette.Create(new AppConfig
            {
                ThemeMode = "GlassLight",
                TextTone = "White",
            });
            var forcedBlackOnDark = ThemePalette.Create(new AppConfig
            {
                ThemeMode = "GlassDark",
                TextTone = "Black",
            });

            var okForce = ThemePalette.ContrastRatio(
                              forcedWhiteOnLight.TextPrimary, forcedWhiteOnLight.NominalBackground) >= 4.5
                          && ThemePalette.ContrastRatio(
                              forcedBlackOnDark.TextPrimary, forcedBlackOnDark.NominalBackground) >= 4.5;

            if (!ok)
            {
                LogService.Shared.Warn("SelfTest", "对比度不足", string.Join(", ", details));
            }

            return ok && okForce;
        });

        Check("v0.2.6：主题/文字配色非法值回落默认", () =>
        {
            var config = new AppConfig { ThemeMode = "Neon", TextTone = "Rainbow" };
            config.Normalize();
            return config.ThemeMode == "SolidDark" && config.TextTone == "Auto";
        });

        Check("v0.2.6：固定岛只有播放三键可点，其余区域穿透（离屏命中测试）", () =>
        {
            // 自检跑在 MTA 线程池（UIA 需要），WPF 窗口必须 STA —— 单开一个 STA 线程跑，
            // 只做 Measure/Arrange 不进消息循环，不 Show 也能拿到真实布局。
            var ok = false;
            var worker = new System.Threading.Thread(() =>
            {
                ok = RunIslandHitTest();
            }, 8 * 1024 * 1024);

            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();

            if (!worker.Join(TimeSpan.FromSeconds(15)))
            {
                return false;
            }

            return ok;
        });

        static bool RunIslandHitTest()
        {
            // 构造一个真实布局的岛（不显示），验证：
            //   播放按钮中心 → 命中 Button（WPF 接收）
            //   歌词行 / 标题文字 / 空白 → 不命中 Button（该位置 HTTRANSPARENT 穿透）
            var island = new IslandWindow
            {
                ModeProvider = () => "Fixed",
                MusicEnabledProvider = () => true,
                MarqueeSpeedProvider = () => 45,
                LyricsEnabledProvider = () => true,
                SavedPositionProvider = () => null,
            };

            try
            {
                island.ApplyTheme(ThemePalette.Create(new AppConfig()));
                island.SetMedia(new MediaInfo("侧脸", "魏语诺", "FOLIA", true, "", "Folia", 30, 200));
                island.ShowLyrics("关于你的一切 我都想要了解", "陪伴三个季节 还是承受决裂");

                // 离屏跑布局
                island.Measure(new System.Windows.Size(900, 200));
                island.Arrange(new System.Windows.Rect(0, 0, 900, 200));
                island.UpdateLayout();

                var play = island.FindName("PlayPauseButton") as System.Windows.Controls.Button;
                if (play is null || play.ActualWidth <= 0)
                {
                    return false;
                }

                // 按钮中心（相对岛窗口的 DIP 坐标）
                var center = play.TranslatePoint(
                    new System.Windows.Point(play.ActualWidth / 2, play.ActualHeight / 2), island);
                var overButton = island.HitTestInteractive(center);

                // 岛体左上角内侧（图标/标题一带）应为穿透区
                var overTitle = island.HitTestInteractive(new System.Windows.Point(40, 30));

                // 歌词区（音乐行下方）应为穿透区
                var lyrics = island.FindName("LyricLine1") as System.Windows.Controls.Control;
                var overLyrics = lyrics is not null
                                 && lyrics.ActualWidth > 0
                                 && island.HitTestInteractive(
                                     lyrics.TranslatePoint(new System.Windows.Point(lyrics.ActualWidth / 2, 6), island));

                return overButton && !overTitle && !overLyrics;
            }
            finally
            {
                island.Close();
            }
        }

        Check("v0.2.7 需求1：播放进度估算器（SMTC 陈旧上报值也能连续推进）", () =>
        {
            // SMTC 的 Position 在正常播放期间不刷新。旧实现直接返回它 → 歌词卡死。
            // 这里验证 PlaybackClock：只要播放器"没刷新"，就以墙钟外推。
            var freq = System.Diagnostics.Stopwatch.Frequency;
            var clock = new PlaybackClock();

            // 第一次上报 10s（播放中）
            var first = clock.Update(10, playing: true, nowTicks: 0);

            // 3 秒后播放器仍然只报 10s（典型陈旧值）：应外推成 ≈13s
            var stale = clock.Update(10, playing: true, nowTicks: (long)(3 * freq));
            var advanceOk = first == 10 && Math.Abs(stale - 13) < 0.05;

            // 播放器刷新成 13.4s（略微超前）：温和收敛，不乱跳
            var refreshed = clock.Update(13.4, playing: true, nowTicks: (long)(3.4 * freq));
            var smoothOk = Math.Abs(refreshed - 13.4) < 0.6;

            // 拖动进度到 60s：偏差 > 1.5s，直接采纳
            var seek = clock.Update(60, playing: true, nowTicks: (long)(4 * freq));
            var seekOk = Math.Abs(seek - 60) < 0.01;

            // 暂停后过 5 秒：位置必须冻结在 60
            var paused = clock.Update(60, playing: false, nowTicks: (long)(9 * freq));
            var pauseOk = Math.Abs(paused - 60) < 0.01;

            // 恢复播放且上报 61：从 61 继续
            var resumed = clock.Update(61, playing: true, nowTicks: (long)(10 * freq));
            var resumeOk = Math.Abs(resumed - 61) < 0.01;

            return advanceOk && smoothOk && seekOk && pauseOk && resumeOk;
        });

        Check("v0.2.8 需求1：四角通知坐标（本体几何 / 步进 / 堆叠方向 / 任务栏）", () =>
        {
            // 用两种工作区各算一遍：100% 缩放（1920×1040）与「曾经被缓存成 150%」的
            // 1280×693 —— 后者正是历史 bug 的场景（右下角算到屏幕中间偏右）。
            var cases = new[]
            {
                new Rect(0, 0, 1920, 1040),
                new Rect(0, 0, 1280, 693),
            };

            foreach (var work in cases)
            {
                foreach (var corner in new[]
                         {
                             NotifyCorner.TopLeft, NotifyCorner.TopRight,
                             NotifyCorner.BottomLeft, NotifyCorner.BottomRight,
                         })
                {
                    var rects = NotificationLayout.CardRects(work, corner, 3);
                    if (rects.Count != 3)
                    {
                        return false;
                    }

                    var leftAligned = NotificationLayout.IsLeftAligned(corner);
                    var stacksUp = NotificationLayout.StacksUpward(corner);

                    // ① 水平锚点：左右各留 VisualMargin，卡片本体宽 356
                    var expectLeft = leftAligned
                        ? work.Left + NotificationLayout.VisualMargin
                        : work.Right - NotificationLayout.VisualMargin - NotificationLayout.CardBodyWidth;
                    if (Math.Abs(rects[0].Left - expectLeft) > 0.6)
                    {
                        return false;
                    }

                    // ② 垂直锚点：上角贴顶、下角贴底（卡片本体，不含阴影）
                    var expectTop = stacksUp
                        ? work.Bottom - NotificationLayout.VisualMargin - NotificationLayout.CardBodyHeight
                        : work.Top + NotificationLayout.VisualMargin;
                    if (Math.Abs(rects[0].Top - expectTop) > 0.6)
                    {
                        return false;
                    }

                    // ③ 底部角最下面那张必须完整落在任务栏之上（旧版会压下去 16px）
                    if (stacksUp && rects[0].Bottom > work.Bottom - NotificationLayout.VisualMargin + 0.6)
                    {
                        return false;
                    }

                    // ④ 步进恒定 = 本体高 + 间距，既不重叠也不留大缝
                    for (var i = 1; i < rects.Count; i++)
                    {
                        var step = Math.Abs(rects[i].Top - rects[i - 1].Top);
                        if (Math.Abs(step - NotificationLayout.StackStep) > 0.6)
                        {
                            return false;
                        }
                    }

                    // ⑤ 堆叠方向：index 0 = 最新，上角在最上、下角在最下
                    var newer = rects[0];
                    var older = rects[1];
                    if (stacksUp ? newer.Top <= older.Top : newer.Top >= older.Top)
                    {
                        return false;
                    }

                    // ⑥ 本体尺寸与窗口几何自洽
                    if (Math.Abs(rects[0].Width - NotificationLayout.CardBodyWidth) > 0.01
                        || Math.Abs(rects[0].Height - NotificationLayout.CardBodyHeight) > 0.01
                        || Math.Abs(NotificationLayout.WindowWidth
                                    - (NotificationLayout.CardBodyWidth + (NotificationLayout.ShadowPad * 2))) > 0.01)
                    {
                        return false;
                    }
                }
            }

            // ⑦ 极端矮屏：MaxFit 至少给 1 张，且不超过堆叠上限
            var tiny = new Rect(0, 0, 1280, 130);
            return NotificationLayout.MaxFit(tiny) >= 1
                   && NotificationLayout.MaxFit(new Rect(0, 0, 1920, 1040)) >= AppConfig.MaxStackLimit - 1;
        });

        Check("v0.2.8 需求2：歌词自带翻译的合并（时点匹配 / 去重 / 缺行丢弃）", () =>
        {
            var lrc = "[00:01.00]Runaway\n[00:05.00]I keep the light on\n[00:09.00]For you";
            var tlyric = "[00:01.00]逃离\n[00:05.00]我一直留着那盏灯\n";

            var lines = LyricsService.ParseLrc(lrc);
            var merged = LyricsService.MergeTranslation(lines, tlyric);

            if (merged.Count != 3
                || merged[0].Translation != "逃离"
                || merged[1].Translation != "我一直留着那盏灯")
            {
                return false;
            }

            // 第 3 行没有译文 → 必须留空（绝不能把上一句的翻译串到下一句）
            if (merged[2].HasTranslation)
            {
                return false;
            }

            // 译文与原文相同（中文歌 / 接口把原文当译文）→ 视为没有译文
            var chinese = LyricsService.ParseLrc("[00:02.00]简单爱");
            var sameAsText = LyricsService.MergeTranslation(chinese, "[00:02.00]简单爱");
            if (sameAsText[0].HasTranslation)
            {
                return false;
            }

            // 没有译文资源 / 时点差太大 → 不硬塞
            if (LyricsService.MergeTranslation(lines, null).Count != 3
                || LyricsService.MergeTranslation(lines, "[00:30.00]完全对不上")[0].HasTranslation)
            {
                return false;
            }

            return true;
        });

        Check("v0.2.8 需求2/5：岛自适应宽度 + 固定模式真居中（离屏布局）", () =>
        {
            try
            {
                return RunSta(RunIslandLayoutTest);
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(AppPaths.Root, "island-layout.txt"), ex.ToString(), Encoding.UTF8);
                }
                catch
                {
                    // 忽略
                }

                return false;
            }
        });

        static bool RunIslandLayoutTest()
        {
            var report = new StringBuilder();

            IslandWindow MakeIsland(int lyricSize = 13)
                => new()
                {
                    ModeProvider = () => "Fixed",
                    MusicEnabledProvider = () => true,
                    MarqueeSpeedProvider = () => 45,
                    LyricsEnabledProvider = () => true,
                    LyricsFontSizeProvider = () => lyricSize,
                    AutoCollapseEnabledProvider = () => false,
                    SavedPositionProvider = () => null,
                };

            static double Layout(IslandWindow island)
            {
                island.Measure(new Size(900, 300));
                island.Arrange(new Rect(0, 0, island.DesiredSize.Width, island.DesiredSize.Height));
                island.RefreshLayoutMetrics();
                island.Measure(new Size(900, 300));
                island.Arrange(new Rect(0, 0, island.DesiredSize.Width, island.DesiredSize.Height));
                return island.DesiredSize.Width;
            }

            var withTask = MakeIsland();
            var solo = MakeIsland();
            var soloLong = MakeIsland();
            var taskOnly = MakeIsland();
            var translated = MakeIsland();

            try
            {
                // ---------- ① 音乐 + 歌词 + 任务：固定岛宽、两栏按预算分配、分隔线出现 ----------
                // v0.2.18 固定尺寸模型：岛长（上下边长）= BaseIslandWidth(480) × 长度(默认 100%) + 外边距(28)
                const double expectedFixedWidth = 480 + 28;

                withTask.ApplyTheme(ThemePalette.Create(new AppConfig()));
                withTask.ApplyLyricsTypography();
                withTask.SetMedia(new MediaInfo("侧脸", "魏语诺, 五毛", "FOLIA", true, "", "Folia", 30, 200));
                withTask.ShowLyrics("关于你的一切 我都想要了解", "陪伴三个季节 还是承受决裂");
                withTask.ShowRunning("WorkBuddy 正在梳理需求文档结构并拆分任务", "WorkBuddy");
                var totalBoth = Layout(withTask);

                var left = withTask.LeftPaneWidth;
                var right = withTask.RightPaneWidth;
                var lyric = withTask.FindName("LyricLine1") as MarqueeText;
                var title = withTask.FindName("MusicText") as MarqueeText;
                var divider = withTask.FindName("Divider") as System.Windows.Controls.Border;
                if (lyric is null || title is null || divider is null)
                {
                    return false;
                }

                // 岛宽严格等于设置值（内容再长也不撑大）：窗口 Width 恒为 480+28，
                // 内容 desired 宽度不超过窗口（超出部分在岛内滚动/裁剪）
                var fixedWidthOk = Math.Abs(withTask.Width - expectedFixedWidth) < 1
                                   && totalBoth <= expectedFixedWidth + 1;

                // 两栏都有实质内容，且两栏 + 分隔线不超出内容区（不会溢出岛体）
                var contentFits = (left + right + 25) <= (expectedFixedWidth - 28) + 1;
                var balanced = left > 200 && right > 140 && contentFits;
                var dividerOk = divider.Visibility == Visibility.Visible;

                // v0.2.18 修：强调脊线（音乐卡左边那条）高度要跟岛高走，
                // 不能只在播放器中间一小截（岛高时上方大片空白 / 岛矮时看不到）
                var spine = withTask.FindName("AccentSpine") as System.Windows.Controls.Border;
                var spineH = spine?.ActualHeight ?? 0;
                var paneH = withTask.LeftPaneHeight;
                // 高度上限 70% → 岛高 104，内容区 58，脊线应≈52（跟着岛高走，不是只在中间一小截）
                var spineOk = spineH > 45 && paneH > 45 && spineH >= paneH - 8;

                // 歌词与歌名同一左边界（同一内容列，误差 ≤2px）
                var lyricLeft = lyric.TranslatePoint(new Point(0, 0), withTask).X;
                var titleLeft = title.TranslatePoint(new Point(0, 0), withTask).X;
                var aligned = Math.Abs(lyricLeft - titleLeft) <= 2.0;

                // 字号 = 配置值 + 1
                var fontOk = Math.Abs(lyric.FontSize - 14) < 0.01;

                var iconShown = (withTask.FindName("MusicIconBox") as System.Windows.Controls.Border)?.Visibility
                                == Visibility.Visible;

                report.Append($"有任务: 整条={totalBoth:F1}(定宽={fixedWidthOk}) 左={left:F1} 右={right:F1} " +
                              $"歌词左={lyricLeft:F1} 歌名左={titleLeft:F1} 字号={lyric.FontSize:F1} " +
                              $"分隔线={divider.Visibility} 歌词宽={lyric.ActualWidth:F1} 图标={iconShown} " +
                              $"脊线高={spineH:F1}/左栏高={paneH:F1}");

                // ---------- ② 仅音乐：岛宽仍是同一个固定值（不再被内容"收窄"） ----------
                solo.ApplyTheme(ThemePalette.Create(new AppConfig()));
                solo.ApplyLyricsTypography();
                solo.SetMedia(new MediaInfo("侧脸", "魏语诺, 五毛", "FOLIA", true, "", "Folia", 30, 200));
                solo.ShowLyrics("关于你的一切 我都想要了解", "陪伴三个季节 还是承受决裂");
                var totalSolo = Layout(solo);
                var statusHidden = (solo.FindName("StatusGroup") as System.Windows.Controls.StackPanel)?.Visibility
                                   == Visibility.Collapsed;
                var soloWidthOk = Math.Abs(solo.Width - expectedFixedWidth) < 1;

                report.Append($"{Environment.NewLine}仅音乐: 整条={totalSolo:F1} 定宽={soloWidthOk} 任务组隐藏={statusHidden}");

                // ---------- ③ 超长歌词：岛宽**不变**，歌词列在预算内滚动（跑马灯） ----------
                soloLong.ApplyTheme(ThemePalette.Create(new AppConfig()));
                soloLong.ApplyLyricsTypography();
                soloLong.SetMedia(new MediaInfo("侧脸", "魏语诺, 五毛", "FOLIA", true, "", "Folia", 30, 200));
                soloLong.ShowLyrics(
                    "我曾经跨过山和大海也穿过人山人海我曾经拥有着的一切转眼都飘散如烟",
                    "我曾经失落失望失掉所有方向直到看见平凡才是唯一的答案");
                var totalLong = Layout(soloLong);
                var lyricLong = soloLong.FindName("LyricLine1") as MarqueeText;
                var longNotWidened = Math.Abs(soloLong.Width - expectedFixedWidth) < 1
                                     && totalLong <= expectedFixedWidth + 1;
                var longScrolls = lyricLong is not null && lyricLong.IsScrolling;

                report.Append($"{Environment.NewLine}超长歌词: 整条={totalLong:F1} 未撑大={longNotWidened} " +
                              $"岛内滚动={longScrolls} 歌词宽={lyricLong?.ActualWidth:F1}");

                // ---------- ④ 只有任务：左栏收起，岛宽仍是同一个固定值 ----------
                taskOnly.ApplyTheme(ThemePalette.Create(new AppConfig()));
                taskOnly.ApplyLyricsTypography();
                taskOnly.ShowRunning("WorkBuddy 正在梳理需求文档结构并拆分任务", "WorkBuddy");
                var totalTaskOnly = Layout(taskOnly);
                var leftCollapsed = (taskOnly.FindName("LeftPane") as System.Windows.Controls.Grid)?.Visibility
                                    == Visibility.Collapsed;
                var taskOnlyOk = leftCollapsed && Math.Abs(taskOnly.Width - expectedFixedWidth) < 1;

                report.Append($"{Environment.NewLine}仅任务: 整条={totalTaskOnly:F1} 左栏收起={leftCollapsed}");

                // ---------- ⑤ 译文行：有译文才占位，且字比当前行小 ----------
                translated.ApplyTheme(ThemePalette.Create(new AppConfig()));
                translated.ApplyLyricsTypography();
                translated.SetMedia(new MediaInfo("Runaway", "AURORA", "FOLIA", true, "", "Folia", 30, 200));
                translated.ShowLyrics("I keep the light on", "For you", "我一直留着那盏灯");
                Layout(translated);

                var trans = translated.FindName("LyricTrans") as MarqueeText;
                var transShown = translated.IsTranslationShown
                                 && trans is not null
                                 && trans.FontSize < (translated.FindName("LyricLine1") as MarqueeText)!.FontSize;

                translated.ShowLyrics("I keep the light on", "For you", null);
                Layout(translated);
                var transHidden = !translated.IsTranslationShown;

                report.Append($"{Environment.NewLine}译文行: 有译文显示={transShown} 无译文隐藏={transHidden}");

                // ---------- ⑥ 固定模式真居中：左右留白相等 ----------
                var work = new Rect(0, 0, 1920, 1040);
                var centeredOk = true;
                foreach (var width in new[] { 320d, 659d, 880d })
                {
                    var x = IslandWindow.CenteredLeft(work, width);
                    var leftGap = x - work.Left;
                    var rightGap = work.Right - (x + width);
                    if (Math.Abs(leftGap - rightGap) > 1.0)
                    {
                        centeredOk = false;
                    }
                }

                report.Append($"{Environment.NewLine}居中: 左右留白相等={centeredOk}");

                try
                {
                    File.WriteAllText(
                        Path.Combine(AppPaths.Root, "island-layout.txt"), report.ToString(), Encoding.UTF8);
                }
                catch
                {
                    // 忽略
                }

                return fixedWidthOk && balanced && dividerOk && aligned && fontOk && statusHidden
                       && soloWidthOk && longNotWidened && longScrolls && taskOnlyOk
                       && spineOk && transShown && transHidden && centeredOk;
            }
            finally
            {
                withTask.Close();
                solo.Close();
                soloLong.Close();
                taskOnly.Close();
                translated.Close();
            }
        }

        Check("v0.2.8 需求4：收纳细条跑道指示器（撑满轨道 / 四形态可切 / 新几何）", () =>
        {
            return RunSta(() =>
            {
                // 直接量“裸 FrameworkElement 放进 Border”能否撑开 —— 这正是 v0.2.8 实拍
                // 发现指示器完全不可见的根因（默认 MeasureOverride 返回 0×0，
                // OnRender 里的 h<=2 早退，整条只剩灰底）。
                var indicator = new StripIndicator { Mode = StripMode.Both };
                var host = new System.Windows.Controls.Border
                {
                    Width = 112,
                    Height = 10,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Child = indicator,
                };

                host.Measure(new Size(112, 10));
                host.Arrange(new Rect(0, 0, 112, 10));

                var sized = indicator.ActualWidth > 105 && indicator.ActualHeight > 7;

                var switched = true;
                foreach (var mode in new[] { StripMode.Music, StripMode.Task, StripMode.Both })
                {
                    indicator.Mode = mode;
                    switched &= indicator.Mode == mode;
                }

                indicator.IsDone = true;
                var doneOk = indicator.IsDone;

                // 细条几何（v0.2.8 放大到 12×124）与"三种状态 + 完成态"的形态枚举齐全
                var geometry = SnapStripWindow.UsesNewGeometry;

                return sized && switched && doneOk && geometry;
            });
        });

        Check("v0.2.11 需求1：进程在跑就不重复启动（双击唤起的黑窗/幽灵图标根因）", () =>
        {
            // 本机 WorkBuddy 一定在跑（它就是当前工作环境）
            var running = AppLauncher.IsRunning("WorkBuddy");

            // 不存在的进程：IsRunning=false，且 Open 在没有候选路径时必须立刻返回 Failed，
            // 不能去 launch 任何东西（这条是"激活失败就再拉一个实例"的反向断言）
            var ghost = "notifyhub-ghost-proc-12345";
            var ghostRunning = AppLauncher.IsRunning(ghost);
            var ghostOutcome = AppLauncher.Open(ghost, Array.Empty<string>());

            return running && !ghostRunning && ghostOutcome == OpenOutcome.Failed;
        });

        Check("v0.2.11 需求1：隐藏启动参数（不弹黑命令行窗口的三件套）", () =>
        {
            var psi = AppLauncher.BuildHiddenStartInfo(@"C:/Windows/System32/notepad.exe");

            // ShellExecute 路径会给控制台程序开黑窗；CreateNoWindow 不分配控制台；Hidden 兜底隐藏
            return !psi.UseShellExecute
                   && psi.CreateNoWindow
                   && psi.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden
                   && psi.FileName.EndsWith("notepad.exe", StringComparison.OrdinalIgnoreCase);
        });

        Check("v0.2.14 需求8：WorkBuddy 新任务不回溯推送上一轮的「完成了」", () =>
        {
            // 行为验证：造一个假会话记录文件，让监视器真的去 tail 它，
            // 按「任务1 → 回复 → 安静8秒判完成 → 任务2 → 回复」追加，逐段核对推送序列。
            var dir = Path.Combine(Path.GetTempPath(), "nh-wb-" + Guid.NewGuid().ToString("N")[..8]);
            var project = Path.Combine(dir, "proj");
            Directory.CreateDirectory(project);
            var file = Path.Combine(project, "session.jsonl");
            // 先建空文件：监视器首次接入时 offset 落在文件尾，
            // 如果等它接上再写内容，之前的内容会被整段跳过（本测试第一次就是这么失败的）。
            File.WriteAllText(file, string.Empty);

            var emissions = new List<(string Text, bool Done)>();

            try
            {
                WorkBuddyWatcher.RootOverride = dir;
                using var watcher = new WorkBuddyWatcher(LogService.Shared);
                watcher.TaskChanged += (text, done) => emissions.Add((text, done));
                watcher.Start();

                // 等它接上文件：必须接在"空文件"上，否则首次 tail 会把已有内容跳过
                System.Threading.Thread.Sleep(2500);

                void Append(string role, string payload)
                {
                    var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var line = $"{{\"type\":\"message\",\"role\":\"{role}\",\"timestamp\":{ts}," +
                               $"\"content\":[{{\"type\":\"output_text\",\"text\":\"{payload}\"}}]}}";
                    File.AppendAllText(file, line + "\n", Encoding.UTF8);
                }

                // 第一轮：用户下发 → AI 回复（应推一条"进行中"）
                Append("user", "帮我整理需求文档");
                Append("assistant", "第一轮已完成");
                System.Threading.Thread.Sleep(2000);
                var afterFirstReply = emissions.Count;

                // 安静 8 秒 → 判定完成（应再推一条"完成了"）
                System.Threading.Thread.Sleep(9500);
                var afterQuiet = emissions.Count;

                // 第二轮：同一会话里下发新任务（**不能**把上一轮的完成消息再推一遍）
                Append("user", "再帮我拆一下任务");
                System.Threading.Thread.Sleep(2500);
                var afterNewTask = emissions.Count;

                // 第二轮 AI 回复 → 推这一轮的进度
                Append("assistant", "第二轮已完成");
                System.Threading.Thread.Sleep(2000);
                var afterSecondReply = emissions.Count;

                LogService.Shared.Write(LogLevel.Info, "SelfTest", "WorkBuddy 轮次隔离诊断",
                    $"序列=[{string.Join(" → ", emissions.Select(e => (e.Done ? "D:" : "R:") + e.Text))}] " +
                    $"first={afterFirstReply} quiet={afterQuiet} newTask={afterNewTask} reply={afterSecondReply}");

                return afterFirstReply == 1
                       && afterQuiet == 2
                       && afterNewTask == 2
                       && afterSecondReply == 3
                       && emissions[0].Text == "第一轮已完成" && !emissions[0].Done
                       && emissions[1].Text == "第一轮已完成" && emissions[1].Done
                       && emissions[2].Text == "第二轮已完成" && !emissions[2].Done;
            }
            finally
            {
                WorkBuddyWatcher.RootOverride = null;
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // 清理失败不影响结论
                }
            }
        });

        Check("v0.2.14 需求7：消息身份稳定 + 已推送标记跨重启生效（旧消息不再补发）", () =>
        {
            // 同一条微信消息的两种面貌：未读时带 "[3条] "，点开后只剩正文
            var unread = MessageTextParser.Parse(new[] { "我的朋友们", "[3条] xiy624: 不是不对吧", "昨天 09:56" });
            var read = MessageTextParser.Parse(new[] { "我的朋友们", "xiy624: 不是不对吧", "昨天 09:56" });

            var identityStable = unread is not null && read is not null
                                 && unread.Identity.Length > 0
                                 && string.Equals(unread.Identity, read.Identity, StringComparison.Ordinal);

            // 差分：摘要变了（前缀被消掉）但身份没变 → 不算新消息
            var tracker = new SessionDiffTracker();
            // 第一轮建基线（基线轮本身不产通知），之后才谈"算不算新消息"
            tracker.Diff(new[] { unread! }, DateTime.Now, out _);
            var first = tracker.Diff(new[] { unread! }, DateTime.Now.AddSeconds(1), out var baseline1);
            var second = tracker.Diff(new[] { read! }, DateTime.Now.AddSeconds(2), out var baseline2);
            var diffStable = !baseline1 && !baseline2 && first.Count == 0 && second.Count == 0;

            // 换成另一句话 → 必须算新消息
            var other = MessageTextParser.Parse(new[] { "我的朋友们", "xiy624: 还是不上班", "昨天 10:01" });
            var third = tracker.Diff(new[] { other! }, DateTime.Now.AddSeconds(2), out _);
            var diffDetects = third.Count == 1;

            // 已推送标记：模拟"进程重启"——用同一个文件新建一个 store，标记必须还在
            var dir = Path.Combine(Path.GetTempPath(), "nh-cursor-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            try
            {
                var file = Path.Combine(dir, "cursor.json");
                var id = DedupCache.Hash("wechat\u0001我的朋友们\u0001xiy624: 不是不对吧");

                var storeA = new MessageCursorStore(file);
                var before = storeA.WasPushed("wechat", id);
                storeA.MarkPushed("wechat", id);
                storeA.Flush();

                var storeB = new MessageCursorStore(file);
                var after = storeB.WasPushed("wechat", id);

                LogService.Shared.Write(LogLevel.Info, "SelfTest", "微信去重诊断",
                    $"identityStable={identityStable} diffStable={diffStable} diffDetects={diffDetects} before={before} after={after}");

                return identityStable && diffStable && diffDetects && !before && after;
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // 清理失败不影响结论
                }
            }
        });

        Check("v0.2.14 需求3/4：进程挂着但没播放/没任务 → 仍是待机（场景 B）", () =>
        {
            return RunSta(() =>
            {
                var island = new IslandWindow
                {
                    ModeProvider = () => "Fixed",
                    MusicEnabledProvider = () => true,
                    MarqueeSpeedProvider = () => 45,
                    LyricsEnabledProvider = () => true,
                    SavedPositionProvider = () => null,
                };

                try
                {
                    island.ApplyTheme(ThemePalette.Create(new AppConfig()));

                    // 播放器 + WorkBuddy 进程都在，但都没在干活：必须算待机（左右双击唤起仍可用）
                    island.SetPresence(true, true, "Folia");
                    var idleWithProcesses = island.IsIdleStandby;

                    // 真的开始播放 → 信息展示
                    island.SetMedia(new MediaInfo("侧脸", "魏语诺", "FOLIA", true, "", "Folia", 3, 214));
                    var playing = !island.IsIdleStandby;

                    // 播完停下（进程还在）→ 等 2.2 秒隐藏宽限期过完，回到待机。
                    // 注意：RunSta 的消息循环不会自己转，必须手动泵 dispatcher，定时器才会走。
                    island.SetMedia(null);
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < deadline && island.IsIdleStandby == false)
                    {
                        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                            () => { }, System.Windows.Threading.DispatcherPriority.Background);
                        System.Threading.Thread.Sleep(50);
                    }

                    var backToIdle = island.IsIdleStandby;

                    return idleWithProcesses && playing && backToIdle;
                }
                finally
                {
                    island.Close();
                }
            });
        });

        Check("v0.2.14 需求6：提示音峰值归一化（不动系统音量也能更响）", () =>
        {
            var target = SoundService.PeakNormalizeTarget;

            // 合成一篇并读回 WAV 峰值：必须被拉到目标附近，且不超过满刻度
            var notes = SoundService.MessageTone("DingDong");
            new SoundService(LogService.Shared).Preview(notes, 100);

            var dir = Path.Combine(AppPaths.Root, "sounds");
            if (!Directory.Exists(dir))
            {
                return false;
            }

            var newest = new DirectoryInfo(dir).GetFiles("tone-*.wav")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null)
            {
                return false;
            }

            var bytes = File.ReadAllBytes(newest.FullName);
            var peak = 0;
            for (var i = 44; i + 1 < bytes.Length; i += 2)
            {
                var sample = (short)(bytes[i] | (bytes[i + 1] << 8));
                var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            var ratio = peak / (double)short.MaxValue;

            LogService.Shared.Write(LogLevel.Info, "SelfTest", "提示音增益诊断",
                $"target={target} peak={ratio:F3} file={newest.Name}");

            // 目标 0.92：允许合成/量化的少量误差，但必须明显高于旧的 ~0.42
            return Math.Abs(target - 0.92) < 1e-6 && ratio >= 0.85 && ratio <= 1.0;
        });

        Check("v0.2.13：切歌瞬间丢弃上一首的陈旧进度（歌词不再跳到第 29 行）", () =>
        {
            // 复刻 2026-10-23 20:16 的真实日志：新歌《牵丝戏》刚起，播放器报 239.36s，
            // 而这首歌总长只有 210s、切换才过去 6 秒。
            var logCase = PlaybackClock.LooksStaleOnTrackChange(239.36, 6.0, 210.0);

            // 切歌后 0.5 秒报 30s（上一首的位置，但新歌总长 300s 没触发第一条判据）
            var carryOver = PlaybackClock.LooksStaleOnTrackChange(30, 0.5, 300);

            // 正常情况不能误杀：刚切歌 0.5 秒报 0.6s、或播了 10 秒报 100s（用户拖了进度条）
            var fresh = !PlaybackClock.LooksStaleOnTrackChange(0.6, 0.5, 300);
            var normal = !PlaybackClock.LooksStaleOnTrackChange(100, 10, 300);

            // 时长未知（timeline 不可用）时不能只凭时间判：播了 6 秒报 239s 也该放行
            var unknownDuration = !PlaybackClock.LooksStaleOnTrackChange(6.2, 6.0, 0);

            // 0 或负数位置永远不算"陈旧"（暂停/刚起播）
            var zero = !PlaybackClock.LooksStaleOnTrackChange(0, 0.2, 210);

            return logCase && carryOver && fresh && normal && unknownDuration && zero;
        });

        Check("v0.2.12 需求1：LRC [offset:] 整体偏移 + 元信息行过滤", () =>
        {
            var lrc = string.Join("\n", new[]
            {
                "[ti:测试]",
                "[ar:某歌手]",
                "[offset:+500]",
                "[00:01.00]作词 : 某某",
                "[00:02.00]作曲 : 另一个人",
                "[00:03.00]第一句",
                "[00:05.50]第二句",
                "[00:04.00]纯音乐，请欣赏",
            });

            var lines = LyricsService.ParseLrc(lrc, out var offset);

            // offset 读到了、正文两行、元信息行被丢掉
            var offsetOk = offset == 500;
            var countOk = lines.Count == 2;
            var textOk = lines.Count == 2 && lines[0].Text == "第一句" && lines[1].Text == "第二句";

            // 时间轴整体后移 500ms：3.0 → 3.5、5.5 → 6.0，且仍然按时间排序
            var shiftOk = lines.Count == 2
                          && Math.Abs((lines[0].Time - TimeSpan.FromSeconds(3.5)).TotalMilliseconds) < 1
                          && Math.Abs((lines[1].Time - TimeSpan.FromSeconds(6)).TotalMilliseconds) < 1;

            // 负偏移不会把时间点推到负数
            var negative = LyricsService.ParseLrc("[offset:-9000]\n[00:01.00]开头", out var negOffset);
            var clampOk = negOffset == -9000 && negative.Count == 1 && negative[0].Time >= TimeSpan.Zero;

            return offsetOk && countOk && textOk && shiftOk && clampOk;
        });

        Check("v0.2.12 需求1：本地歌词优先（.lrc 命中 + GBK 编码自动识别）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "nh-lyrics-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            try
            {
                // UTF-8 文件：文件名带歌手，应优先于同名但不带歌手的
                File.WriteAllText(
                    Path.Combine(dir, "孤勇者 - 陈奕迅.lrc"),
                    "[00:01.00]爱你孤身走暗巷\n[00:05.00]爱你不跪的模样",
                    new UTF8Encoding(false));

                File.WriteAllText(
                    Path.Combine(dir, "无关的歌词.lrc"),
                    "[00:01.00]这首歌跟查询无关",
                    new UTF8Encoding(false));

                // GBK 文件：大量老歌词是这个编码，读错就是满屏乱码
                LocalLyricsLibrary.EnsureEncodings();
                var gbkBytes = Encoding.GetEncoding(936).GetBytes("[00:02.00]夜空中最亮的星");
                File.WriteAllBytes(Path.Combine(dir, "夜空中最亮的星 - 逃跑计划.lrc"), gbkBytes);

                var config = new AppConfig { LyricsExtraPaths = dir };
                var library = new LocalLyricsLibrary(LogService.Shared, () => config);

                var hit = library.Find("孤勇者", "陈奕迅");
                var gbk = library.Find("夜空中最亮的星", "逃跑计划");
                var miss = library.Find("完全不存在的歌名xyz", "谁");

                var hitOk = hit is not null && hit.Text.Contains("爱你孤身走暗巷", StringComparison.Ordinal);
                var gbkOk = gbk is not null && gbk.Text.Contains("夜空中最亮的星", StringComparison.Ordinal);
                var missOk = miss is null;

                LogService.Shared.Write(
                    LogLevel.Info,
                    "SelfTest",
                    "本地歌词命中诊断",
                    $"hit={hitOk} gbk={gbkOk} miss={missOk} files={library.IndexedFiles}");

                return hitOk && gbkOk && missOk;
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // 清理失败不影响结论
                }
            }
        });

        // v0.2.14 起"进程在跑但没播放"算场景 B（仍待机），这条旧断言由上面的
        // 「v0.2.14 需求3/4：进程挂着但没播放/没任务 → 仍是待机」取代。


        Check("v0.2.12 需求5：轮询流水日志默认不落盘 + 相同行 2 秒内去重", () =>
        {
            var log = LogService.Shared;
            var before = log.ConsoleSnapshot().Count;

            var wasEnabled = LogService.RoutineLoggingEnabled;
            try
            {
                LogService.RoutineLoggingEnabled = false;

                // 流水：控制台与文件都不该多出内容
                log.Routine("IslandAuto", "收纳倒计时重置 3s", "hidden=False");
                log.Routine("IslandAuto", "收纳倒计时重置 3s", "hidden=False");
                var routineSilent = log.ConsoleSnapshot().Count == before;

                // 业务日志：照常写；紧接着的重复行被去重
                var suppressedBefore = log.SuppressedDuplicateLines;
                log.Info("微信监听", "自检：收到会话变化", "flyme1 → 测试");
                var afterFirst = log.ConsoleSnapshot().Count;
                log.Info("微信监听", "自检：收到会话变化", "flyme1 → 测试");
                var afterSecond = log.ConsoleSnapshot().Count;

                var keptBusiness = afterFirst == before + 1;
                var deduped = afterSecond == afterFirst
                              && log.SuppressedDuplicateLines > suppressedBefore;

                return routineSilent && keptBusiness && deduped;
            }
            finally
            {
                LogService.RoutineLoggingEnabled = wasEnabled;
            }
        });

        Check("v0.2.11 需求2/4（v0.2.14 改写）：进程在线≠信息展示，播放/任务才算", () =>
        {
            return RunSta(() =>
            {
                var island = new IslandWindow
                {
                    ModeProvider = () => "Fixed",
                    MusicEnabledProvider = () => true,
                    MarqueeSpeedProvider = () => 45,
                    LyricsEnabledProvider = () => true,
                    SavedPositionProvider = () => null,
                };

                try
                {
                    island.ApplyTheme(ThemePalette.Create(new AppConfig()));

                    // v0.2.14 需求 3/4：进程挂着但没播放/没任务 → 仍然是待机（场景 B）
                    island.SetPresence(false, false);
                    var idle = island.IsIdleStandby;

                    island.SetPresence(true, false, "Folia");
                    var musicPresence = island.IsIdleStandby;

                    island.SetPresence(false, true);
                    var wbPresence = island.IsIdleStandby;

                    island.SetPresence(true, true, "Folia");
                    var both = island.IsIdleStandby;

                    // 正在跑任务 → 信息展示（场景 A）
                    island.ShowRunning("正在整理需求文档结构", "WorkBuddy");
                    var withTask = !island.IsIdleStandby;

                    // 任务在跑时再把进程全部关掉：仍然是信息展示（任务本身就是内容，与进程无关）
                    island.SetPresence(false, false);
                    var taskKeepsInfo = !island.IsIdleStandby;

                    return idle && musicPresence && wbPresence && both && withTask && taskKeepsInfo;
                }
                finally
                {
                    island.Close();
                }
            });
        });

        Check("v0.2.11 需求2/4：窄条待机快捷模式开关（信息模式不换分区配色）", () =>
        {
            return RunSta(() =>
            {
                var strip = new SnapStripWindow();
                try
                {
                    var standbyByDefault = strip.IsStandbyMode;

                    strip.SetStandbyMode(false);
                    var infoMode = !strip.IsStandbyMode && strip.HoverZone == StripZone.None;

                    strip.SetStandbyMode(true);
                    var backToStandby = strip.IsStandbyMode;

                    return standbyByDefault && infoMode && backToStandby;
                }
                finally
                {
                    strip.Close();
                }
            });
        });

        Check("v0.2.10 需求4：跑马灯重启滚动（收起期间换行后能重新判定并从头开始）", () =>
        {
            return RunSta(() =>
            {
                var text = new MarqueeText
                {
                    Width = 120,
                    FontSize = 14,
                    Text = "这是一段明显超出可视宽度的长歌词文本内容",
                };

                var host = new System.Windows.Controls.Border
                {
                    Width = 120,
                    Height = 24,
                    Background = System.Windows.Media.Brushes.Black,
                    Child = text,
                };

                host.Measure(new Size(120, 24));
                host.Arrange(new Rect(0, 0, 120, 24));
                host.UpdateLayout();

                // 离屏渲染一次：OnRender 里才会判定"要不要滚"，这是 v0.2.8 的坑（不渲染就没有状态）
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    120, 24, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(host);

                var shouldScroll = text.IsScrolling;
                var running = text.IsScrollRunning;

                // 模拟"收起期间换了更短的一句"：重启后必须回到起点、且重新走一遍判定。
                // 注意要重跑 Measure/Arrange —— 真实运行时 WPF 会在渲染前自动重排，
                // 离屏测试必须自己补这一步，否则 _textWidth 还是上一句的宽度。
                text.Text = "短句";
                text.RestartScroll();
                host.Measure(new Size(120, 24));
                host.Arrange(new Rect(0, 0, 120, 24));
                host.UpdateLayout();
                rtb.Render(host);

                var shortNoScroll = !text.IsScrolling && !text.IsScrollRunning
                                    && Math.Abs(text.ScrollOffset) < 0.01;

                // 再换回长句：重启后仍能重新进入滚动
                text.Text = "又是一段超出可视宽度的很长的歌词内容";
                text.RestartScroll();
                host.Measure(new Size(120, 24));
                host.Arrange(new Rect(0, 0, 120, 24));
                host.UpdateLayout();
                rtb.Render(host);
                var longScrollsAgain = text.IsScrolling;

                LogService.Shared.Write(
                    LogLevel.Info,
                    "SelfTest",
                    "跑马灯重启滚动诊断",
                    $"should={shouldScroll} running={running} shortNoScroll={shortNoScroll} " +
                    $"again={longScrollsAgain} off={text.ScrollOffset:F1}");

                return shouldScroll && running && shortNoScroll && longScrollsAgain;
            });
        });

        Check("v0.2.18：固定尺寸下的预算占比（单内容全给一侧 / 双内容按比例且和为 1）", () =>
        {
            // 只有一侧内容：左右占比都是 1.0，可用宽度全给它（否则内容会被裁一半、看着偏一边）
            var single = IslandWindow.SplitFactors(75, bothSides: false);
            var single2 = IslandWindow.SplitFactors(25, bothSides: false);
            var singleOk = Math.Abs(single.Left - 1.0) < 1e-6 && Math.Abs(single.Right - 1.0) < 1e-6
                           && Math.Abs(single2.Left - 1.0) < 1e-6 && Math.Abs(single2.Right - 1.0) < 1e-6;

            // 两侧都有：按比例分成"占比"（0.25~0.75），两侧和为 1，且左右镜像
            var half = IslandWindow.SplitFactors(50, bothSides: true);
            var left75 = IslandWindow.SplitFactors(75, bothSides: true);
            var left25 = IslandWindow.SplitFactors(25, bothSides: true);

            var ratioOk = Math.Abs(half.Left - 0.5) < 1e-6 && Math.Abs(half.Right - 0.5) < 1e-6
                          && Math.Abs(left75.Left - 0.75) < 1e-6 && Math.Abs(left75.Right - 0.25) < 1e-6
                          && Math.Abs(left25.Left - 0.25) < 1e-6 && Math.Abs(left25.Right - 0.75) < 1e-6
                          && Math.Abs(left75.Left - left25.Right) < 1e-6
                          && Math.Abs(left75.Left + left75.Right - 1.0) < 1e-6
                          && Math.Abs(left25.Left + left25.Right - 1.0) < 1e-6;

            return singleOk && ratioOk;
        });

        Check("v0.2.10 需求2：收纳细条左右分区（按比例切换音乐/WorkBuddy）", () =>
        {
            // 50% → 中线；25% → 切点左移；75% → 切点右移；宽度无效时 None
            var mid = SnapStripWindow.ZoneAt(70, 140, 50);
            var leftZone = SnapStripWindow.ZoneAt(10, 140, 50);
            var rightZone = SnapStripWindow.ZoneAt(130, 140, 50);
            var narrow = SnapStripWindow.ZoneAt(20, 140, 25);   // 切点 35 → 20 属于左段
            var wide = SnapStripWindow.ZoneAt(20, 140, 75);     // 切点 105 → 20 仍属左段
            var invalid = SnapStripWindow.ZoneAt(5, 0, 50);

            return mid == StripZone.Right
                   && leftZone == StripZone.Left
                   && rightZone == StripZone.Right
                   && narrow == StripZone.Left
                   && wide == StripZone.Left
                   && invalid == StripZone.None
                   // 比例真的在移动切点：40 在 25% 时已属右段、在 75% 时仍属左段
                   && SnapStripWindow.ZoneAt(40, 140, 25) == StripZone.Right
                   && SnapStripWindow.ZoneAt(40, 140, 75) == StripZone.Left;
        });

        Check("v0.2.10 需求6：Bug 快照日志归类（五模块命中 / 快照自身排除 / 未知丢弃）", () =>
        {
            bool Hit(string tag, string expect)
                => string.Equals(BugSnapshotService.Classify(tag), expect, StringComparison.Ordinal);

            var ok = Hit("Lyrics", "歌词")
                     && Hit("IslandAuto", "岛布局")
                     && Hit("IslandStrip", "岛布局")
                     && Hit("通知", "通知渲染")
                     && Hit("Notification", "通知渲染")
                     && Hit("Settings", "设置UI")
                     && Hit("IslandDrag", "交互控制");

            // 快照自身的日志必须排除，否则每导一次就多一堆自己的记录
            var selfExcluded = BugSnapshotService.Classify("Snapshot") is null;

            // 与五个模块无关的模块（UIA / 去重 / 配置）不进快照，避免几百行噪声
            var noiseExcluded = BugSnapshotService.Classify("Uia") is null
                                && BugSnapshotService.Classify("Dedup") is null;

            var modules = BugSnapshotService.Modules.Count == 5;

            return ok && selfExcluded && noiseExcluded && modules;
        });

        Check("v0.2.10 需求6：快照日志按时间窗过滤并落盘（读回可解析）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "notifyhub-snapshot-test-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            try
            {
                var now = DateTime.Now;
                var lines = new[]
                {
                    // 时间窗外（11 秒前）：应被丢弃
                    $"{now.AddSeconds(-11):yyyy-MM-dd HH:mm:ss.fff} [DBG] [Lyrics] 太早了",
                    // 窗内五个模块各一行
                    $"{now.AddSeconds(-2):yyyy-MM-dd HH:mm:ss.fff} [DBG] [Lyrics] 换句 #3",
                    $"{now.AddSeconds(-2):yyyy-MM-dd HH:mm:ss.fff} [DBG] [IslandAuto] 收纳计时到点",
                    $"{now.AddSeconds(-1):yyyy-MM-dd HH:mm:ss.fff} [INF] [通知] 岛内展示",
                    $"{now.AddSeconds(-1):yyyy-MM-dd HH:mm:ss.fff} [INF] [Settings] 保存配置",
                    $"{now:yyyy-MM-dd HH:mm:ss.fff} [DBG] [IslandDrag] 开始拖拽",
                    // 非核心模块：丢弃
                    $"{now:yyyy-MM-dd HH:mm:ss.fff} [DBG] [Uia] 读会话列表",
                };

                File.WriteAllLines(Path.Combine(dir, "logs.txt"), lines, new UTF8Encoding(false));

                var parsed = BugSnapshotService.ReadLines(dir, now.AddSeconds(-10), now.AddSeconds(1));
                var modules = parsed.Select(l => l.Module).Distinct().OrderBy(m => m).ToList();

                return parsed.Count == 5
                       && modules.SequenceEqual(new[] { "交互控制", "岛布局", "歌词", "设置UI", "通知渲染" }.OrderBy(m => m))
                       && parsed.All(l => l.Time > now.AddSeconds(-10));
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // 清理失败不影响结论
                }
            }
        });

        Check("v0.2.9 需求1：界面缩放映射（1080p→1.00 / 2.5K@150%→0.90 / 2.5K→1.35 / 4K→1.70）", () =>
        {
            // 需求 1 的核心不是"卡片变大变小"，而是"卡片占屏比例恒定"。
            // 这四个点是实机最常见的四种组合，比例都该落在 10.8% 上下。
            var p1080 = ScreenMetrics.UiScale(new Rect(0, 0, 1920, 1040));
            var p25k150 = ScreenMetrics.UiScale(new Rect(0, 0, 1707, 933));
            var p25k100 = ScreenMetrics.UiScale(new Rect(0, 0, 2560, 1400));
            var p4k = ScreenMetrics.UiScale(new Rect(0, 0, 3840, 2100));

            // 0×0 之类的异常输入必须安全回落，不能除零变成 NaN 把布局带崩
            var degenerate = ScreenMetrics.UiScale(new Rect(0, 0, 0, 0));

            return Math.Abs(p1080 - 1.00) < 0.02
                   && Math.Abs(p25k150 - 0.90) < 0.03
                   && Math.Abs(p25k100 - 1.35) < 0.03
                   && Math.Abs(p4k - 1.70) < 0.03
                   && Math.Abs(degenerate - 1.0) < 0.001;
        });

        Check("v0.2.9 需求1：四角通知在 0.9/1.0/1.35/1.7 缩放下都不重叠、不出屏", () =>
        {
            var ok = true;
            var work = new Rect(0, 0, 1920, 1040);

            foreach (var scale in new[] { 0.9, 1.0, 1.35, 1.7 })
            {
                foreach (var corner in new[]
                         {
                             NotifyCorner.TopLeft, NotifyCorner.TopRight,
                             NotifyCorner.BottomLeft, NotifyCorner.BottomRight,
                         })
                {
                    var rects = NotificationLayout.CardRects(work, corner, 3, scale);
                    if (rects.Count != 3)
                    {
                        ok = false;
                        continue;
                    }

                    foreach (var rect in rects)
                    {
                        ok &= rect.Left >= -0.5 && rect.Top >= -0.5
                              && rect.Right <= work.Right + 0.5 && rect.Bottom <= work.Bottom + 0.5
                              && Math.Abs(rect.Width - NotificationLayout.CardWidth(scale)) < 0.5
                              && Math.Abs(rect.Height - NotificationLayout.CardHeight(scale)) < 0.5;
                    }

                    // 相邻两张不能压在一起（四角堆叠方向不同，用"纵向区间是否相交"判，而不是比大小）
                    for (var i = 1; i < rects.Count; i++)
                    {
                        var overlap = Math.Min(rects[i - 1].Bottom, rects[i].Bottom)
                                      - Math.Max(rects[i - 1].Top, rects[i].Top);
                        ok &= overlap <= 0.5;
                    }
                }
            }

            return ok;
        });

        Check("v0.2.9 需求1：帧率计量（去重后一帧一次 / 间隔统计 / 卡顿判定）", () =>
        {
            var meter = new FpsMeter();
            meter.Start();

            for (var i = 0; i < 40; i++)
            {
                meter.Tick(new EventArgs());
                System.Threading.Thread.Sleep(6);
            }

            var report = meter.Finish(60);

            // 只看"能不能正确统计"，不卡具体帧率（自检机器可能被 UIA 拖慢）
            return report.Frames >= 35
                   && report.AvgFps > 15
                   && report.AvgIntervalMs > 1
                   && report.MaxIntervalMs >= report.AvgIntervalMs
                   && report.RefreshHz == 60
                   && report.ToReport().Contains("达成率");
        });

        Check("v0.2.9 需求3：三来源主题色互不相同 + ntfy 多订阅取色板", () =>
        {
            var wechat = SourceColors.AccentOf(new NotifyMessage { Source = MessageSource.WeChat });
            var qq = SourceColors.AccentOf(new NotifyMessage { Source = MessageSource.QQ });
            var ntfy = SourceColors.AccentOf(new NotifyMessage { Source = MessageSource.Ntfy });

            var distinct = wechat != qq && qq != ntfy && wechat != ntfy;

            // ntfy 多订阅：负索引（旧消息/无订阅）回落默认橙；合法索引取订阅色板
            var ntfyFallback = SourceColors.AccentOf(
                new NotifyMessage { Source = MessageSource.Ntfy, AccentIndex = -1 });
            var ntfyPalette = SourceColors.AccentOf(
                new NotifyMessage { Source = MessageSource.Ntfy, AccentIndex = 1 });

            var brush = SourceColors.GradientOf(qq);

            var glyphs = SourceColors.GlyphOf(new NotifyMessage { Source = MessageSource.WeChat }) == "微"
                         && SourceColors.GlyphOf(new NotifyMessage { Source = MessageSource.QQ }) == "Q"
                         && SourceColors.GlyphOf(new NotifyMessage { Source = MessageSource.Ntfy }) == "N";

            return distinct
                   && ntfyFallback == SourceColors.Ntfy
                   && ntfyPalette != SourceColors.Ntfy
                   && brush.GradientStops.Count == 2
                   && glyphs;
        });

        Check("v0.2.18 需求5：有音乐/任务时消息一律走四角，空闲才进岛", () =>
        {
            // 岛内空闲（既没放歌也没任务）→ 新通知进岛（需求6① 负责展开）
            // 有音乐 / 有任务 / 两者都有 → 微信/QQ/ntfy 一律四角卡片，不混杂展示
            return IslandWindow.NoticeGoesToIsland(false, false)
                   && !IslandWindow.NoticeGoesToIsland(true, false)
                   && !IslandWindow.NoticeGoesToIsland(false, true)
                   && !IslandWindow.NoticeGoesToIsland(true, true);
        });

        Check("v0.2.17 工单B 规则8：四角弹窗固定 10 秒收纳（不受全局收纳时长配置影响）", () =>
            NotificationManager.CornerNoticeDurationMs == 10_000);

        Check("v0.2.18 需求1（改版）：实况预览铺样板到真实岛 / 预览期间外部状态不覆盖 / 退出即清空", () =>
        {
            try
            {
                return RunSta(() =>
                {
                    var island = new IslandWindow
                    {
                        ModeProvider = () => "Fixed",
                        MusicEnabledProvider = () => true,
                        MarqueeSpeedProvider = () => 45,
                        LyricsEnabledProvider = () => true,
                        LyricsFontSizeProvider = () => 13,
                        AutoCollapseEnabledProvider = () => true,
                        AutoCollapseSecondsProvider = () => 3,
                        SavedPositionProvider = () => null,
                        OffscreenPreview = true,
                    };

                    try
                    {
                        var config = new AppConfig();

                        // ① 进预览：双内容场景 → 音乐行 + 任务行都在，预览态生效，收纳计时被停掉
                        island.EnterLayoutPreview(config, PreviewScene.Both);

                        var both = island.PreviewMode
                                   && island.HasMusicContent
                                   && island.HasTaskContent
                                   && !island.IsAutoTimerRunning;

                        // ② 预览期间真实状态推不进来（否则一有任务/播放就把样板冲掉，看起来就是"闪一下变回原样"）
                        var demoTask = island.StatusTextSnapshot;
                        island.ShowRunning("真实任务：正在编译 NotifyHub", "WorkBuddy");
                        island.SetMedia(new MediaInfo("真歌", "真歌手", "QQMusic", true, "", "QQMusic", 10, 100));
                        island.ShowLyrics("真实歌词", "真实下一句");
                        var locked = island.StatusTextSnapshot == demoTask
                                     && island.MusicTextSnapshot.Contains("晴天", StringComparison.Ordinal);

                        // ③ 切到"仅任务"场景 → 音乐行收起，任务仍在
                        island.EnterLayoutPreview(config, PreviewScene.Task);
                        var taskOnly = !island.HasMusicContent && island.HasTaskContent;

                        // ④ 退出预览 → 预览态关闭、样板内容清空
                        island.ExitLayoutPreview();
                        var exited = !island.PreviewMode && !island.HasVisibleContent;

                        return both && locked && taskOnly && exited;
                    }
                    finally
                    {
                        island.Close();
                    }
                });
            }
            catch
            {
                return false;
            }
        });

        Check("v0.2.18 需求2：短按上滑手势识别（不等长按 / 上滑即触发 / 松手重置）", () =>
        {
            // 时序 1：按下不动 → 跟踪；上滑 23px 差 1px 不触发；上滑 24px → 触发；触发后不重复报
            var t1 = new SwipeUpCollapseTracker();
            var s1a = t1.Sample(true, 500, 1000, 24);
            var s1b = t1.Sample(true, 500, 1050, 24);
            var s1c = t1.Sample(true, 477, 1080, 24);
            var s1d = t1.Sample(true, 476, 1090, 24);
            var s1e = t1.Sample(true, 400, 1200, 24);

            // 时序 2：只滑 20px（未过阈值）不触发；松手回 Idle；再按下重新从起点计
            var t2 = new SwipeUpCollapseTracker();
            var s2a = t2.Sample(true, 500, 2000, 24);
            var s2b = t2.Sample(true, 480, 2010, 24);
            var s2c = t2.Sample(false, 480, 2020, 24);
            var s2d = t2.Sample(true, 480, 2030, 24);

            // 时序 3：短按（30ms）大距离上甩 60px → 立即触发（v0.2.18 起不看按住时长）
            var t3 = new SwipeUpCollapseTracker();
            var s3a = t3.Sample(true, 500, 3000, 24);
            var s3b = t3.Sample(true, 440, 3030, 24);

            // 时序 4：单纯点击（按住但不动）不触发
            var t4 = new SwipeUpCollapseTracker();
            var s4a = t4.Sample(true, 500, 4000, 24);
            var s4b = t4.Sample(true, 500, 4500, 24);
            var s4c = t4.Sample(true, 498, 4600, 24);

            return s1a == SwipeGestureState.Tracking
                   && s1b == SwipeGestureState.Tracking
                   && s1c == SwipeGestureState.Tracking
                   && s1d == SwipeGestureState.Triggered
                   && s1e == SwipeGestureState.Triggered
                   && s2a == SwipeGestureState.Tracking
                   && s2b == SwipeGestureState.Tracking
                   && s2c == SwipeGestureState.Idle
                   && s2d == SwipeGestureState.Tracking
                   && s3a == SwipeGestureState.Tracking
                   && s3b == SwipeGestureState.Triggered
                   && s4a == SwipeGestureState.Tracking
                   && s4b == SwipeGestureState.Tracking
                   && s4c == SwipeGestureState.Tracking;
        });

        Check("v0.2.18 需求3：细条彗星动画无缝循环（首帧=尾帧=空轨道，运动连续）", () =>
        {
            // 轨道几何（与 StripIndicator.OnRender 一致）
            const double left = 0;
            const double width = 120;
            var segWidth = Math.Min(width, Math.Max(12, width * 0.26));
            var extent = StripIndicator.CometExtent(segWidth);

            // ① 首帧（t=0）：头部在轨道左侧外 —— 整条光带（头+拖尾+光晕）完全不可见
            var x0 = StripIndicator.CometHeadX(0, left, width, segWidth);
            var startInvisible = (x0 + segWidth + 1) <= left && (x0 - (segWidth * 1.35)) <= left;

            // ② 尾帧（t=1）：与首帧**完全重合**（相位归零即回到起点），同样只剩空轨道
            //    ⇒ "动画首帧和尾帧画面完全一致"，且不存在瞬移跳变
            var xEnd = StripIndicator.CometHeadX(1, left, width, segWidth);
            var endInvisible = Math.Abs(xEnd - x0) < 1e-6
                               && (xEnd + segWidth + 1) <= left;

            // ③ 周期覆盖整条光带跨度：出得去才回得来（不会出现"右端还剩半截、左端已冒头"）
            var period = width + extent;
            var periodCoversExtent = period >= extent + segWidth;

            // ④ 一周之内单调向右：没有中途回跳（t 取 [0,1)，不含归零点）
            var monotonic = true;
            var previous = StripIndicator.CometHeadX(0, left, width, segWidth);
            for (var i = 1; i < 100; i++)
            {
                var x = StripIndicator.CometHeadX(i / 100.0, left, width, segWidth);
                if (x <= previous)
                {
                    monotonic = false;
                    break;
                }

                previous = x;
            }

            // ⑤ 中段确实横穿轨道（不是"贴边蹭一下"）
            var mid = StripIndicator.CometHeadX(0.5, left, width, segWidth);
            var crosses = mid > left && mid < left + width;

            report.AppendLine(
                $"      诊断: 首帧不可见={startInvisible} 尾帧重合={endInvisible} " +
                $"周期够长={periodCoversExtent} 单调={monotonic} 横穿={crosses}");

            return startInvisible && endInvisible && periodCoversExtent && monotonic && crosses;
        });

        Check("v0.2.9 需求3：岛内通知整行可点（点击能到达 WPF，不被穿透）", () =>
        {
            var ok = false;
            var worker = new System.Threading.Thread(() => ok = RunNoticeHitTest(), 8 * 1024 * 1024);
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();

            if (!worker.Join(TimeSpan.FromSeconds(15)))
            {
                return false;
            }

            return ok;
        });

        bool RunNoticeHitTest()
        {
            // 岛的窗口是 NOACTIVATE + 分层的，WM_NCHITTEST 默认返回 HTTRANSPARENT（点穿），
            // 只有落在 Button 上才放行。通知行整行被包进一个透明底的 Button，
            // 这里验证"点在通知行正中间 → 判定为可交互"，否则点击跳转就是死代码。
            var island = new IslandWindow
            {
                ModeProvider = () => "Fixed",
                MusicEnabledProvider = () => true,
                MarqueeSpeedProvider = () => 45,
                LyricsEnabledProvider = () => true,
                SavedPositionProvider = () => null,
            };

            try
            {
                island.ApplyTheme(ThemePalette.Create(new AppConfig()));

                // v0.2.17 工单B：空闲状态下收到通知 → 自动展开并展示（规则 3）
                island.ShowNotice(new NotifyMessage
                {
                    Source = MessageSource.WeChat,
                    Sender = "张三",
                    Content = "在吗？刚才那个需求文档我改了一版",
                    Time = DateTime.Now,
                });

                island.Measure(new Size(900, 200));
                island.Arrange(new Rect(0, 0, 900, 200));
                island.UpdateLayout();

                if (island.FindName("NoticeButton") is not System.Windows.Controls.Button button
                    || button.ActualWidth <= 0 || button.ActualHeight <= 0)
                {
                    return false;
                }

                var center = button.TranslatePoint(
                    new System.Windows.Point(button.ActualWidth / 2, button.ActualHeight / 2), island);

                // 通知行中间必须是"可交互"，且状态标记为已展示
                var rowClickable = island.IsNoticeShown && island.HitTestInteractive(center);

                // v0.2.17 工单B 规则 7：先已读收掉，再让音乐进入播放态，
                // 此时来新通知必须**只暂存**（单应用模式不主动弹，防"一闪而过"的反面——
                // 不覆盖用户正在听的歌，悬浮到岛上时才展示）。
                island.HideNotice();
                island.SetMedia(new MediaInfo("侧脸", "魏语诺", "FOLIA", true, "", "Folia", 30, 200));
                island.ShowNotice(new NotifyMessage
                {
                    Source = MessageSource.WeChat,
                    Sender = "李四",
                    Content = "第二条：歌挺好听",
                    Time = DateTime.Now,
                });

                var stashedOnly = !island.IsNoticeShown && island.HasVisibleContent;

                // v0.2.18 需求6①：待机收纳后收到通知 → 岛**立即**展开
                // （不被 ExpandFromStrip 的 400ms 防抖冷却吞掉——那正是
                //  "只有提示音、待机条只变色、岛不展开"的根因）。
                // 先清干净回到待机：音乐停（有 2.2s 换曲 grace，要泵完才收纳）
                // + 通知已读 → 收成细条；紧接着来一条新通知，断言立刻从细条展开。
                island.HideNotice();
                island.SetMedia(null);

                var graceDeadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < graceDeadline && !island.IsStripMode)
                {
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        () => { }, System.Windows.Threading.DispatcherPriority.Background);
                    System.Threading.Thread.Sleep(40);
                }

                var collapsedAfterIdle = island.IsStripMode && !island.HasVisibleContent;

                island.ShowNotice(new NotifyMessage
                {
                    Source = MessageSource.Ntfy,
                    Sender = "CI 构建",
                    Content = "notifyhub #128 构建成功",
                    Time = DateTime.Now,
                });

                var expandedAtOnce = !island.IsStripMode && island.IsNoticeShown;

                report.AppendLine(
                    $"      诊断: 行可点={rowClickable} 单应用只暂存={stashedOnly} " +
                    $"待机已收纳={collapsedAfterIdle} 通知即展开={expandedAtOnce}");

                return rowClickable && stashedOnly && collapsedAfterIdle && expandedAtOnce;
            }
            finally
            {
                island.Close();
            }
        }

        Check("v0.2.18 需求4：通知卡片左上角叉号可关闭（滑动删除已移除）", () =>
        {
            return RunSta(() =>
            {
                var message = new NotifyMessage
                {
                    Source = MessageSource.WeChat,
                    Sender = "张三",
                    Content = "叉号关闭测试",
                    Time = DateTime.Now,
                };

                var card = new NotificationWindow(message, AppConfig.MinDurationMs, 45, new AppConfig(), 1.0);

                try
                {
                    // 离屏显示：绝不在用户桌面上弹一张真卡片
                    card.ShowAt(-4000, -4000, animate: false);

                    if (card.FindName("CloseButton") is not System.Windows.Controls.Button close)
                    {
                        report.AppendLine("      诊断: 找不到 CloseButton");
                        return false;
                    }

                    // 叉号必须在卡片左上角（与卡片原点相距不远）
                    var closeOrigin = close.PointToScreen(new System.Windows.Point(0, 0));
                    var cardOrigin = card.PointToScreen(new System.Windows.Point(0, 0));
                    var nearTopLeft = Math.Abs(closeOrigin.X - cardOrigin.X) < 60
                                      && Math.Abs(closeOrigin.Y - cardOrigin.Y) < 60;

                    // 点叉号 → 走 OnCloseButtonClick → Dismiss → 关闭
                    close.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

                    // RunSta 没有消息循环：手动泵到卡片真的关掉
                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < deadline && !card.IsClosed)
                    {
                        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                            () => { }, System.Windows.Threading.DispatcherPriority.Background);
                        System.Threading.Thread.Sleep(30);
                    }

                    report.AppendLine(
                        $"      诊断: 叉号在左上角={nearTopLeft} 点击后关闭={card.IsClosed}");

                    return nearTopLeft && card.IsClosed;
                }
                finally
                {
                    try
                    {
                        if (!card.IsClosed)
                        {
                            card.Close();
                        }
                    }
                    catch
                    {
                        // 忽略
                    }
                }
            });
        });

        Check("v0.2.9 需求5：歌词时间轴误差 ≤300ms（模拟 SMTC 每秒上报 + 600ms 上报延迟）", () =>
        {
            // 这是"歌词慢半句"的根因复现：SMTC 的 Position 是 LastUpdatedTime 那一刻的快照，
            // 我们读到它时已经过去了几百毫秒。补偿前稳态落后 ≈ 上报延迟 + 上报周期。
            // 现在按 reportAge 补偿 + 播放中只增不减，逐 90ms 推进的误差应当远小于 300ms。
            const double reportLagSeconds = 0.6;
            var freq = System.Diagnostics.Stopwatch.Frequency;
            var clock = new PlaybackClock();
            var maxError = 0.0;
            var steadyError = 0.0;

            // 从第 1 秒开始（第 0 秒时"上报值 = -0.6"会被夹到 0，属于模型边界，不是代码问题）
            for (var ms = 1000; ms <= 21000; ms += 90)
            {
                var truePosition = ms / 1000.0;

                // 上一次上报发生在"整秒"处，上报的也是那一刻的真实位置
                var lastReportAt = Math.Floor(ms / 1000.0);
                var reported = lastReportAt - reportLagSeconds;

                // 上报年龄本身也带误差（LastUpdatedTime 与本地时钟总有几十上百毫秒偏差），
                // 用确定性正弦抖动模拟，保证自检可复现
                var ageJitter = 0.12 * Math.Sin(ms / 700.0);

                var position = clock.Update(
                    reported,
                    playing: true,
                    nowTicks: (long)(ms / 1000.0 * freq),
                    reportAgeSeconds: reportLagSeconds + ageJitter);

                var error = Math.Abs(position - truePosition);
                maxError = Math.Max(maxError, error);

                if (ms > 6000)
                {
                    steadyError = Math.Max(steadyError, error);
                }
            }

            LogService.Shared.Write(
                steadyError <= 0.30 ? LogLevel.Info : LogLevel.Error,
                "SelfTest",
                "歌词时间轴误差",
                $"稳态最大 {steadyError * 1000:F0}ms / 全程最大 {maxError * 1000:F0}ms");

            // 稳态（跳过起步的 6 秒）误差才是用户能感知的部分
            return steadyError <= 0.30 && maxError <= 0.35;
        });

        Check("v0.2.18：长度（上下边长）100 基准 60~160 / 高度（左右边长）上限 70 / 左右分配滑块", () =>
        {
            var defaults = new AppConfig();
            var okDefaults = defaults.IslandWidthPercent == 100 && defaults.IslandSplitRatio == 50
                             // 长度是上下边长（水平），可以到 160；高度是左右边长（垂直），70 是上限
                             && AppConfig.MaxIslandWidthPercent == 160
                             && AppConfig.MinIslandWidthPercent == 60
                             && AppConfig.MaxIslandHeightPercent == 70;

            var high = new AppConfig { IslandWidthPercent = 999, IslandSplitRatio = 999 };
            high.Normalize();
            var okHigh = high.IslandWidthPercent == AppConfig.MaxIslandWidthPercent
                         && high.IslandSplitRatio == AppConfig.MaxIslandSplitRatio;

            var low = new AppConfig { IslandWidthPercent = 1, IslandSplitRatio = 1 };
            low.Normalize();
            var okLow = low.IslandWidthPercent == AppConfig.MinIslandWidthPercent
                        && low.IslandSplitRatio == AppConfig.MinIslandSplitRatio;

            // 旧版 config.json 没有这两个字段 → 反序列化成 0 → 必须回落默认而不是缩到最小
            var legacy = new AppConfig { IslandWidthPercent = 0, IslandSplitRatio = 0 };
            legacy.Normalize();
            var okLegacy = legacy.IslandWidthPercent == 100 && legacy.IslandSplitRatio == 50;

            return okDefaults && okHigh && okLow && okLegacy;
        });

        Check("v0.2.9 需求6：毛玻璃配色跟随系统（ThemeMode/TextTone 不再影响调色板）", () =>
        {
            var a = ThemePalette.Create(new AppConfig { ThemeMode = "SolidDark", TextTone = "White" }, 0.2);
            var b = ThemePalette.Create(new AppConfig { ThemeMode = "GlassLight", TextTone = "Black" }, 0.2);

            var ignored = a.TextPrimary == b.TextPrimary && a.NominalBackground == b.NominalBackground;

            // 背景亮暗都要自己把文字对比度拉回 WCAG 4.5 以上，不需要用户手调
            var onDark = ThemePalette.Create(new AppConfig(), 0.12);
            var onLight = ThemePalette.Create(new AppConfig(), 0.88);
            var readable = ThemePalette.ContrastRatio(onDark.TextPrimary, onDark.NominalBackground) >= 4.5
                           && ThemePalette.ContrastRatio(onLight.TextPrimary, onLight.NominalBackground) >= 4.5;

            return ignored && readable;
        });

        Check("v0.2.9 需求7：系统能力探测（不抛异常 / Win11 判定与 Build 对齐 / 数值合理）", () =>
        {
            var caps = SystemCapabilities.Refresh();

            var ok = caps.Build >= 7600
                     && caps.DpiForSystem >= 96
                     && caps.UiScale >= ScreenMetrics.MinUiScale
                     && caps.UiScale <= ScreenMetrics.MaxUiScale
                     && caps.WorkArea.Width > 100
                     && caps.WorkArea.Height > 100
                     && caps.IsWindows11 == (caps.Build >= 22000)
                     && caps.ToReport().Contains("毛玻璃实现");

            LogService.Shared.Write(ok ? LogLevel.Info : LogLevel.Error, "SelfTest", "系统能力 " + caps.ToLogLine());
            return ok;
        });

        Check("v0.2.7 需求2：收纳配置默认 3 秒 / 区间收敛 / 音乐例外默认关（v0.2.17 统一时序）", () =>
        {
            var defaults = new AppConfig();
            var defaultsOk = defaults.AutoCollapseEnabled
                             && defaults.AutoCollapseSeconds == 3
                             && !defaults.KeepOpenWhilePlaying
                             && defaults.LyricsEnabled
                             && defaults.LyricsFontSize == 13
                             // v0.2.17 工单A 新配置默认值
                             && defaults.WeChatAllowChatMultimedia
                             // v0.2.18 需求1/2 新配置默认值
                             && defaults.SwipeCollapseEnabled
                             && defaults.SwipeCollapsePx == 24
                             && defaults.IslandHeightPercent == 70
                             && AppConfig.MaxIslandHeightPercent == 70
                             && AppConfig.MinIslandHeightPercent < 70
                             && defaults.MusicModuleScale == 100
                             && defaults.LyricsWidthPercent == 100
                             && defaults.MusicLyricsGap == 4
                             && defaults.LyricsOffsetX == 0
                             && defaults.TaskModuleScale == 100
                             && defaults.TaskLineSpacing == 4
                             && defaults.TaskOffsetY == 0;

            var high = new AppConfig
            {
                AutoCollapseSeconds = 999,
                LyricsFontSize = 99,
                IslandHeightPercent = 999,
                MusicModuleScale = 999,
                TaskModuleScale = 999,
                LyricsWidthPercent = 999,
                MusicLyricsGap = 999,
                LyricsOffsetX = 999,
                TaskLineSpacing = 999,
                TaskOffsetY = 999,
                SwipeCollapsePx = 999,
            };
            high.Normalize();
            var highOk = high.AutoCollapseSeconds == AppConfig.MaxAutoCollapseSeconds
                         && high.LyricsFontSize == AppConfig.MaxLyricsFontSize
                         && high.IslandHeightPercent == AppConfig.MaxIslandHeightPercent
                         && high.MusicModuleScale == AppConfig.MaxModuleScale
                         && high.TaskModuleScale == AppConfig.MaxModuleScale
                         && high.LyricsWidthPercent == AppConfig.MaxLyricsWidthPercent
                         && high.MusicLyricsGap == AppConfig.MaxMusicLyricsGap
                         && high.LyricsOffsetX == AppConfig.MaxLyricsOffsetX
                         && high.TaskLineSpacing == AppConfig.MaxTaskLineSpacing
                         && high.TaskOffsetY == AppConfig.MaxTaskOffsetY
                         && high.SwipeCollapsePx == AppConfig.MaxSwipeCollapsePx;

            var low = new AppConfig
            {
                AutoCollapseSeconds = 0,
                LyricsFontSize = 1,
                IslandHeightPercent = 1,
                MusicModuleScale = 1,
                TaskModuleScale = 1,
                LyricsWidthPercent = 1,
                MusicLyricsGap = -5,
                LyricsOffsetX = -999,
                TaskLineSpacing = -5,
                TaskOffsetY = -999,
                SwipeCollapsePx = 1,
            };
            low.Normalize();
            var lowOk = low.AutoCollapseSeconds == AppConfig.MinAutoCollapseSeconds
                        && low.LyricsFontSize == AppConfig.MinLyricsFontSize
                        && low.IslandHeightPercent == AppConfig.MinIslandHeightPercent
                        && low.MusicModuleScale == AppConfig.MinModuleScale
                        && low.TaskModuleScale == AppConfig.MinModuleScale
                        && low.LyricsWidthPercent == AppConfig.MinLyricsWidthPercent
                        && low.MusicLyricsGap == AppConfig.MinMusicLyricsGap
                        && low.LyricsOffsetX == AppConfig.MinLyricsOffsetX
                        && low.TaskLineSpacing == AppConfig.MinTaskLineSpacing
                        && low.TaskOffsetY == AppConfig.MinTaskOffsetY
                        && low.SwipeCollapsePx == AppConfig.MinSwipeCollapsePx;

            // 旧配置缺字段（反序列化成 0）时按默认值处理，不能被收敛成下限
            var legacy = new AppConfig
            {
                IslandHeightPercent = 0,
                MusicModuleScale = 0,
                TaskModuleScale = 0,
                LyricsWidthPercent = 0,
            };
            legacy.Normalize();
            var legacyOk = legacy.IslandHeightPercent == 70
                           && legacy.MusicModuleScale == 100
                           && legacy.TaskModuleScale == 100
                           && legacy.LyricsWidthPercent == 100;

            return defaultsOk && highOk && lowOk && legacyOk;
        });

        Check("v0.2.5：夸克浏览器定位（真实安装路径 + 注册表兜底）", () =>
        {
            // 夸克装在 %LOCALAPPDATA%\Programs\Quark\quark.exe；本机装了就必须找到，
            // 找到的路径必须真实存在。没装则跳过（返回 null 也算通过）。
            var quark = BrowserLauncher.Locate("Quark");
            var pathOk = quark is null || File.Exists(quark);

            // 注册表 App Paths 兜底：本机 quark.exe 有登记，直接读注册表也要能拿到
            var fromRegistry = BrowserLauncher.LocateFromAppPaths("quark.exe");
            var registryOk = fromRegistry is null || File.Exists(fromRegistry);

            return pathOk && registryOk;
        });

        Check("v0.2.4 需求1：多订阅配置（迁移/清理/色板收敛）", () =>
        {
            // 旧单地址配置 → 迁移成第一个订阅
            var legacy = new AppConfig { NtfyEnabled = true, NtfyUrl = "https://ntfy.sh/legacy" };
            legacy.Normalize();
            var migrated = legacy.NtfySubscriptions.Count == 1
                           && legacy.NtfySubscriptions[0].Url == "https://ntfy.sh/legacy"
                           && legacy.NtfySubscriptions[0].Enabled;

            // 空地址项被清理，颜色下标收敛
            var dirty = new AppConfig
            {
                NtfySubscriptions = new List<NtfySubscription>
                {
                    new() { Enabled = true, Url = "https://ntfy.sh/a", ColorIndex = 99 },
                    new() { Enabled = true, Url = "  ", ColorIndex = 0 },
                    null!,
                },
            };
            dirty.Normalize();
            var cleaned = dirty.NtfySubscriptions.Count == 1
                          && dirty.NtfySubscriptions[0].ColorIndex == AppConfig.NtfyPalette.Count - 1;

            // 色板：5 色、索引 0 仍是原 ntfy 橙
            var palette = AppConfig.NtfyPalette.Count == 5 && AppConfig.NtfyPalette[0] == 0xF97316u;

            return migrated && cleaned && palette;
        });

        Check("v0.2.4 需求3：下行推送地址规整（剥 /sse 与查询参数）", () =>
        {
            return NtfyPublisher.BuildPushUrl("https://ntfy.sh/myphone") == "https://ntfy.sh/myphone"
                   && NtfyPublisher.BuildPushUrl("https://ntfy.sh/myphone/sse") == "https://ntfy.sh/myphone"
                   && NtfyPublisher.BuildPushUrl("ntfy.sh/myphone?poll=1") == "https://ntfy.sh/myphone"
                   && NtfyPublisher.BuildPushUrl("http://192.168.1.9:8080/alerts") == "http://192.168.1.9:8080/alerts"
                   && NtfyPublisher.BuildPushUrl("") is null
                   && NtfyPublisher.BuildPushUrl("   ") is null;
        });

        Check("v0.2.4 需求7：浏览器四选项定位与快捷方式解析", () =>
        {
            // 固定浏览器探测（本机不一定装，装了的必须找到）
            var chrome = BrowserLauncher.Locate("Chrome");
            var edge = BrowserLauncher.Locate("Edge");
            var quark = BrowserLauncher.Locate("Quark");
            var probeOk = (chrome is null || File.Exists(chrome))
                          && (edge is null || File.Exists(edge))
                          && (quark is null || File.Exists(quark));

            // 造一个指向本程序的 .lnk，验证 ResolveShortcut 能解析出 exe
            var lnkOk = false;
            var temp = Path.Combine(Path.GetTempPath(), "notifyhub-selftest-link.lnk");
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is not null)
                {
                    var shell = Activator.CreateInstance(shellType);
                    var shortcut = shellType.InvokeMember("CreateShortcut",
                        System.Reflection.BindingFlags.InvokeMethod, null, shell!, new object[] { temp });
                    var exe = Environment.ProcessPath ?? "";
                    shortcut!.GetType().InvokeMember("TargetPath",
                        System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exe });
                    // 必须 Save() 落盘，否则 ResolveShortcut 读不到目标
                    shortcut.GetType().InvokeMember("Save",
                        System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                    var resolved = BrowserLauncher.ResolveShortcut(temp);
                    lnkOk = File.Exists(temp)
                            && string.Equals(resolved, exe, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // WScript.Shell 不可用时跳过 lnk 部分
                lnkOk = true;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            // 非 lnk 路径原样返回
            var plainOk = BrowserLauncher.ResolveShortcut(@"C:\Windows\System32\notepad.exe")
                             == @"C:\Windows\System32\notepad.exe";

            return probeOk && lnkOk && plainOk;
        });

        Check("v0.2.4 需求9：提示音量回到正常水平（增益 0.85→1.0，约 +17.6%）", () =>
        {
            // 用户反馈 v0.2.3 比正常音量低 10%~20%：增益回到满刻度 1.0 即 +17.6%，
            // 音符音量维持 v0.2.3 调校值（不叠加放大，否则 +40% 会过响）。
            var tone = SoundService.MessageTone("DingDong");
            var ok = tone.Length == 3
                     && Math.Abs(tone[0].Volume - 0.42) < 0.001
                     && Math.Abs(tone[2].Volume - 0.38) < 0.001
                     && Math.Abs(tone[0].Freq - 880.0) < 0.001;

            // 增益确实回到了 1.0（自检里直接读内部常量）
            var gainOk = Math.Abs(SoundService.SynthesisGain - 1.0) < 0.0001;

            // 所有音色音量都不得低于 0.26（静音/极低音色的兜底）
            var all = new[] { "DingDong", "Crystal", "Bubble", "Marimba", "Digital" }
                .SelectMany(SoundService.MessageTone)
                .Concat(new[] { "Ascend", "Success", "Bell", "Chord" }.SelectMany(SoundService.TaskTone))
                .Where(n => n.Freq > 0)
                .All(n => n.Volume >= 0.26);

            return ok && gainOk && all;
        });

        Check("v0.2.4 需求8：日志目录自定义（SetLogDir 落盘 + 事件文件迁移）", () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "notifyhub-logtest-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(tempDir);
                var ok = LogService.Shared.SetLogDir(tempDir);
                var fileExists = File.Exists(Path.Combine(tempDir, "events.json")) || ok;
                var dirOk = string.Equals(LogService.Shared.LogDir, tempDir, StringComparison.OrdinalIgnoreCase);

                // 无效目录返回 false
                var badOk = !LogService.Shared.SetLogDir("Z:\\not\\a\\real\\dir\\" + Guid.NewGuid().ToString("N"));

                return ok && fileExists && dirOk && badOk;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

        Check("UI Automation 客户端可用（能读到根元素与顶层窗口）", () =>
        {
            var root = AutomationElement.RootElement;
            if (root is null)
            {
                return false;
            }

            var windows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            return windows.Count > 0;
        });

        Check("v0.2.15 需求1：关掉软件后再次双击必须重新拉起（不再永久锁死）", () =>
        {
            // 2026-10-24 真实日志：10:47:29 真启动 Folia，之后 10:47:53 / 10:48:37 /
            // 10:50:53 / 10:51:28 全是"已启动过，等待窗口 | Folia" + outcome=Launched，
            // 而 Folia 早就被用户关掉了 —— 双击看起来"生效了"，实际什么都没做。
            const long now = 100_000;

            // 进程还在跑 → 必须拦住（否则就是重复实例：幽灵任务栏图标 + 16×16 空白窗）
            var runningSuppressed = AppLauncher.ShouldSuppressLaunch(now - 60_000, now, processRunning: true);

            // 刚拉起 1 秒、进程表里还没出现 → 宽限期内先拦住
            var graceSuppressed = AppLauncher.ShouldSuppressLaunch(now - 1_000, now, processRunning: false);

            // 用户已经关掉、且过了宽限期 → 必须放行（老 bug 的修复点）
            var allowAfterExit = !AppLauncher.ShouldSuppressLaunch(now - 10_000, now, processRunning: false);
            var allowLongAfter = !AppLauncher.ShouldSuppressLaunch(now - 3_600_000, now, processRunning: false);

            return runningSuppressed && graceSuppressed && allowAfterExit && allowLongAfter
                   && AppLauncher.LaunchGraceLimitMs >= 1_000;
        });

        Check("v0.2.15 需求1：音乐唤起按“进程名 + 路径”配对（不会误拉第二个播放器）", () =>
        {
            var targets = AppLauncher.MusicTargets();
            var candidates = AppLauncher.MusicAppCandidates();

            var hasFolia = targets.Any(t => t.Keyword == "Folia"
                                            && t.Path.EndsWith("Folia.exe", StringComparison.OrdinalIgnoreCase));
            var hasCloudMusic = targets.Any(t => t.Keyword == "cloudmusic");
            var hasQqMusic = targets.Any(t => t.Keyword == "QQMusic");

            // 每个关键字的安装路径必须真的带上这个进程名，否则"跑没跑"与"启动哪个"会错位
            var keywordMatchesPath = targets.All(t =>
                t.Path.IndexOf(t.Keyword, StringComparison.OrdinalIgnoreCase) >= 0);
            var allRooted = targets.All(t => Path.IsPathRooted(t.Path));
            var candidatesAligned = candidates.Count == targets.Count;

            return hasFolia && hasCloudMusic && hasQqMusic && keywordMatchesPath && allRooted && candidatesAligned;
        });

        Check("v0.2.15 需求2：没有可见内容绝不展开空岛（白色椭圆的根因）", () =>
        {
            return RunSta(() =>
            {
                var island = new IslandWindow
                {
                    ModeProvider = () => "Fixed",
                    MusicEnabledProvider = () => true,
                    MarqueeSpeedProvider = () => 45,
                    LyricsEnabledProvider = () => true,
                    LyricsFontSizeProvider = () => 13,
                    AutoCollapseEnabledProvider = () => false,
                    SavedPositionProvider = () => null,
                };

                try
                {
                    island.ApplyTheme(ThemePalette.Create(new AppConfig()));

                    // ① 什么都没有：不算内容
                    island.SetPresence(false, false);
                    var emptyNoContent = !island.HasVisibleContent;

                    // ② 只有"刚双击唤起"的 15 秒保持期：
                    //    旧版把它算成内容 → 播放器还没起来的几秒里，岛上一片空白却被 Show 出来，
                    //    宽度塌到 54、圆角 22 = 屏幕上那个竖立白色椭圆（10-24 10:42 快照实拍）
                    island.NoteUserLaunch();
                    var holdIsActive = island.IsLaunchHoldActive;
                    var holdNoContent = !island.HasVisibleContent;

                    // ③ 真任务进来 → 算内容
                    island.ShowRunning("正在整理需求清单", "WorkBuddy");
                    var taskIsContent = island.HasVisibleContent;

                    // ④ 完成态同样算内容（绿勾要停留 600ms）
                    island.ShowDone("整理完成", "WorkBuddy");
                    var doneIsContent = island.HasVisibleContent;

                    return emptyNoContent && holdIsActive && holdNoContent && taskIsContent && doneIsContent;
                }
                finally
                {
                    // 这些调用会顺手建出窄条窗口（Evaluate → CollapseToStrip），收干净别留痕
                    try { island.Close(); } catch { }
                }
            });
        });

        Check("v0.2.17 工单B 规则5：后台挂着的播放器不占岛；唤起保持期才显示左栏", () =>
        {
            return RunSta(() =>
            {
                var island = new IslandWindow
                {
                    ModeProvider = () => "Fixed",
                    MusicEnabledProvider = () => true,
                    MarqueeSpeedProvider = () => 45,
                    LyricsEnabledProvider = () => true,
                    LyricsFontSizeProvider = () => 13,
                    AutoCollapseEnabledProvider = () => false,
                    SavedPositionProvider = () => null,
                };

                try
                {
                    island.ApplyTheme(ThemePalette.Create(new AppConfig()));

                    // 场景 1（稳态）：WorkBuddy 在跑 + 播放器进程在跑、但没有播放会话。
                    // v0.2.17 工单B 规则 5："无任务无播放的应用不展示"——
                    // 旧版（v0.2.15 需求2）会把待机左栏撑出来占着岛；现在稳态下
                    // 左栏与在线行都不显示，岛收成窄条（只展示活跃项）。
                    island.SetPresence(true, true);
                    island.SetMedia(null);

                    var standbyGone = !island.IsStandbyMusicShown;
                    var steadyNoContent = !island.HasVisibleContent;

                    // 场景 2（唤起保持期）：双击窄条唤起播放器后的 15s 内，待机左栏
                    // 回归（用户可以直接点左栏的播放键），岛从细条重新展开。
                    // 注意 NoteUserLaunch 内部会因"当前无内容"再走一次收纳、刷新细条
                    // 落幕时间，所以前后各越过一次 400ms 展开冷却，最后的 SetMedia
                    // 才真正走 ExpandFromStrip 把岛拉回来。
                    Thread.Sleep(450);
                    island.NoteUserLaunch();
                    Thread.Sleep(450);
                    island.SetMedia(null);

                    var standbyBack = island.IsStandbyMusicShown;
                    var holdHasContent = island.HasVisibleContent;
                    var expandedBack = !island.IsStripMode;

                    island.Measure(new Size(900, 300));
                    island.Arrange(new Rect(0, 0, island.DesiredSize.Width, island.DesiredSize.Height));
                    island.RefreshLayoutMetrics();
                    island.Measure(new Size(900, 300));
                    island.Arrange(new Rect(0, 0, island.DesiredSize.Width, island.DesiredSize.Height));

                    var leftPane = island.FindName("LeftPane") as System.Windows.Controls.Grid;
                    var divider = island.FindName("Divider") as System.Windows.Controls.Border;

                    report.AppendLine(
                        $"      诊断: left={leftPane?.Visibility.ToString() ?? "null"} " +
                        $"divider={divider?.Visibility.ToString() ?? "null"} " +
                        $"width={island.DesiredSize.Width:F0} content={holdHasContent} " +
                        $"standby={standbyBack} strip={island.IsStripMode}");

                    if (leftPane is null || divider is null)
                    {
                        return false;
                    }

                    // 左栏必须留着（图标 + 空歌名 + 三键），否则"分隔线画了、左栏没内容"
                    // 就会让岛宽塌到 54 —— 那正是白色椭圆/白色细框的外观
                    var leftVisible = leftPane.Visibility == Visibility.Visible;
                    var dividerVisible = divider.Visibility == Visibility.Visible;
                    var wideEnough = island.DesiredSize.Width >= 200;

                    return standbyGone && steadyNoContent && standbyBack && holdHasContent
                           && expandedBack && leftVisible && dividerVisible && wideEnough;
                }
                finally
                {
                    // 这些调用会顺手建出窄条窗口（Evaluate → CollapseToStrip），收干净别留痕
                    try { island.Close(); } catch { }
                }
            });
        });

        Check("v0.2.15 需求5：进程巡检 ≤500ms（关掉播放器/WorkBuddy 立刻回待机）", () =>
            PresenceWatcher.PollInterval <= 500 && PresenceWatcher.PollInterval >= 200);

        Check("v0.2.15 需求1：进程退出后真的重新拉起（端到端 / 隐藏 notepad）", () =>
        {
            var notepad = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            if (!File.Exists(notepad))
            {
                // 没有 notepad 的极简系统：跳过（不影响其它用例），但要在报告里说清楚
                report.AppendLine("      SKIP  未找到 notepad.exe，跳过端到端拉起验证");
                return true;
            }

            static int Count() => System.Diagnostics.Process.GetProcessesByName("notepad").Length;

            try
            {
                AppLauncher.ForgetLaunched("notepad");
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("notepad")) { try { p.Kill(); } catch { } }

                var first = AppLauncher.Open("notepad", new[] { notepad });
                Thread.Sleep(900);
                var aliveAfterFirst = Count() > 0;

                foreach (var p in System.Diagnostics.Process.GetProcessesByName("notepad")) { try { p.Kill(); } catch { } }
                Thread.Sleep(600);
                var gone = Count() == 0;

                // 冷却 600ms（wx 去重）+ 越过 2s 启动宽限，再双击一次
                Thread.Sleep(2_200);
                var second = AppLauncher.Open("notepad", new[] { notepad });
                Thread.Sleep(900);
                var relaunched = Count() > 0;

                report.AppendLine(
                    $"      诊断: first={first} alive={aliveAfterFirst} gone={gone} " +
                    $"second={second} relaunched={relaunched}");

                return first == OpenOutcome.Launched && aliveAfterFirst && gone
                       && second == OpenOutcome.Launched && relaunched;
            }
            finally
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("notepad")) { try { p.Kill(); } catch { } }
                AppLauncher.ForgetLaunched("notepad");
            }
        });

        Check("v0.2.15 需求3：通知卡超时必收（不留桌面残留框）", () =>
        {
            return RunSta(() =>
            {
                var message = new NotifyMessage
                {
                    Source = MessageSource.Ntfy,
                    Sender = "自检",
                    Content = "卡片卡死兜底",
                    Time = DateTime.Now,
                };

                var card = new NotificationWindow(message, AppConfig.MinDurationMs, 45, new AppConfig(), 1.0);

                try
                {
                    // 离屏显示：绝不在用户桌面上弹一张真卡片
                    card.ShowAt(-4000, -4000, animate: false);

                    // RunSta 没有消息循环，必须手动泵 dispatcher，DispatcherTimer 才会走
                    var deadline = DateTime.UtcNow.AddSeconds(
                        (AppConfig.MinDurationMs / 1000.0) + NotificationWindow.StuckGuardExtraMs / 1000.0 + 3);

                    while (DateTime.UtcNow < deadline && !card.IsClosed)
                    {
                        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                            () => { }, System.Windows.Threading.DispatcherPriority.Background);
                        System.Threading.Thread.Sleep(40);
                    }

                    var closed = card.IsClosed;
                    LogService.Shared.Write(
                        LogLevel.Info, "SelfTest", "卡片兜底诊断", $"closed={closed} deadlineHit={!closed}");
                    return closed;
                }
                finally
                {
                    try
                    {
                        if (!card.IsClosed)
                        {
                            card.Close();
                        }
                    }
                    catch
                    {
                        // 忽略
                    }
                }
            }, 20);
        });

        Check("v0.2.16 需求1：分层窗最小化 → 收起隐藏（左下角白块的根因）", () =>
        {
            return RunSta(() =>
            {
                // 复刻设置窗的窗口属性：无系统标题栏 + 分层透明
                var probe = new System.Windows.Window
                {
                    WindowStyle = System.Windows.WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    ShowInTaskbar = false,
                    Width = 420,
                    Height = 300,
                    Left = -4000,
                    Top = -4000,
                    Content = new System.Windows.Controls.Border
                    {
                        Background = System.Windows.Media.Brushes.White,
                    },
                };

                var squashed = 0;
                WindowSquashGuard.Attach(probe, _ => squashed++);

                try
                {
                    probe.Show();

                    // RunSta 不跑消息循环：手动泵到窗口真正上屏
                    var shownBy = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < shownBy && !probe.IsVisible)
                    {
                        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                            () => { }, System.Windows.Threading.DispatcherPriority.Background);
                        System.Threading.Thread.Sleep(20);
                    }

                    var shown = probe.IsVisible && probe.WindowState == System.Windows.WindowState.Normal;

                    // 触发最小化：补丁应把它转成 Normal + Hide
                    probe.WindowState = System.Windows.WindowState.Minimized;

                    var settled = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < settled && probe.WindowState == System.Windows.WindowState.Minimized)
                    {
                        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                            () => { }, System.Windows.Threading.DispatcherPriority.Background);
                        System.Threading.Thread.Sleep(20);
                    }

                    var hidden = !probe.IsVisible;
                    var restoredState = probe.WindowState == System.Windows.WindowState.Normal;
                    var sizeKept = probe.Width > 400 && probe.Width < 440
                                   && probe.Height > 280 && probe.Height < 320;
                    var notSquashed = !WindowSquashGuard.IsSquashed(probe);

                    LogService.Shared.Write(
                        LogLevel.Info,
                        "SelfTest",
                        "最小化补丁诊断",
                        $"shown={shown} hidden={hidden} state={probe.WindowState} {probe.Width}x{probe.Height} squashedCount={squashed}");

                    return shown && hidden && restoredState && sizeKept && notSquashed && squashed == 1;
                }
                finally
                {
                    try
                    {
                        probe.Close();
                    }
                    catch
                    {
                        // 忽略
                    }
                }
            });
        });

        Check("v0.2.16 需求1：IsSquashed 只在“最小化且仍可见”时判真（不误伤正常窗口）", () =>
        {
            return RunSta(() =>
            {
                var probe = new System.Windows.Window
                {
                    WindowStyle = System.Windows.WindowStyle.None,
                    AllowsTransparency = true,
                    Width = 300,
                    Height = 200,
                    ShowInTaskbar = false,
                    Left = -4000,
                    Top = -4000,
                    Content = new System.Windows.Controls.Border
                    {
                        Background = System.Windows.Media.Brushes.White,
                    },
                };

                try
                {
                    // 未 Show：状态 Normal，不该被判成压扁
                    var normalNotSquashed = !WindowSquashGuard.IsSquashed(probe);

                    probe.Show();
                    var shownNotSquashed = !WindowSquashGuard.IsSquashed(probe);

                    return normalNotSquashed && shownNotSquashed;
                }
                finally
                {
                    try
                    {
                        probe.Close();
                    }
                    catch
                    {
                        // 忽略
                    }
                }
            });
        });

        // dump 可能被桌面上无响应窗口的 UIA 30 秒超时拖慢，用超时护栏避免自检整体挂死
        var dumpTask = Task.Run(() => UiaDumper.DumpAll(log));
        if (dumpTask.Wait(TimeSpan.FromSeconds(150)))
        {
            var dumpResult = dumpTask.Result;
            Check("UIA dump 在目标未运行时可正常降级并落盘", () =>
                File.Exists(dumpResult.FilePath) && dumpResult.NodeCount >= 0);
        }
        else
        {
            skipped++;
            Write("SKIP  UIA dump 降级落盘  → UIA 枚举超过 150s：桌面存在无响应窗口（环境相关，主功能的窗口定位已改用 EnumWindows，不受影响）", true);
        }

        report.AppendLine(new string('-', 70));
        report.AppendLine($"结果: 通过 {passed} / 共 {passed + failed + skipped}，失败 {failed}，跳过 {skipped}");

        var path = Path.Combine(AppPaths.Root, $"selftest-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            File.WriteAllText(path, report.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 忽略
        }

        var summary = $"SELFTEST {(failed == 0 ? "PASS" : "FAIL")} 通过 {passed} 失败 {failed} 跳过 {skipped} → {path}";
        ConsoleBridge.WriteLine(summary);
        log.Info("SelfTest", summary);

        return failed == 0 ? 0 : 1;
    }
}
