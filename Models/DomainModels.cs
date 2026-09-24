using System.Text.Json.Serialization;

namespace SimFlow.Models;

[JsonConverter(typeof(JsonStringEnumConverter<WorkflowStatus>))]
public enum WorkflowStatus
{
    NotStarted,
    Active,
    Waiting,
    Completed
}

[JsonConverter(typeof(JsonStringEnumConverter<StorageStatus>))]
public enum StorageStatus
{
    Working,
    Archived
}

[JsonConverter(typeof(JsonStringEnumConverter<StorageLocationCode>))]
public enum StorageLocationCode
{
    LocalWork,
    WorkstationWork,
    WorkstationArchive
}

/// <summary>
/// 项目的仿真类型（单选）。
/// 旧项目数据库与 <c>project.json</c> 里没有这个字段，因此模型上允许为空，界面显示为“未分类”。
/// 注意：塑壳断路器、框架断路器、操动机构等产品信息继续用标签表达，不是正式字段。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SimulationType>))]
public enum SimulationType
{
    Dynamics,
    Electromagnetics,
    Structural,
    Fatigue,
    ThermalFluid,
    Multiphysics,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferState>))]
public enum TransferState
{
    Pending,
    Copying,
    Verifying,
    Switched,
    CleanupPending,
    Completed,
    Cancelled,
    Failed,
    Abandoned
}

public enum ProjectSortMode
{
    RecentlyUpdated,
    RecentlyCreated,
    Name
}

public enum ScanConflictResolution
{
    KeepDatabase,
    UseMetadata
}

/// <summary>
/// 仿真类型的固定顺序与显示文案。下拉框候选、统计图例顺序都取这里，避免两处各写一份。
/// </summary>
public static class SimulationTypes
{
    /// <summary>未分类（旧项目缺字段或用户没有选择）的显示文案。</summary>
    public const string Unclassified = "未分类";

    /// <summary>第一版默认类型，顺序即下拉框与统计图例顺序（未分类排在最后）。</summary>
    public static IReadOnlyList<SimulationType> All { get; } =
    [
        SimulationType.Dynamics,
        SimulationType.Electromagnetics,
        SimulationType.Structural,
        SimulationType.Fatigue,
        SimulationType.ThermalFluid,
        SimulationType.Multiphysics,
        SimulationType.Other
    ];

    public static string Describe(SimulationType type) => type switch
    {
        SimulationType.Dynamics => "动力学仿真",
        SimulationType.Electromagnetics => "电磁仿真",
        SimulationType.Structural => "结构仿真",
        SimulationType.Fatigue => "疲劳仿真",
        SimulationType.ThermalFluid => "热流体仿真",
        SimulationType.Multiphysics => "多物理场仿真",
        SimulationType.Other => "其他",
        _ => Unclassified
    };

    /// <summary>可空类型统一走这里：为空表示未分类。</summary>
    public static string Describe(SimulationType? type) => type.HasValue ? Describe(type.Value) : Unclassified;

    /// <summary>类型在 <see cref="All"/> 中的序号；未知值返回 -1。</summary>
    public static int IndexOf(SimulationType type)
    {
        for (var index = 0; index < All.Count; index++)
        {
            if (All[index] == type)
            {
                return index;
            }
        }

        return -1;
    }
}

