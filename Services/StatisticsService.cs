using SimFlow.Models;

namespace SimFlow.Services;

/// <summary>
/// 统计分析的计算逻辑：给定项目列表与时间范围，算出一份 <see cref="StatisticsSnapshot"/>。
///
/// 统计口径（页面上的说明文字与这里必须一致）：
/// <list type="bullet">
/// <item>新建项目 = <see cref="ProjectRecord.CreatedAt"/> 落在所选区间内。</item>
/// <item>完成项目与项目周期 = <see cref="ProjectRecord.CompletedAt"/> 落在所选区间内。</item>
/// <item>仿真类型分布 / 软件使用 / 需求人 / 标签 / 等待时间 = 区间内创建的项目（按 CreatedAt），
/// 占比分母是区间内项目数。</item>
/// <item>进行中 / 等待中 / 已归档 = 当前全量快照，不随时间范围变化（它们是“现在有多少”，
/// 跟着时间范围变会得出违反直觉的结果）。</item>
/// </list>
///
/// 这里刻意不做专用 SQL 聚合：<c>IProjectRepository.GetAllAsync</c> 已经带上标签与软件关联，
/// 个人桌面工具的项目量（几十到几百）直接在内存里聚合更简单，也更容易测试。
/// </summary>
public sealed class StatisticsService : IStatisticsService
{
    /// <summary>标签使用频率只展示前 N 个。</summary>
    private const int TopTagLimit = 10;

    /// <summary>统计页底部“最近完成的项目”保留的行数。</summary>
    private const int RecentCompletedLimit = 3;

    /// <summary>趋势图最多 12 个刻度；“全部”模式跨度超过它时改按年聚合。</summary>
    private const int MaxTrendBuckets = 12;

    /// <summary>需求人为空时的统一文案。</summary>
    private const string UnfilledRequester = "未填写";

    /// <summary>项目周期区间（顺序即展示顺序）。</summary>
    private static readonly string[] CycleBucketLabels =
        ["0–3 天", "4–7 天", "8–14 天", "15–30 天", "31–60 天", ">60 天"];

    public StatisticsSnapshot Build(
        IReadOnlyList<ProjectRecord> projects,
        StatisticsRange range,
        DateTimeOffset now,
        IReadOnlyList<WaitReasonRecord>? waitReasons = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var localNow = now.ToLocalTime();
        var (start, end) = ResolveWindow(range, localNow);

        var inRange = projects.Where(project => InRange(project.CreatedAt, start, end)).ToList();
        var completedInRange = projects
            .Where(project => project.CompletedAt is { } completed && InRange(completed, start, end))
            .ToList();

        // 周期样本：已完成且开始/完成时间都齐全、且区间非负。缺 StartedAt 时不拿 CreatedAt 顶替
        // （现有业务里开始时间就是开始时间），这类项目只是不计入周期统计。
        var cycleSamples = completedInRange
            .Where(project => project.StartedAt is not null)
            .Select(project => (project.CompletedAt!.Value - project.StartedAt!.Value).TotalDays)
            .Where(days => days >= 0)
            .ToList();

        var waitSeconds = inRange.Select(project => WaitSeconds(project, localNow)).ToList();
        var projectsWithWait = inRange
            .Where((project, index) => waitSeconds[index] > 0 || !string.IsNullOrWhiteSpace(project.WaitReason))
            .Count();
        var totalWaitSeconds = waitSeconds.Sum();
        var (trend, trendByYear) = BuildTrend(projects, range, localNow, start, end);
        var (tags, tagCount) = BuildTagUsage(inRange);
        var (reasonStats, intervalCount) = BuildWaitReasons(inRange, waitReasons);
        // 同比：同一个口径的上一年度区间；「全部」没有可比区间。
        var previous = start is not null && end is not null
            ? (Start: start.Value.AddYears(-1), End: end.Value.AddYears(-1))
            : ((DateTimeOffset Start, DateTimeOffset End)?)null;

        return new StatisticsSnapshot
        {
            ProjectCountInRange = inRange.Count,
            WindowStart = start,
            WindowEnd = end,
            NewProjectCount = inRange.Count,
            CompletedProjectCount = completedInRange.Count,
            CompletedInRangeCount = completedInRange.Count,
            ActiveProjectCount = projects.Count(project =>
                project.WorkflowStatus == WorkflowStatus.Active && project.StorageStatus == StorageStatus.Working),
            WaitingProjectCount = projects.Count(project =>
                project.WorkflowStatus == WorkflowStatus.Waiting && project.StorageStatus == StorageStatus.Working),
            ArchivedProjectCount = projects.Count(project => project.StorageStatus == StorageStatus.Archived),
            SimulationTypes = BuildSimulationTypes(inRange),
            MonthlyTrend = trend,
            TrendAggregatedByYear = trendByYear,
            SoftwareUsage = BuildSoftwareUsage(inRange),
            Requesters = BuildRequesters(inRange),
            // “共 N 位需求人”算的是去重后的人数，不是项目数。
            RequesterCount = inRange
                .Select(project => project.Requester)
                .Where(requester => !string.IsNullOrWhiteSpace(requester))
                .Select(requester => requester.Trim())
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Count(),
            CycleDistribution = BuildCycleDistribution(cycleSamples),
            CycleSampleCount = cycleSamples.Count,
            TotalWaitSeconds = totalWaitSeconds,
            AverageWaitSeconds = projectsWithWait == 0 ? 0 : totalWaitSeconds / projectsWithWait,
            ProjectsWithWaitCount = projectsWithWait,
            TopTags = tags,
            TagCount = tagCount,
            WaitReasons = reasonStats,
            WaitIntervalCount = intervalCount,
            RecentCompleted = BuildRecentCompleted(completedInRange),
            NewProjectPreviousCount = previous is null
                ? null
                : projects.Count(project => InRange(project.CreatedAt, previous.Value.Start, previous.Value.End)),
            CompletedProjectPreviousCount = previous is null
                ? null
                : projects.Count(project => project.CompletedAt is { } completed
                    && InRange(completed, previous.Value.Start, previous.Value.End))
        };
    }

