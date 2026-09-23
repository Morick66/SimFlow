using CommunityToolkit.Mvvm.ComponentModel;
using SimFlow.Models;
using SimFlow.Services;
using System.Collections.ObjectModel;

namespace SimFlow.ViewModels;

/// <summary>
/// 统计分析页的视图模型：取项目列表 + 等待原因 → 交给 Core 的 <see cref="IStatisticsService"/> 聚合 →
/// 把结果转成图表/表格需要的展示对象（柱高、坐标轴刻度、环形角度、配色都在这里算好，
/// 界面只做一次 x:Bind，不在 View 层做计算）。
/// 统计口径本身不在这里，见 <see cref="StatisticsService"/>。
/// </summary>
public sealed partial class StatisticsViewModel(IProjectService projectService, IStatisticsService statistics) : ObservableObject
{
    /// <summary>柱状图绘图区高度（px）；柱子高度按坐标轴上限等比缩放，0 不画，非 0 至少 3px。</summary>
    private const double TrendBarMaxHeight = 118;
    private const double CycleBarMaxHeight = 112;

    /// <summary>图表可用宽度预算（px）。按项目当前最小卡片内容宽度取值，保证刻度不会溢出卡片。</summary>
    private const double ChartWidthBudget = 340;

    /// <summary>需求人排行最多显示的行数。</summary>
    private const int RequesterRowLimit = 8;

    /// <summary>等待原因最多显示的条数。</summary>
    private const int WaitReasonLimit = 8;

    /// <summary>环形图相邻扇区之间留出的角度（只有一片时不留）。</summary>
    private const double SliceGapDegrees = 1.2;

    /// <summary>环形图配色，顺序与 <see cref="SimulationTypes.All"/> 对应，最后一色是“未分类”。</summary>
    private static readonly string[] SliceColors =
        ["#2874F0", "#16A34A", "#F59E0B", "#8B5CF6", "#06B6D4", "#EC4899", "#64748B", "#94A3B8"];

    /// <summary>软件/标签等横条的配色池（按名称散列稳定取色，避免数量变化时颜色乱跳）。</summary>
    private static readonly string[] BarPalette =
        ["#2874F0", "#7C3AED", "#0EA5E9", "#F59E0B", "#16A34A", "#EC4899", "#06B6D4", "#EF4444", "#8B5CF6", "#14B8A6"];

    private int _loadToken;

    public ObservableCollection<KpiCardViewModel> KpiCards { get; } = [];
    public ObservableCollection<SimulationTypeSliceViewModel> TypeSlices { get; } = [];
    public ObservableCollection<TrendBarViewModel> TrendBars { get; } = [];
    public ObservableCollection<SoftwareUsageViewModel> SoftwareUsage { get; } = [];
    public ObservableCollection<RequesterRowViewModel> Requesters { get; } = [];
    public ObservableCollection<CycleBarViewModel> CycleBuckets { get; } = [];
    public ObservableCollection<WaitReasonViewModel> WaitReasons { get; } = [];
    public ObservableCollection<RecentCompletedViewModel> RecentCompleted { get; } = [];
    public ObservableCollection<TagUsageViewModel> TopTags { get; } = [];

    [ObservableProperty]
    public partial StatisticsRange Range { get; set; } = StatisticsRange.CurrentYear;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>当前时间范围实际覆盖的窗口（只读展示，例如 2026-01-01 ~ 2026-12-31）。</summary>
    [ObservableProperty]
    public partial string WindowText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SimulationTypeTotal { get; set; }

    [ObservableProperty]
    public partial string SimulationTypeCaption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSimulationTypes { get; set; }

    [ObservableProperty]
    public partial string TrendCaption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ChartAxisViewModel TrendAxis { get; set; } = ChartAxisViewModel.Create(0);

    [ObservableProperty]
    public partial ChartAxisViewModel CycleAxis { get; set; } = ChartAxisViewModel.Create(0);

    [ObservableProperty]
    public partial string SoftwareCaption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RequesterCaption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CycleCaption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CycleNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WaitReasonCaption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TagCaption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RecentCompletedCaption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TotalWaitText { get; set; } = "—";

    [ObservableProperty]
    public partial string AverageWaitText { get; set; } = "—";

    [ObservableProperty]
    public partial int ProjectsWithWaitCount { get; set; }

    [ObservableProperty]
    public partial bool HasTrend { get; set; }

    [ObservableProperty]
    public partial bool HasSoftwareUsage { get; set; }

