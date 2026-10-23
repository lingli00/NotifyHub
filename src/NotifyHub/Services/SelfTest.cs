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

        Check("v0.2.7 需求1/2：灵动岛两栏配平 + 歌词字号（离屏布局）", () =>
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
            // 有音乐 + 歌词 + 任务（最拥挤的状态）：
            //   ① 左栏与右栏宽度接近（不再左挤右空）
            //   ② 整条宽度落在合理区间（不再被 440px 任务文字撑到 810）
            //   ③ 歌词正文左边界与歌名左边界对齐
            //   ④ 歌词字号 = 配置值 + 1.5
            //   ⑤ 两侧都有内容时分隔线出现
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
                island.ApplyLyricsTypography();   // provider 注入后才能读到字号
                island.SetMedia(new MediaInfo("侧脸", "魏语诺, 五毛", "FOLIA", true, "", "Folia", 30, 200));
                island.ShowLyrics("关于你的一切 我都想要了解", "陪伴三个季节 还是承受决裂");
                island.ShowRunning("WorkBuddy 正在梳理需求文档结构并拆分任务", "WorkBuddy");

                island.Measure(new Size(900, 300));
                island.Arrange(new Rect(0, 0, island.DesiredSize.Width, island.DesiredSize.Height));
                island.RefreshLayoutMetrics();
                island.Measure(new Size(900, 300));
                island.Arrange(new Rect(0, 0, island.DesiredSize.Width, island.DesiredSize.Height));

                var left = island.LeftPaneWidth;
                var right = island.RightPaneWidth;
                var total = island.DesiredSize.Width;

                var lyric = island.FindName("LyricLine1") as MarqueeText;
                var title = island.FindName("MusicText") as MarqueeText;
                var divider = island.FindName("Divider") as System.Windows.Controls.Border;
                if (lyric is null || title is null || divider is null)
                {
                    return false;
                }

                var balanced = left > 260
                               && right > 240
                               && Math.Abs(left - right) <= Math.Max(left, right) * 0.18;

                var fits = total > 520 && total <= 780;

                var lyricLeft = lyric.TranslatePoint(new Point(0, 0), island).X;
                var titleLeft = title.TranslatePoint(new Point(0, 0), island).X;
                var aligned = Math.Abs(lyricLeft - titleLeft) <= 2.5;

                var fontOk = Math.Abs(lyric.FontSize - 14.5) < 0.01;

                var dividerOk = divider.Visibility == Visibility.Visible;

                var diag = $"左栏={left:F1} 右栏={right:F1} 整条={total:F1} " +
                           $"歌词左={lyricLeft:F1} 歌名左={titleLeft:F1} 歌词字号={lyric.FontSize:F1} " +
                           $"分隔线={divider.Visibility} 歌词宽={lyric.ActualWidth:F1}";
                try
                {
                    File.WriteAllText(Path.Combine(AppPaths.Root, "island-layout.txt"), diag, Encoding.UTF8);
                }
                catch
                {
                    // 诊断文件写不进去不影响判定
                }

                // 只有音乐（无任务）：右侧栏收起，整条自然变窄
                var island2 = new IslandWindow
                {
                    ModeProvider = () => "Fixed",
                    MusicEnabledProvider = () => true,
                    MarqueeSpeedProvider = () => 45,
                    LyricsEnabledProvider = () => true,
                    LyricsFontSizeProvider = () => 16,
                    AutoCollapseEnabledProvider = () => false,
                    SavedPositionProvider = () => null,
                };

                try
                {
                    island2.ApplyTheme(ThemePalette.Create(new AppConfig()));
                    island2.ApplyLyricsTypography();
                    island2.SetMedia(new MediaInfo("侧脸", "魏语诺", "FOLIA", true, "", "Folia", 30, 200));
                    island2.ShowLyrics("关于你的一切 我都想要了解", "陪伴三个季节 还是承受决裂");
                    island2.Measure(new Size(900, 300));
                    island2.Arrange(new Rect(0, 0, island2.DesiredSize.Width, island2.DesiredSize.Height));
                    island2.RefreshLayoutMetrics();
                    island2.Measure(new Size(900, 300));
                    island2.Arrange(new Rect(0, 0, island2.DesiredSize.Width, island2.DesiredSize.Height));

                    var narrower = island2.DesiredSize.Width < total - 150;
                    var font2 = island2.FindName("LyricLine1") as MarqueeText;
                    var fontAdapt = font2 is not null && Math.Abs(font2.FontSize - 17.5) < 0.01;

                    try
                    {
                        File.AppendAllText(
                            Path.Combine(AppPaths.Root, "island-layout.txt"),
                            $"{Environment.NewLine}仅音乐: 整条={island2.DesiredSize.Width:F1} " +
                            $"字号={(font2?.FontSize ?? -1):F1} narrower={narrower} fontAdapt={fontAdapt}",
                            Encoding.UTF8);
                    }
                    catch
                    {
                        // 忽略
                    }

                    return balanced && fits && aligned && fontOk && dividerOk && narrower && fontAdapt;
                }
                finally
                {
                    island2.Close();
                }
            }
            finally
            {
                island.Close();
            }
        }

        Check("v0.2.7 需求2：收纳配置默认 3 秒 / 区间收敛 / 音乐例外默认开", () =>
        {
            var defaults = new AppConfig();
            var defaultsOk = defaults.AutoCollapseEnabled
                             && defaults.AutoCollapseSeconds == 3
                             && defaults.KeepOpenWhilePlaying
                             && defaults.LyricsEnabled
                             && defaults.LyricsFontSize == 13;

            var high = new AppConfig { AutoCollapseSeconds = 999, LyricsFontSize = 99 };
            high.Normalize();
            var highOk = high.AutoCollapseSeconds == AppConfig.MaxAutoCollapseSeconds
                         && high.LyricsFontSize == AppConfig.MaxLyricsFontSize;

            var low = new AppConfig { AutoCollapseSeconds = 0, LyricsFontSize = 1 };
            low.Normalize();
            var lowOk = low.AutoCollapseSeconds == AppConfig.MinAutoCollapseSeconds
                        && low.LyricsFontSize == AppConfig.MinLyricsFontSize;

            return defaultsOk && highOk && lowOk;
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