    /// <summary>
    /// 最近完成的项目：区间内已完成，按完成时间倒序取前几条。周期按“开始 → 完成”算，
    /// 缺开始时间就留空（不拿创建时间顶替）。
    /// </summary>
    private static List<RecentCompletedProject> BuildRecentCompleted(IReadOnlyList<ProjectRecord> completedInRange)
        => completedInRange
            .OrderByDescending(project => project.CompletedAt)
            .ThenBy(project => project.ProjectCode, StringComparer.OrdinalIgnoreCase)
            .Take(RecentCompletedLimit)
            .Select(project => new RecentCompletedProject(
                project.ProjectCode,
                project.Name,
                project.SimulationType,
                project.Requester,
                project.Software,
                project.CompletedAt!.Value,
                project.StartedAt is { } started && project.CompletedAt!.Value >= started
                    ? (project.CompletedAt.Value - started).TotalDays
                    : null))
            .ToList();

    /// <summary>
    /// 等待原因分布：按等待“次数”统计（每次进入等待算一次），数据来自状态历史里的原因字符串，
    /// 不新增一套原因枚举；占比之和为 100%。
    /// </summary>
    private static (List<WaitReasonStatistic> Items, int IntervalCount) BuildWaitReasons(
        IReadOnlyList<ProjectRecord> inRange,
        IReadOnlyList<WaitReasonRecord>? waitReasons)
    {
        if (waitReasons is null || waitReasons.Count == 0)
        {
            return ([], 0);
        }

        var projectIds = inRange.Select(project => project.Id).ToHashSet();
        var intervals = waitReasons
            .Where(record => projectIds.Contains(record.ProjectId) && !string.IsNullOrWhiteSpace(record.Reason))
            .ToList();
        var items = intervals
            .GroupBy(record => record.Reason.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new WaitReasonStatistic(
                group.Key,
                group.Count(),
                intervals.Count == 0 ? 0 : group.Count() * 100d / intervals.Count))
            .OrderByDescending(item => item.IntervalCount)
            .ThenBy(item => item.Reason, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return (items, intervals.Count);
    }

    /// <summary>把时间范围换成 [start, end) 的本地时间窗口；<see cref="StatisticsRange.All"/> 不设边界。</summary>
    private static (DateTimeOffset? Start, DateTimeOffset? End) ResolveWindow(StatisticsRange range, DateTimeOffset now)
    {
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        return range switch
        {
            StatisticsRange.CurrentMonth => (monthStart, monthStart.AddMonths(1)),
            // “近 3 个月”按自然月滚动：包含当前月在内往回 3 个月。
            StatisticsRange.LastThreeMonths => (monthStart.AddMonths(-2), monthStart.AddMonths(1)),
            StatisticsRange.CurrentYear => (
                new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset),
                new DateTimeOffset(now.Year + 1, 1, 1, 0, 0, 0, now.Offset)),
            _ => (null, null)
        };
    }

