using SimFlow.Models;
using SimFlow.Services;
using Xunit;

namespace SimFlow.Tests;

/// <summary>
/// 统计聚合的纯计算测试：不碰数据库，直接喂领域对象。
/// 覆盖仿真类型数量、软件项目数、需求人空值、缺失时间字段等容易出错的边界。
/// </summary>
public sealed class StatisticsServiceTests
{
    private readonly StatisticsService _statistics = new();

    /// <summary>测试统一用当天中午的时间戳，避免时区换算把日期挤到相邻月份。</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 12, 0, 0, TimeSpan.Zero);

    private static ProjectRecord Project(
        string code,
        DateTimeOffset created,
        SimulationType? type = null,
        string requester = "",
        string[]? tags = null,
        string[]? software = null,
        WorkflowStatus status = WorkflowStatus.Active,
        DateTimeOffset? started = null,
        DateTimeOffset? completed = null,
        long accumulatedWaitSeconds = 0,
        DateTimeOffset? waitStartedAt = null,
        string? waitReason = null,
        long id = 0)
        => new()
        {
            Id = id,
            ProjectCode = code,
            Name = code,
            RelativePath = code,
            CreatedAt = created,
            UpdatedAt = created,
            SimulationType = type,
            Requester = requester,
            Tags = tags?.ToList() ?? [],
            Software = software?.ToList() ?? [],
            WorkflowStatus = status,
            StartedAt = started,
            CompletedAt = completed,
            AccumulatedWaitSeconds = accumulatedWaitSeconds,
            WaitStartedAt = waitStartedAt,
            WaitReason = waitReason
        };

    [Fact]
    public void Build_CountsSimulationTypesIncludingUnclassified()
    {
        var projects = new[]
        {
            Project("A", At(2026, 3, 1), SimulationType.Dynamics),
            Project("B", At(2026, 4, 1), SimulationType.Dynamics),
            Project("C", At(2026, 5, 1))
        };

        var snapshot = _statistics.Build(projects, StatisticsRange.CurrentYear, Now);

        // 固定 8 行（7 个类型 + 未分类），0 项的类型也保留，图例顺序稳定。
        Assert.Equal(SimulationTypes.All.Count + 1, snapshot.SimulationTypes.Count);
        Assert.Equal(SimulationTypes.Describe(SimulationType.Dynamics), snapshot.SimulationTypes[0].DisplayName);
        Assert.Equal(2, snapshot.SimulationTypes.Single(item => item.Type == SimulationType.Dynamics).ProjectCount);
        Assert.Equal(0, snapshot.SimulationTypes.Single(item => item.Type == SimulationType.Electromagnetics).ProjectCount);
        Assert.Equal(1, snapshot.SimulationTypes.Single(item => item.Type is null).ProjectCount);
        Assert.Equal(SimulationTypes.Unclassified, snapshot.SimulationTypes[^1].DisplayName);
        Assert.Equal(3, snapshot.ProjectCountInRange);

        // 占比分母是区间内项目数。
        Assert.Equal(200d / 3d, snapshot.SimulationTypes.Single(item => item.Type == SimulationType.Dynamics).Percentage, 3);
        Assert.Equal(100d / 3d, snapshot.SimulationTypes.Single(item => item.Type is null).Percentage, 3);
    }