public sealed class ProjectRecord
{
    public long Id { get; set; }
    public required string ProjectCode { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Requester { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public WorkflowStatus WorkflowStatus { get; set; }
    public StorageStatus StorageStatus { get; set; } = StorageStatus.Working;
    public StorageLocationCode StorageLocation { get; set; } = StorageLocationCode.LocalWork;
    /// <summary>仿真类型；为空表示未分类（旧项目默认如此）。</summary>
    public SimulationType? SimulationType { get; set; }
    public required string RelativePath { get; set; }
    public string? WaitReason { get; set; }
    public string? WaitNote { get; set; }
    public DateTimeOffset? WaitStartedAt { get; set; }
    public bool IsFavorite { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string CurrentVersion { get; set; } = "V001";
    public long AccumulatedWaitSeconds { get; set; }
    public string MetadataSyncState { get; set; } = "Synced";
    public string CoverImage { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public List<string> Software { get; set; } = [];
}

public sealed class ProjectVersionRecord
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public required string VersionNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ChangeSummary { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string RelativePath { get; set; }
}

public sealed class ProjectActivityRecord
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public required string ActivityType { get; set; }
    public required string Description { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 状态历史的一行表示项目在 <see cref="FromStatus"/> 这一段停留的区间：
/// <see cref="StartedAt"/> 是该状态开始的时间，<see cref="EndedAt"/> 是它结束的时间。
/// 仍然处于当前状态的那一行 <see cref="EndedAt"/> 为 null。
/// 创建项目写入的第一行 <see cref="FromStatus"/> 为 null。
/// </summary>
public sealed class ProjectStatusHistoryRecord
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public WorkflowStatus? FromStatus { get; set; }
    public WorkflowStatus ToStatus { get; set; }
    public string? Reason { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long? DurationSeconds { get; set; }
}

public sealed class CreateProjectRequest
{
    public required string Name { get; init; }
    /// <summary>仿真类型；不是必填项，为空表示未分类。</summary>
    public SimulationType? SimulationType { get; init; }
    public string Requester { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Software { get; init; } = [];
    public StorageLocationCode InitialLocation { get; init; } = StorageLocationCode.LocalWork;
    public bool StartImmediately { get; init; } = true;
    public bool IsFavorite { get; init; }
}

public sealed class UpdateProjectInfoRequest
{
    public required string Name { get; init; }
    public SimulationType? SimulationType { get; init; }
    public string Requester { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Software { get; init; } = [];
}

public sealed class ProjectMetadataDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Requester { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public WorkflowStatus WorkflowStatus { get; init; }
    public StorageStatus StorageStatus { get; init; }
    public StorageLocationCode StorageLocation { get; init; }
    /// <summary>
    /// 仿真类型。旧档案里没有这个字段，反序列化后保持 null（= 未分类），
    /// 因此扫描旧 <c>project.json</c> 不需要额外兼容分支。
    /// </summary>
    public SimulationType? SimulationType { get; init; }
    public required string RelativePath { get; init; }
    public string? WaitReason { get; init; }
    public string? WaitNote { get; init; }
    public DateTimeOffset? WaitStartedAt { get; init; }
    public long AccumulatedWaitSeconds { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool Favorite { get; init; }
    public IReadOnlyList<string> Software { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string CurrentVersion { get; init; } = "V001";
    /// <summary>项目目录内的封面图相对路径，例如 <c>cover.png</c>；未设置时为空。</summary>
    public string CoverImage { get; init; } = string.Empty;

    public static ProjectMetadataDocument FromProject(ProjectRecord project) => new()
    {
        Id = project.ProjectCode,
        Name = project.Name,
        Description = project.Description,
        Requester = project.Requester,
        Notes = project.Notes,
        WorkflowStatus = project.WorkflowStatus,
        StorageStatus = project.StorageStatus,
        StorageLocation = project.StorageLocation,
        SimulationType = project.SimulationType,
        RelativePath = project.RelativePath,
        WaitReason = project.WaitReason,
        WaitNote = project.WaitNote,
        WaitStartedAt = project.WaitStartedAt,
        AccumulatedWaitSeconds = project.AccumulatedWaitSeconds,
        CreatedAt = project.CreatedAt,
        StartedAt = project.StartedAt,
        CompletedAt = project.CompletedAt,
        ArchivedAt = project.ArchivedAt,
        UpdatedAt = project.UpdatedAt,
        Favorite = project.IsFavorite,
        Software = project.Software,
        Tags = project.Tags,
        CurrentVersion = project.CurrentVersion,
        CoverImage = project.CoverImage
    };

    public ProjectRecord ToProject() => new()
    {
        ProjectCode = Id,
        Name = Name,
        Description = Description,
        Requester = Requester,
        Notes = Notes,
        WorkflowStatus = WorkflowStatus,
        StorageStatus = StorageStatus,
        StorageLocation = StorageLocation,
        SimulationType = SimulationType,
        RelativePath = string.IsNullOrWhiteSpace(RelativePath) ? Id : RelativePath,
        WaitReason = WaitReason,
        WaitNote = WaitNote,
        WaitStartedAt = WaitStartedAt,
        AccumulatedWaitSeconds = AccumulatedWaitSeconds,
        CreatedAt = CreatedAt,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        ArchivedAt = ArchivedAt,
        UpdatedAt = UpdatedAt,
        IsFavorite = Favorite,
        Software = Software.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Tags = Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        CurrentVersion = CurrentVersion,
        CoverImage = CoverImage,
        MetadataSyncState = "Synced"
    };
}

public sealed class VersionMetadataDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required string VersionNumber { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ChangeSummary { get; init; } = string.Empty;
    public string Status { get; init; } = "Active";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed class AppConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string LocalWorkRoot { get; set; } = string.Empty;
    public string WorkstationWorkRoot { get; set; } = string.Empty;
    public string WorkstationArchiveRoot { get; set; } = string.Empty;
    /// <summary>项目卡片/详情未设置封面时使用的自定义占位图；为空时使用内置 ProjectPreview.png。</summary>
    public string ProjectPlaceholderImage { get; set; } = string.Empty;
    /// <summary>仿真报告模板文件；创建报告时复制到项目的 Delivery 目录。</summary>
    public string SimulationReportTemplatePath { get; set; } = string.Empty;
    /// <summary>归档副本完成校验并切换为主副本后，是否删除原 Work 目录。</summary>
    public bool DeleteSourceAfterArchive { get; set; } = true;
    public bool CheckWorkstationOnStartup { get; set; } = true;
    public List<SoftwareConfiguration> Software { get; set; } =
    [
        new() { Name = "Adams" },
        new() { Name = "ANSYS Mechanical" },
        new() { Name = "ANSYS Maxwell" },
        new() { Name = "nCode" },
        new() { Name = "SolidWorks" },
        new() { Name = "SpaceClaim" },
        new() { Name = "COMSOL" }
    ];
}

public sealed class SoftwareConfiguration
{
    public required string Name { get; set; }

    /// <summary>图标墙瓷砖上显示的首字母；空名称回退为 "?"。</summary>
    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

}

public sealed class TransferOperationRecord
{
    public required string Id { get; init; }
    public long ProjectId { get; init; }
    public StorageLocationCode SourceLocation { get; init; }
    public StorageLocationCode TargetLocation { get; init; }
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public TransferState State { get; set; }
    public long TotalBytes { get; set; }
    public long ProcessedBytes { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record MigrationProgress(
    TransferState State,
    string Message,
    string? CurrentFile,
    long ProcessedBytes,
    long TotalBytes)
{
    public double Percent => TotalBytes <= 0 ? 0 : Math.Clamp(ProcessedBytes * 100d / TotalBytes, 0, 100);
}

public sealed record ScanConflict(
    string ProjectCode,
    string MetadataPath,
    string Reason,
    ProjectRecord DatabaseProject,
    ProjectMetadataDocument Metadata,
    StorageLocationCode DiscoveredLocation,
    string RelativePath);

/// <summary>扫描发现的“版本记录 ↔ 版本目录”不一致方向。</summary>
public enum ScanVersionIssueKind
{
    /// <summary>数据库里有版本记录，但 <c>Versions\Vxxx</c> 目录已不存在。</summary>
    MissingFolder,

    /// <summary>磁盘上有 <c>Versions\Vxxx</c> 目录，但数据库里没有对应记录。</summary>
    UnrecordedFolder
}

/// <summary>
/// 扫描发现的版本一致性问题。扫描本身不改任何数据，两个方向都只能由
/// <c>IProjectScannerService.ResolveVersionIssueAsync</c> 在用户确认后处理。
/// </summary>
/// <param name="CanResolve">
/// 是否允许处理。唯一版本记录缺目录时为 false：删掉它会让项目没有任何版本，
/// 这种只能人工确认磁盘内容后处理。
/// </param>
public sealed record ScanVersionIssue(
    ScanVersionIssueKind Kind,
    string ProjectCode,
    string ProjectName,
    string VersionNumber,
    string Title,
    string DirectoryPath,
    string Detail,
    bool CanResolve);

/// <summary>
/// 版本号工具（V001 ↔ 1）。版本号生成、删除后的“当前版本”回退、扫描对账都用同一份解析，
/// 避免出现两处算法不一致。
/// </summary>
public static class VersionNumbers
{
    /// <summary>解析版本号；无法解析时返回 0。</summary>
    public static int Parse(string versionNumber)
        => int.TryParse(versionNumber.TrimStart('V', 'v'), out var number) ? number : 0;

    /// <summary>目录名是否是一个版本目录（V + 纯数字，例如 V001）。</summary>
    public static bool IsFolderName(string name)
        => name.Length > 1 && (name[0] == 'V' || name[0] == 'v') && name[1..].All(char.IsDigit);

    /// <summary>返回最大的版本号；序列为空时返回 null。</summary>
    public static string? Highest(IEnumerable<string> versionNumbers)
    {
        string? best = null;
        foreach (var candidate in versionNumbers)
        {
            if (best is null
                || Parse(candidate) > Parse(best)
                || (Parse(candidate) == Parse(best) && string.CompareOrdinal(candidate, best) > 0))
            {
                best = candidate;
            }
        }

        return best;
    }
}

/// <summary>标签汇总：名称与当前使用项目数（0 表示没有项目在使用）。</summary>
public sealed record TagSummaryRecord(string Name, int Count);

/// <summary>一条等待区间的原因（来自状态历史，含仍在等待中的那次）。</summary>
public sealed record WaitReasonRecord(long ProjectId, string Reason);

/// <summary>统计分析页顶部的时间范围。</summary>
public enum StatisticsRange
{
    CurrentMonth,
    LastThreeMonths,
    CurrentYear,
    All
}

/// <summary>仿真类型分布的一项；<see cref="Type"/> 为 null 表示未分类。</summary>
public sealed record SimulationTypeStatistic(SimulationType? Type, string DisplayName, int ProjectCount, double Percentage);

/// <summary>月度（或跨度过长时按年聚合）的一个数据点。</summary>
public sealed record MonthlyTrendPoint(string Label, int CreatedCount, int CompletedCount);

/// <summary>某个软件被多少个项目使用；一个项目可用多个软件，因此百分比之和可以超过 100%。</summary>
public sealed record SoftwareUsageStatistic(string Name, int ProjectCount, double Percentage);

/// <summary>需求人排行榜的一行；周期无法计算时为 null。</summary>
public sealed record RequesterStatistic(string Name, int ProjectCount, int CompletedCount, int ActiveCount, double? AverageCycleDays);

/// <summary>项目周期分布的一个区间。</summary>
public sealed record CycleDistributionBucket(string Label, int ProjectCount);

/// <summary>标签使用频率的一项：被多少个项目使用。</summary>
public sealed record TagUsageStatistic(string Name, int ProjectCount);

/// <summary>
/// 等待原因分布的一项。<see cref="IntervalCount"/> 是等待“次数”（每次进入等待算一次），
/// 因此占比之和为 100%，与“有等待记录的项目数”不是同一口径。
/// </summary>
public sealed record WaitReasonStatistic(string Reason, int IntervalCount, double Percentage);

/// <summary>
/// 最近完成的项目行（统计页底部卡片）。周期无法计算时为 null（缺开始时间），
/// 不拿创建时间顶替。
/// </summary>
public sealed record RecentCompletedProject(
    string ProjectCode,
    string Name,
    SimulationType? SimulationType,
    string Requester,
    IReadOnlyList<string> Software,
    DateTimeOffset CompletedAt,
    double? CycleDays);

/// <summary>柱状图刻度计算：把最大值抬到 4 的倍数，保证 5 个刻度都是整数。</summary>
public static class ChartScale
{
    /// <summary>最少刻度上限，避免最大值很小时刻度挤在 0/1 两格。</summary>
    public const int MinimumMaximum = 4;

    /// <summary>刻度条数（含 0）。</summary>
    public const int TickCount = 5;

    public static int NiceMaximum(int maxCount)
    {
        var clamped = Math.Max(0, maxCount);
        return Math.Max(MinimumMaximum, ((clamped + 3) / 4) * 4);
    }

    /// <summary>从上限到 0 的刻度值（含两端）。</summary>
    public static IReadOnlyList<int> Ticks(int maxCount)
    {
        var maximum = NiceMaximum(maxCount);
        var step = maximum / (TickCount - 1);
        return Enumerable.Range(0, TickCount).Select(index => maximum - (index * step)).ToList();
    }
}

/// <summary>
/// 统计分析的一次计算结果。所有数字都来自真实项目数据，界面只负责展示。
/// 统计口径见 <c>StatisticsService</c>：项目数量类按创建时间、完成类按完成时间、
/// 进行中/等待中/已归档是当前快照。
/// </summary>
public sealed class StatisticsSnapshot
{
    /// <summary>所选时间区间内（按创建时间）的项目数，也是各占比的分母。</summary>
    public int ProjectCountInRange { get; init; }

    /// <summary>区间起点（本地时间）；“全部”为 null。页面用它展示实际时间窗口。</summary>
    public DateTimeOffset? WindowStart { get; init; }

    /// <summary>区间终点（本地时间，不含）；“全部”为 null。</summary>
    public DateTimeOffset? WindowEnd { get; init; }
    public int NewProjectCount { get; init; }
    public int CompletedProjectCount { get; init; }
    /// <summary>当前快照：全部未归档项目里处于进行中的数量，不随时间范围变化。</summary>
    public int ActiveProjectCount { get; init; }
    public int WaitingProjectCount { get; init; }
    public int ArchivedProjectCount { get; init; }
    public IReadOnlyList<SimulationTypeStatistic> SimulationTypes { get; init; } = [];
    public IReadOnlyList<MonthlyTrendPoint> MonthlyTrend { get; init; } = [];
    /// <summary>趋势图是否按年聚合（全部范围跨度过长时）。</summary>
    public bool TrendAggregatedByYear { get; init; }
    public IReadOnlyList<SoftwareUsageStatistic> SoftwareUsage { get; init; } = [];
    public IReadOnlyList<RequesterStatistic> Requesters { get; init; } = [];
    /// <summary>全部需求人数量（不含“未填写”），界面只展示前若干行。</summary>
    public int RequesterCount { get; init; }
    public IReadOnlyList<CycleDistributionBucket> CycleDistribution { get; init; } = [];
    /// <summary>能够正确计算周期的已完成项目数（开始时间与完成时间都齐全）。</summary>
    public int CycleSampleCount { get; init; }
    /// <summary>区间内已完成项目数，可能大于 <see cref="CycleSampleCount"/>。</summary>
    public int CompletedInRangeCount { get; init; }
    public long TotalWaitSeconds { get; init; }
    public long AverageWaitSeconds { get; init; }
    public int ProjectsWithWaitCount { get; init; }
    public IReadOnlyList<TagUsageStatistic> TopTags { get; init; } = [];
    /// <summary>区间内出现过的不同标签数量。</summary>
    public int TagCount { get; init; }

    /// <summary>等待原因分布（按等待次数），只统计区间内项目。</summary>
    public IReadOnlyList<WaitReasonStatistic> WaitReasons { get; init; } = [];
    /// <summary>区间内等待区间总数（分母）。</summary>
    public int WaitIntervalCount { get; init; }

    /// <summary>最近完成的项目（按完成时间倒序，界面只展示前几行）。</summary>
    public IReadOnlyList<RecentCompletedProject> RecentCompleted { get; init; } = [];

    /// <summary>
    /// 上一个年度同区间的“新建项目”数量（同比用）。
    /// 范围为“全部”时没有可比区间，为 null——界面此时不显示同比。
    /// </summary>
    public int? NewProjectPreviousCount { get; init; }

    /// <summary>上一个年度同区间的“完成项目”数量（同比用）。</summary>
    public int? CompletedProjectPreviousCount { get; init; }
}

public sealed class ScanResult
{
    public int ScannedFiles { get; set; }
    public int ImportedProjects { get; set; }
    public int UnchangedProjects { get; set; }
    public List<ScanConflict> Conflicts { get; } = [];
    /// <summary>版本记录与版本目录不一致的条目（需要人工确认后才处理）。</summary>
    public List<ScanVersionIssue> VersionIssues { get; } = [];
    public List<string> Errors { get; } = [];
}

public static class ActivityTypes
{
    public const string ProjectCreated = "PROJECT_CREATED";
    public const string ProjectStarted = "PROJECT_STARTED";
    public const string StatusChanged = "STATUS_CHANGED";
    public const string WaitStarted = "WAIT_STARTED";
    public const string WaitEnded = "WAIT_ENDED";
    public const string VersionCreated = "VERSION_CREATED";
    public const string VersionDeleted = "VERSION_DELETED";
    public const string ProjectMoved = "PROJECT_MOVED";
    public const string ProjectArchived = "PROJECT_ARCHIVED";
    public const string ProjectRestored = "PROJECT_RESTORED";
    public const string FavoriteChanged = "FAVORITE_CHANGED";
    public const string NoteUpdated = "NOTE_UPDATED";
    public const string ProjectUpdated = "PROJECT_UPDATED";
    public const string CoverChanged = "COVER_CHANGED";
    public const string TagsReorganized = "TAGS_REORGANIZED";
    public const string ProjectImported = "PROJECT_IMPORTED";
}

/// <summary>
/// 业务状态转换规则。服务层和界面（看板拖拽）共用同一份判断，避免两处规则不一致。
/// </summary>
public static class WorkflowRules
{
    /// <summary>四种业务状态允许互相纠正；选择当前状态不构成有效转换。</summary>
    public static bool CanTransition(WorkflowStatus from, WorkflowStatus to) => from != to;

    public static bool RequiresWaitReason(WorkflowStatus to) => to == WorkflowStatus.Waiting;

    public static string Describe(WorkflowStatus status) => status switch
    {
        WorkflowStatus.NotStarted => "未开始",
        WorkflowStatus.Active => "进行中",
        WorkflowStatus.Waiting => "等待中",
        WorkflowStatus.Completed => "已完成",
        _ => "未知"
    };
}