    private static bool InRange(DateTimeOffset value, DateTimeOffset? start, DateTimeOffset? end)
        => (start is null || value >= start) && (end is null || value < end);

    private static List<SimulationTypeStatistic> BuildSimulationTypes(IReadOnlyList<ProjectRecord> inRange)
    {
        var total = inRange.Count;
        var counts = inRange
            .Where(project => project.SimulationType.HasValue)
            .GroupBy(project => project.SimulationType!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var items = SimulationTypes.All
            .Select(type =>
            {
                var count = counts.GetValueOrDefault(type);
                return new SimulationTypeStatistic(type, SimulationTypes.Describe(type), count, Percentage(count, total));
            })
            .ToList();
        // 未分类始终排在最后：它是“缺数据”，不是一种业务类型。
        var unclassified = inRange.Count(project => !project.SimulationType.HasValue);
        items.Add(new SimulationTypeStatistic(null, SimulationTypes.Unclassified, unclassified, Percentage(unclassified, total)));
        return items;
    }

    private static List<SoftwareUsageStatistic> BuildSoftwareUsage(IReadOnlyList<ProjectRecord> inRange)
    {
        var total = inRange.Count;
        return inRange
            .SelectMany(project => project.Software.Distinct(StringComparer.OrdinalIgnoreCase))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new SoftwareUsageStatistic(group.First(), group.Count(), Percentage(group.Count(), total)))
            .OrderByDescending(item => item.ProjectCount)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<RequesterStatistic> BuildRequesters(IReadOnlyList<ProjectRecord> inRange)
    {
        return inRange
            .GroupBy(project => RequesterName(project.Requester), StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new RequesterStatistic(
                group.Key,
                group.Count(),
                group.Count(project => project.WorkflowStatus == WorkflowStatus.Completed),
                group.Count(project => project.WorkflowStatus == WorkflowStatus.Active),
                AverageCycleDays(group)))
            .OrderByDescending(item => item.ProjectCount)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>需求人为空的项目归到“未填写”，不因为空值抛异常也不被丢掉。</summary>
    private static string RequesterName(string? requester)
        => string.IsNullOrWhiteSpace(requester) ? UnfilledRequester : requester.Trim();

    private static double? AverageCycleDays(IEnumerable<ProjectRecord> projects)
    {
        var samples = projects
            .Where(project => project.StartedAt is not null && project.CompletedAt is not null)
            .Select(project => (project.CompletedAt!.Value - project.StartedAt!.Value).TotalDays)
            .Where(days => days >= 0)
            .ToList();
        return samples.Count == 0 ? null : samples.Average();
    }

    private static List<CycleDistributionBucket> BuildCycleDistribution(IReadOnlyList<double> cycleSamples)
    {
        var counts = new int[CycleBucketLabels.Length];
        foreach (var days in cycleSamples)
        {
            counts[CycleBucketIndex(days)]++;
        }

        return CycleBucketLabels
            .Select((label, index) => new CycleDistributionBucket(label, counts[index]))
            .ToList();
    }

    private static int CycleBucketIndex(double days) => days switch
    {
        <= 3 => 0,
        <= 7 => 1,
        <= 14 => 2,
        <= 30 => 3,
        <= 60 => 4,
        _ => 5
    };

    private static (List<TagUsageStatistic> Top, int Count) BuildTagUsage(IReadOnlyList<ProjectRecord> inRange)
    {
        var items = inRange
            .SelectMany(project => project.Tags)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new TagUsageStatistic(group.First(), group.Count()))
            .OrderByDescending(item => item.ProjectCount)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return (items.Take(TopTagLimit).ToList(), items.Count);
    }

    private static (List<MonthlyTrendPoint> Points, bool ByYear) BuildTrend(
        IReadOnlyList<ProjectRecord> projects,
        StatisticsRange range,
        DateTimeOffset now,
        DateTimeOffset? start,
        DateTimeOffset? end)
    {
        if (range != StatisticsRange.All)
        {
            // 本月 → 1 个刻度；近 3 个月 → 3 个；今年 → 1 月～12 月（未来的月份为 0，刻度保持完整）。
            var limit = range == StatisticsRange.CurrentYear
                ? new DateTimeOffset(now.Year + 1, 1, 1, 0, 0, 0, now.Offset)
                : end!.Value;
            var points = new List<MonthlyTrendPoint>();
            for (var cursor = start!.Value; cursor < limit; cursor = cursor.AddMonths(1))
            {
                var next = cursor.AddMonths(1);
                points.Add(new MonthlyTrendPoint(
                    $"{cursor.Month}月",
                    CountCreatedBetween(projects, cursor, next),
                    CountCompletedBetween(projects, cursor, next)));
            }

            return (points, false);
        }

        var earliest = EarliestTimestamp(projects);
        if (earliest is null)
        {
            return ([], false);
        }

        var first = earliest.Value.ToLocalTime();
        var months = ((now.Year - first.Year) * 12) + now.Month - first.Month + 1;
        if (months <= MaxTrendBuckets)
        {
            var points = new List<MonthlyTrendPoint>();
            var cursor = new DateTimeOffset(first.Year, first.Month, 1, 0, 0, 0, now.Offset);
            for (var index = 0; index < months; index++)
            {
                var next = cursor.AddMonths(1);
                points.Add(new MonthlyTrendPoint(
                    $"{cursor.Month}月",
                    CountCreatedBetween(projects, cursor, next),
                    CountCompletedBetween(projects, cursor, next)));
                cursor = next;
            }

            return (points, false);
        }

        // 跨度过长：按年聚合，最多保留最近 MaxTrendBuckets 年，避免刻度比柱子还密。
        var firstYear = Math.Max(first.Year, now.Year - (MaxTrendBuckets - 1));
        var years = new List<MonthlyTrendPoint>();
        for (var year = firstYear; year <= now.Year; year++)
        {
            years.Add(new MonthlyTrendPoint(
                $"{year}年",
                projects.Count(project => project.CreatedAt.Year == year),
                projects.Count(project => project.CompletedAt is { } completed && completed.Year == year)));
        }

        return (years, true);
    }

    private static DateTimeOffset? EarliestTimestamp(IReadOnlyList<ProjectRecord> projects)
    {
        DateTimeOffset? earliest = null;
        foreach (var project in projects)
        {
            foreach (var value in new[] { project.CreatedAt, project.CompletedAt, project.StartedAt })
            {
                if (value.HasValue && (earliest is null || value.Value < earliest.Value))
                {
                    earliest = value;
                }
            }
        }

        return earliest;
    }

    private static int CountCreatedBetween(IReadOnlyList<ProjectRecord> projects, DateTimeOffset start, DateTimeOffset end)
        => projects.Count(project => project.CreatedAt >= start && project.CreatedAt < end);

    private static int CountCompletedBetween(IReadOnlyList<ProjectRecord> projects, DateTimeOffset start, DateTimeOffset end)
        => projects.Count(project => project.CompletedAt is { } completed && completed >= start && completed < end);

    /// <summary>
    /// 已记录的等待时长：已完成等待区间之和（AccumulatedWaitSeconds）加上当前仍在等待的那一段
    /// （WaitStartedAt 到 now）。这与项目卡片上显示的等待时长是同一口径。
    /// </summary>
    private static long WaitSeconds(ProjectRecord project, DateTimeOffset now)
    {
        var seconds = Math.Max(0, project.AccumulatedWaitSeconds);
        if (project.WaitStartedAt is { } started)
        {
            seconds += Math.Max(0, (long)(now - started).TotalSeconds);
        }

        return seconds;
    }

    private static double Percentage(int count, int total) => total <= 0 ? 0 : count * 100d / total;
}