    [Fact]
    public void Build_CountsProjectsPerSoftwareAndAllowsTotalsOverHundredPercent()
    {
        var projects = new[]
        {
            Project("A", At(2026, 3, 1), software: ["Adams", "ANSYS Maxwell"]),
            Project("B", At(2026, 4, 1), software: ["Adams"]),
            Project("C", At(2026, 5, 1), software: ["ANSYS Maxwell", "nCode"]),
            // 没有软件的项目不参与，但仍在分母里。
            Project("D", At(2026, 6, 1))
        };

        var snapshot = _statistics.Build(projects, StatisticsRange.CurrentYear, Now);

        Assert.Equal(4, snapshot.ProjectCountInRange);
        Assert.Equal(2, snapshot.SoftwareUsage.Single(item => item.Name == "Adams").ProjectCount);
        Assert.Equal(2, snapshot.SoftwareUsage.Single(item => item.Name == "ANSYS Maxwell").ProjectCount);
        Assert.Equal(1, snapshot.SoftwareUsage.Single(item => item.Name == "nCode").ProjectCount);
        Assert.Equal(50d, snapshot.SoftwareUsage.Single(item => item.Name == "Adams").Percentage, 3);
        // 一个项目可用多个软件：合计可以超过 100%。
        Assert.True(snapshot.SoftwareUsage.Sum(item => item.Percentage) > 100d);
        // 按项目数倒序。
        Assert.Equal(2, snapshot.SoftwareUsage[0].ProjectCount);
        Assert.Equal(2, snapshot.SoftwareUsage[1].ProjectCount);
    }

    [Fact]
    public void Build_HandlesMissingRequesterWithoutThrowing()
    {
        var projects = new[]
        {
            Project("A", At(2026, 3, 1), requester: "张三", status: WorkflowStatus.Completed, started: At(2026, 3, 2), completed: At(2026, 3, 12)),
            Project("B", At(2026, 4, 1), requester: ""),
            Project("C", At(2026, 5, 1), requester: "   "),
            Project("D", At(2026, 6, 1), requester: "张三")
        };

        var snapshot = _statistics.Build(projects, StatisticsRange.CurrentYear, Now);

        // 空需求人归到“未填写”，不会丢项目也不会抛异常。
        Assert.Equal(2, snapshot.Requesters.Single(item => item.Name == "未填写").ProjectCount);
        Assert.Equal(2, snapshot.Requesters.Single(item => item.Name == "张三").ProjectCount);
        Assert.Equal(1, snapshot.RequesterCount);
        var zhangsan = snapshot.Requesters.Single(item => item.Name == "张三");
        Assert.Equal(1, zhangsan.CompletedCount);
        Assert.Equal(10d, zhangsan.AverageCycleDays!.Value, 3);
        // 没有可计算周期的需求人显示为 null，由界面转成“—”。
        Assert.Null(snapshot.Requesters.Single(item => item.Name == "未填写").AverageCycleDays);
    }

    [Fact]
    public void Build_CycleDistributionIgnoresProjectsWithoutUsableTimestamps()
    {
        var projects = new[]
        {
            // 2 天 → 0–3 天
            Project("A", At(2026, 3, 1), status: WorkflowStatus.Completed, started: At(2026, 3, 10), completed: At(2026, 3, 12)),
            // 40 天 → 31–60 天
            Project("B", At(2026, 1, 1), status: WorkflowStatus.Completed, started: At(2026, 1, 1), completed: At(2026, 2, 10)),
            // 已完成但没有开始时间：不能拿创建时间顶替，直接不算样本
            Project("C", At(2026, 4, 1), status: WorkflowStatus.Completed, completed: At(2026, 4, 5)),
            // 时间区间为负（脏数据）：跳过
            Project("D", At(2026, 5, 1), status: WorkflowStatus.Completed, started: At(2026, 5, 20), completed: At(2026, 5, 1)),
            // 尚未完成
            Project("E", At(2026, 6, 1), status: WorkflowStatus.Active, started: At(2026, 6, 1))
        };

        var snapshot = _statistics.Build(projects, StatisticsRange.CurrentYear, Now);

        Assert.Equal(4, snapshot.CompletedInRangeCount);
        Assert.Equal(2, snapshot.CycleSampleCount);
        Assert.Equal(6, snapshot.CycleDistribution.Count);
        Assert.Equal(1, snapshot.CycleDistribution.Single(item => item.Label == "0–3 天").ProjectCount);
        Assert.Equal(1, snapshot.CycleDistribution.Single(item => item.Label == "31–60 天").ProjectCount);
        Assert.Equal(0, snapshot.CycleDistribution.Single(item => item.Label == "4–7 天").ProjectCount);
        Assert.Equal(2, snapshot.CycleDistribution.Sum(item => item.ProjectCount));
    }