    [ObservableProperty]
    public partial bool HasRequesters { get; set; }

    [ObservableProperty]
    public partial bool HasCycleData { get; set; }

    [ObservableProperty]
    public partial bool HasWaitReasons { get; set; }

    [ObservableProperty]
    public partial bool HasRecentCompleted { get; set; }

    [ObservableProperty]
    public partial bool HasTopTags { get; set; }

    public async Task SetRangeAsync(StatisticsRange range, CancellationToken cancellationToken = default)
    {
        Range = range;
        await RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// 重新读取项目并重算统计。每次进入统计页或切换时间范围时调用；
    /// 加令牌是为了防止连续切换时旧的一次加载把新结果覆盖掉。
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var token = ++_loadToken;
        IsBusy = true;
        try
        {
            var projects = await projectService.GetProjectsAsync(cancellationToken);
            var waitReasons = await projectService.GetWaitReasonsAsync(cancellationToken);
            if (token != _loadToken)
            {
                return;
            }

            Apply(statistics.Build(projects, Range, DateTimeOffset.Now, waitReasons));
            StatusMessage = string.Empty;
        }
        finally
        {
            if (token == _loadToken)
            {
                IsBusy = false;
            }
        }
    }

    private void Apply(StatisticsSnapshot snapshot)
    {
        WindowText = snapshot.WindowStart is { } start && snapshot.WindowEnd is { } end
            ? $"{start:yyyy-MM-dd} ~ {end.AddDays(-1):yyyy-MM-dd}"
            : "全部时间";
        ApplyKpiCards(snapshot);
        ApplySimulationTypes(snapshot);
        ApplyTrend(snapshot);
        ApplySoftwareUsage(snapshot);
        ApplyRequesters(snapshot);
        ApplyCycleDistribution(snapshot);
        ApplyWaitStatistics(snapshot);
        ApplyRecentCompleted(snapshot);
        ApplyTags(snapshot);
    }

    private void ApplyKpiCards(StatisticsSnapshot snapshot)
    {
        KpiCards.Clear();
        // 只有“新建项目/完成项目”能用上一年的同口径区间做真实同比；
        // 进行中/等待中/已归档是当前快照，没有历史快照，因此保持“当前”小字，不编造同比。
        KpiCards.Add(CreateKpiCard("新建项目", snapshot.NewProjectCount, "按创建时间", "\uE710", "#E8F1FF", "#2563EB",
            snapshot.NewProjectPreviousCount));
        KpiCards.Add(CreateKpiCard("完成项目", snapshot.CompletedProjectCount, "按完成时间", "\uE73E", "#E1F7EC", "#16A34A",
            snapshot.CompletedProjectPreviousCount));
        KpiCards.Add(CreateKpiCard("进行中", snapshot.ActiveProjectCount, "当前", "\uE768", "#DCEAFF", "#1261D8", null));
        KpiCards.Add(CreateKpiCard("等待中", snapshot.WaitingProjectCount, "当前", "\uE823", "#FFF3DA", "#F59E0B", null));
        KpiCards.Add(CreateKpiCard("已归档", snapshot.ArchivedProjectCount, "当前", "\uE7B8", "#EDF1F6", "#64748B", null));
    }

    private static KpiCardViewModel CreateKpiCard(
        string title, int count, string caption, string glyph, string background, string accent, int? previousCount)
    {
        if (previousCount is not { } previous)
        {
            return new KpiCardViewModel(title, count, caption, glyph, background, accent);
        }

        var delta = count - previous;
        var trend = previous == 0
            ? delta > 0 ? "↑ 新增" : string.Empty
            : $"{(delta >= 0 ? "↑" : "↓")} {Math.Abs(previous == 0 ? 0 : (int)Math.Round(delta * 100d / previous))}%";
        return new KpiCardViewModel(title, count, $"同比去年 {previous}", glyph, background, accent, trend,
            delta > 0 ? "#16A34A" : delta < 0 ? "#DC2626" : "#64748B");
    }

