using System.Text.RegularExpressions;

namespace NotifyHub.Services;

/// <summary>
/// WorkBuddy 通知文案过滤器（v0.2.19 需求5）：
/// **只放行结论类文字**，过滤掉中间过程——调用了哪个工具、编辑了哪些文件、
/// 执行了什么命令、思考/校验日志等，一律不进通知与岛内文案。
///
/// 判定思路（纯函数，便于自检直接断言）：
///   1) 代码围栏、表格线、纯符号/emoji 行直接丢弃；
///   2) "动作词 + 目标"（如「修改：IslandWindow.xaml.cs」「查看文件、运行校验：island-layout.txt」）
///      这种描述工具行为的行丢弃；
///   3) 单独的动作词（「运行命令」「运行校验」「查看文件」…）丢弃；
///   4) 剩下的第一行有意义的文字即结论，作为通知内容。
/// </summary>
public static class WorkBuddyTextFilter
{
    /// <summary>
    /// 兼容保留：v0.2.20 起**不再截断**结论文字（ntfy 要推全文）。
    /// 岛内渲染的宽度上限由 `IslandController.IslandDisplayLimit` 单独兜底。
    /// </summary>
    public const int MaxLength = int.MaxValue;

    /// <summary>单独出现即视为"过程"的动作词（没有结论含义）。</summary>
    private static readonly string[] BareActionWords =
    {
        "运行命令", "运行校验", "运行测试", "查看文件", "读取文件", "读取文档", "执行命令",
        "调用工具", "工具调用", "思考中", "正在思考", "正在分析", "开始执行", "执行中",
        "写入文件", "修改文件", "编辑文件", "搜索文件", "查看代码", "读取代码",
    };

    /// <summary>"动作词 + 目标" 形态：动作(可选动作)、动作：目标。</summary>
    private static readonly Regex ActionWithTarget = new(
        @"^(运行|查看|读取|修改|编辑|写入|保存|执行|调用|搜索|查找|编译|构建|测试|删除|创建|新建|移动|重命名|安装|下载|上传)"
        + @"(校验|命令|文件|文档|代码|目录|测试)?([、,，].*?)?[:：]\s*(?<target>.+)$",
        RegexOptions.Compiled);

    /// <summary>把一段 AI 回复压成"一句结论"；没有可用结论时返回空串。</summary>
    public static string ToConclusion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var inFence = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            // 代码围栏整段跳过（里面是命令/代码，不是结论）
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            line = Normalize(line);
            if (line.Length == 0 || IsProcessLine(line) || IsNoiseLine(line))
            {
                continue;
            }

            return line;
        }

        return string.Empty;
    }

    /// <summary>该行是否是"过程信息"（工具 / 文件 / 命令 / 校验日志）。</summary>
    public static bool IsProcessLine(string line)
    {
        var s = Normalize(line);
        if (s.Length == 0)
        {
            return true;
        }

        var plain = s.TrimEnd('：', ':', '。', '.').Trim();

        foreach (var word in BareActionWords)
        {
            if (plain.Equals(word, StringComparison.Ordinal)
                || plain.StartsWith(word + "：", StringComparison.Ordinal)
                || plain.StartsWith(word + ":", StringComparison.Ordinal)
                || plain.StartsWith(word + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        var match = ActionWithTarget.Match(plain);
        if (match.Success && LooksLikeTarget(match.Groups["target"].Value))
        {
            return true;
        }

        // 命令行样式（powershell / python / dotnet / git / npm …）
        if (Regex.IsMatch(plain, @"^(运行|执行|输入|敲)\s*(powershell|pwsh|cmd|python|node|npm|git|dotnet|msbuild|curl)\b",
                RegexOptions.IgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 目标是否像"文件 / 目录 / 命令"（而不是一句中文结论）。
    /// 「编译验证：」这种冒号后为空的行不算目标，所以会保留为结论。
    /// </summary>
    private static bool LooksLikeTarget(string target)
    {
        var t = target.Trim().Trim('`', '"', '\'');
        if (t.Length == 0)
        {
            return false;
        }

        // 路径分隔符 / 盘符
        if (t.Contains('\\') || t.Contains('/') || Regex.IsMatch(t, @"^[A-Za-z]:$"))
        {
            return true;
        }

        // 带扩展名的文件名（如 IslandWindow.xaml.cs / island-layout.txt）
        if (Regex.IsMatch(t, @"\.[A-Za-z0-9]{1,5}\b"))
        {
            return true;
        }

        // 纯 ASCII 且不含空格中文（命令名 / 标识符）
        if (Regex.IsMatch(t, @"^[A-Za-z0-9_\-\.,: ]+$") && !t.Contains(' '))
        {
            return true;
        }

        // "源码.js 与 目标.css" 这类多文件名描述
        if (Regex.IsMatch(t, @"[^\s，。]+\.[A-Za-z0-9]{1,5}(\s|，|、|$)"))
        {
            return true;
        }

        return false;
    }

    /// <summary>去掉 Markdown 装饰（标题井号、列表符、引用符、行内反引号）。</summary>
    private static string Normalize(string line)
    {
        var s = line.Trim();
        if (s.Length == 0)
        {
            return s;
        }

        // 列表序号 "1. " / "- " / "* " / "> " / "#" 前缀
        s = Regex.Replace(s, @"^(#{1,6}\s*|[-*>+]\s+|\d{1,2}[.、)]\s+)", string.Empty);
        s = s.Replace("`", string.Empty).Replace("**", string.Empty).Trim();

        return s;
    }

    /// <summary>纯符号 / 分隔线 / emoji 行没有信息量。</summary>
    private static bool IsNoiseLine(string line)
    {
        if (line.Length < 2)
        {
            return true;
        }

        // 表格分隔线、纯标点
        if (Regex.IsMatch(line, @"^[\s\-=_*#~|:·．。，,、]+$"))
        {
            return true;
        }

        // 一个汉字/字母都没有（纯 emoji / 数字）
        return !Regex.IsMatch(line, @"[\u4e00-\u9fa5A-Za-z]");
    }

    private static string Cut(string text, int max)
        => text.Length <= max ? text : text[..max].TrimEnd() + "…";
}