    [Fact]
    public void Build_RangeFiltersCreatedAndCompletedButKeepsCurrentStatusSnapshot()
    {
        var projects = new[]
        {
            Project("A", At(2026, 3, 1), status: WorkflowStatus.Active, started: At(2026, 3, 1)),
            Project("B", At(2025, 12, 1), status: WorkflowStatus.Active, started: At(2025, 12, 1)),
            Project("C", At(2026, 9, 1), status: WorkflowStatus.Completed, started: At(2026, 9, 2), completed: At(2026, 9, 10)),
            Project("E", At(2026, 8, 1), status: WorkflowStatus.Waiting, waitReason: "等待试验", waitStartedAt: At(2026, 9, 1)),
            Project("F", At(2025, 1, 1), status: WorkflowStatus.Completed, completed: At(2025, 2, 1))
        };
        // 归档项目：存储状态独立于业务状态。
        projects[4].StorageStatus = StorageStatus.Archived;

        var yearly = _statistics.Build(projects, StatisticsRange.CurrentYear, Now);
        Assert.Equal(3, yearly.NewProjectCount);            // A、C、E 在 2026 年创建
        Assert.Equal(1, yearly.CompletedProjectCount);      // 只有 C 在 2026 年完成
        Assert.Equal(2, yearly.ActiveProjectCount);         // 当前快照：A、B（不受区间影响）
        Assert.Equal(1, yearly.WaitingProjectCount);
        Assert.Equal(1, yearly.ArchivedProjectCount);

        var all = _statistics.Build(projects, StatisticsRange.All, Now);
        Assert.Equal(5, all.NewProjectCount);
        Assert.Equal(2, all.CompletedProjectCount);
        Assert.Equal(2, all.ActiveProjectCount);

        var month = _statistics.Build(projects, StatisticsRange.CurrentMonth, Now);
        Assert.Equal(1, month.NewProjectCount);             // 只有 C 在 2026-09 创建
        Assert.Equal(1, month.CompletedProjectCount);

        var lastThree = _statistics.Build(projects, StatisticsRange.LastThreeMonths, Now);
        Assert.Equal(2, lastThree.NewProjectCount);         // E（8 月）与 C（9 月）
    }

    [Fact]
    public void Build_MonthlyTrendFollowsSelectedRange()
    {
        var projects = new[]
        {
            Project("A", At(2026, 1, 5), status: WorkflowStatus.Completed, started: At(2026, 1, 5), completed: At(2026, 3, 5)),
            Project("B", At(2026, 3, 20)),
            Project("C", At(2026, 9, 2))
        };

        var yearly = _statistics.Build(projects, StatisticsRange.CurrentYear, Now);
        Assert.False(yearly.TrendAggregatedByYear);
        Assert.Equal(12, yearly.MonthlyTrend.Count);
        Assert.Equal("1月", yearly.MonthlyTrend[0].Label);
        Assert.Equal("12月", yearly.MonthlyTrend[^1].Label);
        Assert.Equal(1, yearly.MonthlyTrend[0].CreatedCount);
        Assert.Equal(1, yearly.MonthlyTrend[2].CompletedCount);
        Assert.Equal(0, yearly.MonthlyTrend[11].CreatedCount);

        var lastThree = _statistics.Build(projects, StatisticsRange.LastThreeMonths, Now);
        Assert.Equal(3, lastThree.MonthlyTrend.Count);
        Assert.Equal(["7月", "8月", "9月"], lastThree.MonthlyTrend.Select(point => point.Label));
        Assert.Equal(1, lastThree.MonthlyTrend[2].CreatedCount);

        var month = _statistics.Build(projects, StatisticsRange.CurrentMonth, Now);
        Assert.Single(month.MonthlyTrend);
        Assert.Equal(1, month.MonthlyTrend[0].CreatedCount);
    }