    private void ApplySimulationTypes(StatisticsSnapshot snapshot)
    {
        TypeSlices.Clear();
        SimulationTypeTotal = snapshot.ProjectCountInRange;
        SimulationTypeCaption = $"共 {snapshot.ProjectCountInRange} 个项目";
        // 按概念图：数量多的排前面、0 项不占图例位置；颜色仍按类型的固定顺序取，
        // 这样数量变化只会换位置、不会换颜色。相邻扇区留一点缝，视觉上更好分辨。
        var visible = snapshot.SimulationTypes
            .Where(item => item.ProjectCount > 0)
            .OrderByDescending(item => item.ProjectCount)
            .ThenBy(item => CanonicalTypeIndex(item.Type))
            .ToList();
        var gap = visible.Count > 1 ? SliceGapDegrees : 0d;
        var angle = -90d;
        foreach (var item in visible)
        {
            var rawSweep = item.Percentage / 100d * 360d;
            TypeSlices.Add(new SimulationTypeSliceViewModel(
                item.DisplayName,
                item.ProjectCount,
                $"{item.Percentage:0.#}%",
                SliceColors[Math.Clamp(CanonicalTypeIndex(item.Type), 0, SliceColors.Length - 1)],
                angle,
                Math.Max(0.6, rawSweep - gap)));
            angle += rawSweep;
        }

        HasSimulationTypes = TypeSlices.Count > 0;
    }

    private static int CanonicalTypeIndex(SimulationType? type)
        => type is { } value ? SimulationTypes.IndexOf(value) : SimulationTypes.All.Count;

    private void ApplyTrend(StatisticsSnapshot snapshot)
    {
        TrendBars.Clear();
        var maximum = snapshot.MonthlyTrend.Count == 0
            ? 0
            : snapshot.MonthlyTrend.Max(point => Math.Max(point.CreatedCount, point.CompletedCount));
        TrendAxis = ChartAxisViewModel.Create(maximum);
        var slot = SlotWidth(snapshot.MonthlyTrend.Count);
        var bar = BarWidth(slot);
        foreach (var point in snapshot.MonthlyTrend)
        {
            TrendBars.Add(new TrendBarViewModel(
                point.Label,
                point.CreatedCount,
                point.CompletedCount,
                BarHeight(point.CreatedCount, TrendAxis.Maximum, TrendBarMaxHeight),
                BarHeight(point.CompletedCount, TrendAxis.Maximum, TrendBarMaxHeight),
                slot,
                bar));
        }

        HasTrend = TrendBars.Count > 0;
        TrendCaption = snapshot.TrendAggregatedByYear
            ? "跨度较长，按年聚合"
            : $"共 {snapshot.ProjectCountInRange} 个项目（按创建时间）";
    }

    private void ApplySoftwareUsage(StatisticsSnapshot snapshot)
    {
        SoftwareUsage.Clear();
        foreach (var item in snapshot.SoftwareUsage)
        {
            var color = ColorForName(item.Name);
            SoftwareUsage.Add(new SoftwareUsageViewModel(
                item.Name,
                item.ProjectCount,
                Math.Clamp(item.Percentage, 0, 100),
                $"{item.ProjectCount} ({item.Percentage:0.#}%)",
                color,
                TintFor(color),
                item.Name.Length > 0 ? item.Name[..1].ToUpperInvariant() : "?"));
        }

        HasSoftwareUsage = SoftwareUsage.Count > 0;
        SoftwareCaption = $"共 {snapshot.ProjectCountInRange} 个项目";
    }

    private void ApplyRequesters(StatisticsSnapshot snapshot)
    {
        Requesters.Clear();
        var rank = 0;
        foreach (var item in snapshot.Requesters.Take(RequesterRowLimit))
        {
            Requesters.Add(new RequesterRowViewModel(
                ++rank,
                item.Name,
                item.ProjectCount,
                item.CompletedCount,
                item.ActiveCount,
                item.AverageCycleDays is { } days ? FormatCycle(days) : "—"));
        }

        HasRequesters = Requesters.Count > 0;
        RequesterCaption = snapshot.RequesterCount > 0
            ? $"共 {snapshot.RequesterCount} 位需求人"
            : "暂无需求人";
    }

    private void ApplyCycleDistribution(StatisticsSnapshot snapshot)
    {
        CycleBuckets.Clear();
        var maximum = snapshot.CycleDistribution.Count == 0
            ? 0
            : snapshot.CycleDistribution.Max(bucket => bucket.ProjectCount);
        CycleAxis = ChartAxisViewModel.Create(maximum);
        var slot = SlotWidth(snapshot.CycleDistribution.Count);
        var bar = BarWidth(slot);
        foreach (var bucket in snapshot.CycleDistribution)
        {
            CycleBuckets.Add(new CycleBarViewModel(
                bucket.Label,
                bucket.ProjectCount,
                BarHeight(bucket.ProjectCount, CycleAxis.Maximum, CycleBarMaxHeight),
                slot,
                bar));
        }

        HasCycleData = snapshot.CycleSampleCount > 0;
        CycleCaption = $"已完成项目 {snapshot.CompletedInRangeCount} 个";
        CycleNote = $"其中 {snapshot.CycleSampleCount} 个可计算周期（开始 → 完成）";
    }

