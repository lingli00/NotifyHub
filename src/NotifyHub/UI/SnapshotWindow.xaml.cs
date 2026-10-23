using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using NotifyHub.Services;

namespace NotifyHub.UI;

/// <summary>
/// Bug 快照查看页（v0.2.10 需求 6）：左边整屏截图、右边同一时段的核心模块日志。
///
/// 为什么做成独立窗口而不是塞进设置页：截图是整屏、日志动辄几十上百行，
/// 挤在设置页的一个分组里两边都看不清；独立窗口可以并排对照，也能随时切历史快照。
/// 这里**不持有任何后台线程**：打开时读一次文件，点"刷新"再读一次，关掉就结束。
/// </summary>
public partial class SnapshotWindow : Window
{
    private readonly Dictionary<string, bool> _moduleEnabled = new();
    private List<BugSnapshotService.LogLine> _lines = new();
    private List<BugSnapshotService.SnapshotInfo> _snapshots = new();
    private bool _suppressSelection;

    public SnapshotWindow()
        : this(null)
    {
    }

    public SnapshotWindow(string? snapshotId)
    {
        InitializeComponent();

        _suppressSelection = true;
        _snapshots = BugSnapshotService.List();
        SnapshotCombo.ItemsSource = _snapshots;
        SnapshotCombo.DisplayMemberPath = nameof(BugSnapshotService.SnapshotInfo.Display);
        _suppressSelection = false;

        BuildModuleChips();

        var target = snapshotId is not null
            ? _snapshots.FirstOrDefault(s => s.Id == snapshotId)
            : _snapshots.FirstOrDefault();

        if (target is not null)
        {
            SnapshotCombo.SelectedItem = target;
        }
        else
        {
            StatusText.Text = "还没有任何快照。到「设置 → 诊断与反馈」点「导出 Bug 快照」。";
        }
    }

    /// <summary>五个核心模块的筛选按钮（默认全开）。</summary>
    private void BuildModuleChips()
    {
        foreach (var module in BugSnapshotService.Modules)
        {
            _moduleEnabled[module] = true;

            var toggle = new ToggleButton
            {
                Content = module,
                IsChecked = true,
                Style = (Style)FindResource("Chip"),
            };

            toggle.Checked += (_, _) =>
            {
                _moduleEnabled[module] = true;
                ApplyFilter();
            };

            toggle.Unchecked += (_, _) =>
            {
                _moduleEnabled[module] = false;
                ApplyFilter();
            };

            ModulePanel.Children.Add(toggle);
        }
    }

    private void OnSnapshotSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        if (SnapshotCombo.SelectedItem is BugSnapshotService.SnapshotInfo info)
        {
            Load(info);
        }
    }

    private void Load(BugSnapshotService.SnapshotInfo info)
    {
        PathText.Text = info.Directory;
        StatusText.Text = info.Completed
            ? $"快照 {info.CreatedAt:yyyy-MM-dd HH:mm:ss} · 前后各 10 秒日志已完整采集"
            : $"快照 {info.CreatedAt:yyyy-MM-dd HH:mm:ss} · 后段日志仍在补录（10 秒后点「刷新」）";

        // 截图
        try
        {
            var shot = Path.Combine(info.Directory, "screen.png");
            if (File.Exists(shot))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(shot);
                bitmap.EndInit();
                bitmap.Freeze();
                ShotImage.Source = bitmap;
            }
            else
            {
                ShotImage.Source = null;
                StatusText.Text += "（截图缺失）";
            }
        }
        catch (Exception ex)
        {
            LogService.Shared.Warn("Snapshot", "加载快照截图失败", ex.Message);
            ShotImage.Source = null;
        }

        _lines = BugSnapshotService.ReadLines(info.Directory);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = _lines
            .Where(l => _moduleEnabled.TryGetValue(l.Module, out var on) && on)
            .ToList();

        LogList.ItemsSource = filtered;
        CountText.Text = $"显示 {filtered.Count}/{_lines.Count} 行";
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = PathText.Text;
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            LogService.Shared.Warn("Snapshot", "打开快照目录失败", ex.Message);
        }
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(PathText.Text))
            {
                Clipboard.SetText(PathText.Text);
                StatusText.Text = "已复制快照路径";
            }
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        var currentId = (SnapshotCombo.SelectedItem as BugSnapshotService.SnapshotInfo)?.Id;

        _suppressSelection = true;
        _snapshots = BugSnapshotService.List();
        SnapshotCombo.ItemsSource = _snapshots;
        _suppressSelection = false;

        var target = currentId is not null
            ? _snapshots.FirstOrDefault(s => s.Id == currentId)
            : _snapshots.FirstOrDefault();

        if (target is not null)
        {
            SnapshotCombo.SelectedItem = target;
            Load(target);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