    [Fact]
    public void Build_AggregatesTrendByYearWhenAllRangeSpansTooLong()
    {
        var projects = new[]
        {
            Project("A", At(2020, 5, 1)),
            Project("B", At(2026, 3, 1))
        };

        var snapshot = _statistics.Build(projects, StatisticsRange.All, Now);

        Assert.True(snapshot.TrendAggregatedByYear);
        Assert.Equal(7, snapshot.MonthlyTrend.Count);
        Assert.Equal("2020年", snapshot.MonthlyTrend[0].Label);
        Assert.Equal("2026年", snapshot.MonthlyTrend[^1].Label);
        Assert.Equal(1, snapshot.MonthlyTrend[0].CreatedCount);
        Assert.Equal(1, snapshot.MonthlyTrend[^1].CreatedCount);
    }

    [Fact]
    public void Build_SumsClosedAndOpenWaitDurations()
    {
        var projects = new[]
        {
            // 已经结束的等待：1 小时
            Project("A", At(2026, 3, 1), accumulatedWaitSeconds: 3600),
            // 正在等待：从 Now 往前 30 分钟，尚未计入累计值
            Project("B", At(2026, 4, 1), status: WorkflowStatus.Waiting, waitReason: "等待试验", waitStartedAt: Now.AddMinutes(-30)),
            // 没有等待记录
            Project("C", At(2026, 5, 1))
        };

        var snapshot = _statistics.Build(projects, StatisticsRange.CurrentYear, Now);

        Assert.Equal(3600 + 1800, snapshot.TotalWaitSeconds);
        Assert.Equal(2, snapshot.ProjectsWithWaitCount);
        Assert.Equal(2700, snapshot.AverageWaitSeconds);
    }

    [Fact]
    public void Build_RanksTagsByProjectUsageAndCapsTopTen()
    {
        var projects = Enumerable.Range(0, 12)
            .Select(index => Project($"P{index}", At(2026, 3, 1), tags: [$"标签{index}", "通用"]))
            .ToArray();

        var snapshot = _statistics.Build(projects, StatisticsRange.CurrentYear, Now);

        Assert.Equal(13, snapshot.TagCount);
        Assert.Equal(10, snapshot.TopTags.Count);
        Assert.Equal("通用", snapshot.TopTags[0].Name);
        Assert.Equal(12, snapshot.TopTags[0].ProjectCount);
        // 其余标签各被 1 个项目使用，按名称稳定排序。
        Assert.All(snapshot.TopTags.Skip(1), item => Assert.Equal(1, item.ProjectCount));
    }

    /// <summary>
    /// 同比：只对“新建项目/完成项目”给出上一年度同区间的真实数量；「全部」没有可比区间时为 null。
    /// </summary>
    [Fact]
    public void Build_ComparesSameWindowOfPreviousYear()
    {
        var projects = new[]
        {
            Project("A", At(2026, 3, 1), status: WorkflowStatus.Completed, started: At(2026, 3, 2), completed: At(2026, 3, 9)),
            Project("B", At(2026, 4, 1), status: WorkflowStatus.Completed, started: At(2026, 4, 2), completed: At(2026, 4, 9)),
            Project("C", At(2025, 5, 1)),
            Project("D", At(2025, 6, 1), status: WorkflowStatus.Completed, started: At(2025, 6, 2), completed: At(2025, 6, 9)),
            // 2024 年的数据不该被算进“同比去年”
            Project("E", At(2024, 5, 1))
        };

        var yearly = _statistics.Build(projects, StatisticsRange.CurrentYear, Now);
        Assert.Equal(2, yearly.NewProjectCount);
        Assert.Equal(2, yearly.NewProjectPreviousCount!.Value);   // 2025 年创建：C、D（E 是 2024 年，不算）
        Assert.Equal(2, yearly.CompletedProjectCount);
        Assert.Equal(1, yearly.CompletedProjectPreviousCount!.Value);

        var month = _statistics.Build(projects, StatisticsRange.CurrentMonth, Now);
        Assert.Equal(0, month.NewProjectCount);
        Assert.Equal(0, month.NewProjectPreviousCount!.Value);    // 2025-09 无项目

        // 「全部」没有可比区间：同比留空，界面不显示。
        var all = _statistics.Build(projects, StatisticsRange.All, Now);
        Assert.Null(all.NewProjectPreviousCount);
        Assert.Null(all.CompletedProjectPreviousCount);
    }