    private void ApplyWaitStatistics(StatisticsSnapshot snapshot)
    {
        TotalWaitText = FormatTotalDuration(snapshot.TotalWaitSeconds);
        AverageWaitText = FormatAverageDuration(snapshot.AverageWaitSeconds);
        ProjectsWithWaitCount = snapshot.ProjectsWithWaitCount;

        // 等待原因来自状态历史里的真实原因字符串（等待下拉框写入），只是换个维度展示，不新增枚举。
        WaitReasons.Clear();
        foreach (var item in snapshot.WaitReasons.Take(WaitReasonLimit))
        {
            WaitReasons.Add(new WaitReasonViewModel(item.Reason, item.IntervalCount, $"{item.Percentage:0.#}%"));
        }

        HasWaitReasons = WaitReasons.Count > 0;
        WaitReasonCaption = snapshot.WaitIntervalCount > 0 ? $"按等待次数统计，共 {snapshot.WaitIntervalCount} 次" : string.Empty;
    }

    private void ApplyRecentCompleted(StatisticsSnapshot snapshot)
    {
        RecentCompleted.Clear();
        foreach (var item in snapshot.RecentCompleted)
        {
            RecentCompleted.Add(new RecentCompletedViewModel(
                item.Name,
                item.ProjectCode,
                SimulationTypes.Describe(item.SimulationType),
                string.IsNullOrWhiteSpace(item.Requester) ? "未填写" : item.Requester,
                item.Software.Count == 0 ? "未指定软件" : string.Join("  ·  ", item.Software),
                item.CompletedAt.ToString("yyyy-MM-dd"),
                item.CycleDays is { } days ? FormatCycle(days) : "—"));
        }

        HasRecentCompleted = RecentCompleted.Count > 0;
        RecentCompletedCaption = snapshot.CompletedInRangeCount > RecentCompleted.Count
            ? $"共 {snapshot.CompletedInRangeCount} 个已完成"
            : string.Empty;
    }

    private void ApplyTags(StatisticsSnapshot snapshot)
    {
        TopTags.Clear();
        // 条形长度按最高频标签归一化：标签统计看的是相对频率，不是占全部项目的比例。
        var maximum = snapshot.TopTags.Count == 0 ? 0 : snapshot.TopTags[0].ProjectCount;
        foreach (var item in snapshot.TopTags)
        {
            TopTags.Add(new TagUsageViewModel(
                item.Name,
                item.ProjectCount,
                maximum <= 0 ? 0 : item.ProjectCount * 100d / maximum));
        }

        HasTopTags = TopTags.Count > 0;
        TagCaption = snapshot.TagCount > TopTags.Count
            ? $"共 {snapshot.TagCount} 个标签，显示前 {TopTags.Count} 个"
            : $"共 {snapshot.TagCount} 个标签";
    }

    private static double SlotWidth(int count)
        => count <= 0 ? 54 : Math.Clamp(Math.Floor(ChartWidthBudget / count), 12, 54);

    private static double BarWidth(double slot) => Math.Clamp(Math.Round(slot * 0.34, 1), 6, 14);

    private static double BarHeight(int count, int maximum, double maxHeight)
        => count <= 0 ? 0 : Math.Max(3, Math.Round(count * maxHeight / Math.Max(1, maximum), 1));

    /// <summary>按名称散列稳定取色：同名软件永远同色，数量变化不会让颜色错位。</summary>
    private static string ColorForName(string name)
    {
        var hash = 0;
        foreach (var character in name)
        {
            hash = ((hash * 31) + character) & 0x7fffffff;
        }

        return BarPalette[hash % BarPalette.Length];
    }

    /// <summary>把主色压成很淡的同色底（图标块背景）。</summary>
    private static string TintFor(string color) => color.StartsWith('#') ? "#1F" + color[1..] : color;

    private static string FormatTotalDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "0 天";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays} 天 {duration.Hours} 小时";
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟"
            : $"{Math.Max(0, duration.Minutes)} 分钟";
    }

    private static string FormatAverageDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "—";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalDays >= 1 ? $"{duration.TotalDays:0.#} 天" : $"{duration.TotalHours:0.#} 小时";
    }

    /// <summary>周期/间隔的统一格式：天 → 小时 → 分钟，避免出现“0 小时”。</summary>
    private static string FormatCycle(double days)
    {
        if (days >= 1)
        {
            return $"{days:0.#} 天";
        }

        var hours = days * 24;
        return hours >= 1 ? $"{hours:0.#} 小时" : $"{Math.Max(1, Math.Round(hours * 60)):0} 分钟";
    }
}

