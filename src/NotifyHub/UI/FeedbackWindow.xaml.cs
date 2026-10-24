using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>
/// 误拦截反馈窗（v0.2.17 工单A 优化建议 3）。
/// 数据来源：<see cref="FeedbackService.RecentBlocks"/> 从运行日志还原的"最近被拦截"列表；
/// 用户选中一条 → 自动填入会话名/原因（连同渠道标签与内容类型一起带进反馈 JSON），
/// 也可以完全手动填写。提交 = 写 JSON 到反馈目录 + 复制到剪贴板。
/// </summary>
public partial class FeedbackWindow : Window
{
    private BlockedEntry? _selected;

    public FeedbackWindow()
    {
        // 先按系统深浅色准备画刷资源（XAML 里的 DynamicResource 会在加载时来查）
        ApplyChrome();

        InitializeComponent();

        // 标准窗口样式（系统标题栏）：最小化行为正常，不需要 WindowSquashGuard
        var blocks = FeedbackService.RecentBlocks();
        if (blocks.Count == 0)
        {
            BlockList.Items.Add("（最近没有被拦截的记录——可以直接手动填写下方两栏）");
            BlockList.IsEnabled = false;
        }
        else
        {
            foreach (var entry in blocks)
            {
                BlockList.Items.Add(entry);
            }
        }

        StatusText.Text = $"反馈目录：{FeedbackService.FolderPath}（已提交 {FeedbackService.SubmittedCount()} 条）";
    }

    /// <summary>
    /// 深浅色画刷（与设置窗同一套取值）。必须放在 <see cref="InitializeComponent"/> 之前——
    /// XAML 的 Background="{DynamicResource WinBg}" 在加载期就会来查这些键。
    /// </summary>
    private void ApplyChrome()
    {
        var light = SystemCapabilities.Current.AppsUseLightTheme;

        var fg = light ? Color.FromRgb(0x1F, 0x23, 0x28) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        var sub = light ? Color.FromRgb(0x6B, 0x72, 0x80) : Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF);
        var winBg = light ? Color.FromRgb(0xF7, 0xF7, 0xF9) : Color.FromRgb(0x1E, 0x1E, 0x22);
        var inputBg = light ? Colors.White : Color.FromRgb(0x2A, 0x2A, 0x30);
        var inputBorder = light ? Color.FromRgb(0xD1, 0xD5, 0xDB) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

        Resources["FgBrush"] = new SolidColorBrush(fg);
        Resources["SubFgBrush"] = new SolidColorBrush(sub);
        Resources["WinBg"] = new SolidColorBrush(winBg);
        Resources["InputBg"] = new SolidColorBrush(inputBg);
        Resources["InputBorder"] = new SolidColorBrush(inputBorder);
    }

    private void OnBlockSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BlockList.SelectedItem is not BlockedEntry entry)
        {
            return;
        }

        _selected = entry;
        ContactBox.Text = entry.Contact;
        ReasonBox.Text = entry.Reason;
        StatusText.Text = $"已填入 {entry.At:HH:mm:ss} 的记录（渠道={entry.SourceTag}，内容={entry.Content}），可补充说明后提交。";
    }

    private void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        var contact = ContactBox.Text?.Trim() ?? string.Empty;
        var reason = ReasonBox.Text?.Trim() ?? string.Empty;

        if (contact.Length == 0 && reason.Length == 0)
        {
            StatusText.Text = "会话名和拦截原因至少填一项。";
            return;
        }

        var path = FeedbackService.Submit(
            contact,
            reason,
            CommentBox.Text,
            _selected?.SourceTag,
            _selected?.Content);

        if (path is null)
        {
            StatusText.Text = "保存失败（反馈目录不可写？）。可以手动复制上方内容发给开发者。";
            return;
        }

        StatusText.Text = $"已保存：{path}\nJSON 已复制到剪贴板，直接贴给开发者即可。";
        CommentBox.Text = string.Empty;
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        => FeedbackService.OpenFolder();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