    /// <summary>等待原因分布按等待次数统计（来自状态历史的原因字符串），只算区间内项目。</summary>
    [Fact]
    public void Build_DistributesWaitReasonsByInterval()
    {
        var projects = new[]
        {
            Project("A", At(2026, 3, 1), id: 1),
            Project("B", At(2026, 4, 1), id: 2),
            // 区间外的项目：它的等待原因不应被统计
            Project("C", At(2025, 1, 1), id: 3)
        };
        var reasons = new[]
        {
            new WaitReasonRecord(1, "等待试验"),
            new WaitReasonRecord(1, "等待试验"),
            new WaitReasonRecord(1, "等待CAD模型"),
            new WaitReasonRecord(2, "等待试验"),
            new WaitReasonRecord(3, "等待评审")
        };

        var snapshot = _statistics.Build(projects, StatisticsRange.CurrentYear, Now, reasons);

        Assert.Equal(4, snapshot.WaitIntervalCount);
        Assert.Equal("等待试验", snapshot.WaitReasons[0].Reason);
        Assert.Equal(3, snapshot.WaitReasons[0].IntervalCount);
        Assert.Equal(75d, snapshot.WaitReasons[0].Percentage, 3);
        Assert.Equal("等待CAD模型", snapshot.WaitReasons[1].Reason);
        Assert.Equal(25d, snapshot.WaitReasons[1].Percentage, 3);
        // 没有等待记录时给出空列表而不是抛异常
        Assert.Empty(_statistics.Build(projects, StatisticsRange.CurrentYear, Now).WaitReasons);
    }

    /// <summary>最近完成的项目：按完成时间倒序、最多 3 条、缺开始时间时周期留空。</summary>
    [Fact]
    public void Build_ListsRecentCompletedProjects()
    {
        var projects = new[]
        {
            Project("A", At(2026, 1, 1), status: WorkflowStatus.Completed, started: At(2026, 1, 1), completed: At(2026, 1, 5)),
            Project("B", At(2026, 2, 1), SimulationType.Structural, requester: "李工", software: ["Adams"],
                status: WorkflowStatus.Completed, started: At(2026, 2, 1), completed: At(2026, 2, 20)),
            Project("C", At(2026, 3, 1), status: WorkflowStatus.Completed, completed: At(2026, 3, 8)),
            Project("D", At(2026, 4, 1), status: WorkflowStatus.Completed, started: At(2026, 4, 1), completed: At(2026, 4, 2))
        };

        var snapshot = _statistics.Build(projects, StatisticsRange.CurrentYear, Now);

        // 只保留最近 3 条，按完成时间倒序
        Assert.Equal(["D", "C", "B"], snapshot.RecentCompleted.Select(item => item.ProjectCode));
        Assert.Equal(1d, snapshot.RecentCompleted[0].CycleDays!.Value, 3);
        // C 没有开始时间：周期留空（不拿创建时间顶替）
        Assert.Null(snapshot.RecentCompleted.Single(item => item.ProjectCode == "C").CycleDays);

        var mapped = snapshot.RecentCompleted.Single(item => item.ProjectCode == "B");
        Assert.Equal(SimulationType.Structural, mapped.SimulationType);
        Assert.Equal("李工", mapped.Requester);
        Assert.Equal(["Adams"], mapped.Software);
        // 更早的 A 被挤出去
        Assert.DoesNotContain(snapshot.RecentCompleted, item => item.ProjectCode == "A");
    }