/// <summary>
/// 柱状图左侧刻度：固定 5 条（含 0），上限抬到 4 的倍数以保证刻度都是整数；
/// 柱子高度按 <see cref="Maximum"/> 缩放。
/// </summary>
public sealed partial class ChartAxisViewModel : ObservableObject
{
    private ChartAxisViewModel()
    {
    }

    [ObservableProperty]
    public partial string Top { get; set; } = "0";

    [ObservableProperty]
    public partial string UpperMiddle { get; set; } = "0";

    [ObservableProperty]
    public partial string Middle { get; set; } = "0";

    [ObservableProperty]
    public partial string LowerMiddle { get; set; } = "0";

    [ObservableProperty]
    public partial string Bottom { get; set; } = "0";

    /// <summary>坐标轴上限（用于柱高缩放）。</summary>
    public int Maximum { get; private init; } = ChartScale.MinimumMaximum;

    public static ChartAxisViewModel Create(int maxCount)
    {
        var ticks = ChartScale.Ticks(maxCount);
        return new ChartAxisViewModel
        {
            Maximum = ChartScale.NiceMaximum(maxCount),
            Top = ticks[0].ToString(),
            UpperMiddle = ticks[1].ToString(),
            Middle = ticks[2].ToString(),
            LowerMiddle = ticks[3].ToString(),
            Bottom = ticks[4].ToString()
        };
    }
}

/// <summary>顶部概览卡片：数量 + 口径/同比小字（同比只在能真实计算时才有）。</summary>
public sealed record KpiCardViewModel(
    string Title,
    int Count,
    string Footnote,
    string IconGlyph,
    string IconBackground,
    string AccentColor,
    string TrendText = "",
    string TrendColor = "#64748B")
{
    public bool HasTrend => TrendText.Length > 0;
}

/// <summary>环形图的一片：角度在视图模型里算好，视图只负责画弧。</summary>
public sealed record SimulationTypeSliceViewModel(
    string Name,
    int Count,
    string PercentageText,
    string Color,
    double StartAngle,
    double SweepAngle);

/// <summary>月度趋势图的一根刻度（两个系列并排）。</summary>
public sealed record TrendBarViewModel(
    string Label,
    int CreatedCount,
    int CompletedCount,
    double CreatedHeight,
    double CompletedHeight,
    double SlotWidth,
    double BarWidth)
{
    public string CreatedTooltip => $"{Label}　新建项目：{CreatedCount}";
    public string CompletedTooltip => $"{Label}　完成项目：{CompletedCount}";
}

/// <summary>项目周期分布的一根柱子。</summary>
public sealed record CycleBarViewModel(
    string Label,
    int ProjectCount,
    double BarHeight,
    double SlotWidth,
    double BarWidth)
{
    public string Tooltip => $"{Label}：{ProjectCount} 个";
}

/// <summary>软件使用情况的一行：图标块颜色、条形颜色、项目数与占比（可多选，合计可超过 100%）。</summary>
public sealed record SoftwareUsageViewModel(
    string Name,
    int ProjectCount,
    double Percentage,
    string CountText,
    string Color,
    string IconBackground,
    string Initial);

/// <summary>需求人排行的一行。</summary>
public sealed record RequesterRowViewModel(
    int Rank,
    string Name,
    int ProjectCount,
    int CompletedCount,
    int ActiveCount,
    string AverageCycleText);

/// <summary>等待原因分布的一行（按等待次数）。</summary>
public sealed record WaitReasonViewModel(string Reason, int IntervalCount, string PercentageText)
{
    public string CountTooltip => $"{Reason}：{IntervalCount} 次";
}

/// <summary>最近完成的项目一行。</summary>
public sealed record RecentCompletedViewModel(
    string Name,
    string ProjectCode,
    string SimulationTypeText,
    string RequesterText,
    string SoftwareText,
    string CompletedText,
    string CycleText);

/// <summary>标签使用频率的一行；百分比按最高频标签归一化。</summary>
public sealed record TagUsageViewModel(string Name, int ProjectCount, double Percentage)
{
    public string CountText => ProjectCount.ToString();
}