    [Fact]
    public void ChartScale_RoundsMaximumUpToMultipleOfFour()
    {
        Assert.Equal(4, ChartScale.NiceMaximum(0));
        Assert.Equal(4, ChartScale.NiceMaximum(1));
        Assert.Equal(4, ChartScale.NiceMaximum(4));
        Assert.Equal(8, ChartScale.NiceMaximum(5));
        Assert.Equal(12, ChartScale.NiceMaximum(12));
        Assert.Equal(16, ChartScale.NiceMaximum(13));

        // 固定 5 条刻度、整数、从上限递减到 0
        Assert.Equal([8, 6, 4, 2, 0], ChartScale.Ticks(5));
        Assert.Equal([4, 3, 2, 1, 0], ChartScale.Ticks(1));
        Assert.Equal([12, 9, 6, 3, 0], ChartScale.Ticks(12));
        Assert.Equal(ChartScale.TickCount, ChartScale.Ticks(100).Count);
        Assert.Equal(0, ChartScale.Ticks(100)[^1]);
    }

    [Fact]
    public void Build_ExposesWindowForCurrentRange()
    {
        var localNow = Now.ToLocalTime();
        var yearly = _statistics.Build([], StatisticsRange.CurrentYear, Now);
        Assert.NotNull(yearly.WindowStart);
        Assert.NotNull(yearly.WindowEnd);
        // 窗口按本地时间给出：今年 1 月 1 日 ~ 明年 1 月 1 日（不含）
        Assert.Equal(new DateTimeOffset(localNow.Year, 1, 1, 0, 0, 0, localNow.Offset), yearly.WindowStart);
        Assert.Equal(new DateTimeOffset(localNow.Year + 1, 1, 1, 0, 0, 0, localNow.Offset), yearly.WindowEnd);

        var month = _statistics.Build([], StatisticsRange.CurrentMonth, Now);
        Assert.Equal(localNow.Month, month.WindowStart!.Value.Month);
        Assert.Equal(1, month.WindowStart.Value.Day);

        var all = _statistics.Build([], StatisticsRange.All, Now);
        Assert.Null(all.WindowStart);
        Assert.Null(all.WindowEnd);
    }

    [Fact]
    public void Build_WithNoProjectsReturnsEmptySnapshot()
    {
        var snapshot = _statistics.Build([], StatisticsRange.CurrentYear, Now);

        Assert.Equal(0, snapshot.ProjectCountInRange);
        Assert.Equal(0, snapshot.NewProjectCount);
        Assert.Equal(0, snapshot.CompletedProjectCount);
        Assert.Empty(snapshot.SoftwareUsage);
        Assert.Empty(snapshot.Requesters);
        Assert.Empty(snapshot.TopTags);
        // 固定区间下月份刻度照常给出（只是全为 0），这样坐标轴不会因为没数据而消失。
        Assert.Equal(12, snapshot.MonthlyTrend.Count);
        Assert.All(snapshot.MonthlyTrend, point => Assert.Equal(0, point.CreatedCount));
        // “全部”没有起点可言，因此不产出刻度。
        Assert.Empty(_statistics.Build([], StatisticsRange.All, Now).MonthlyTrend);
        Assert.Equal(0, snapshot.CycleSampleCount);
        Assert.Equal(0, snapshot.TotalWaitSeconds);
        // 类型图例仍然给出 8 行（全部为 0），界面不需要为空数据做特殊分支。
        Assert.Equal(SimulationTypes.All.Count + 1, snapshot.SimulationTypes.Count);
        Assert.All(snapshot.SimulationTypes, item => Assert.Equal(0, item.ProjectCount));
        Assert.All(snapshot.SimulationTypes, item => Assert.Equal(0d, item.Percentage));
    }
}
