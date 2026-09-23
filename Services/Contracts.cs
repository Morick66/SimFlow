using SimFlow.Models;

namespace SimFlow.Services;

public interface IConfigurationService
{
    AppConfiguration Current { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface IProjectRepository
{
    Task<IReadOnlyList<ProjectRecord>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ProjectRecord?> GetByIdAsync(long projectId, CancellationToken cancellationToken = default);
    Task<ProjectRecord?> GetByCodeAsync(string projectCode, CancellationToken cancellationToken = default);
    Task<string> GenerateNextProjectCodeAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<ProjectRecord> CreateAsync(ProjectRecord project, ProjectVersionRecord initialVersion, ProjectActivityRecord activity, CancellationToken cancellationToken = default);
    Task SaveAsync(ProjectRecord project, ProjectActivityRecord activity, WorkflowStatus? previousStatus = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectVersionRecord>> GetVersionsAsync(long projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectActivityRecord>> GetActivitiesAsync(long projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectStatusHistoryRecord>> GetStatusHistoryAsync(long projectId, CancellationToken cancellationToken = default);
    Task AddVersionAsync(ProjectRecord project, ProjectVersionRecord version, ProjectActivityRecord activity, CancellationToken cancellationToken = default);
    /// <summary>在同一写事务中检查保留版本、执行删除前检查/目录操作、删除记录并回退当前版本，记录活动及待同步状态。</summary>
    Task DeleteVersionAsync(long projectId, long versionId, string description, Action<ProjectRecord>? beforeDelete = null, CancellationToken cancellationToken = default);
    Task MarkMetadataSyncedAsync(long projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectRecord>> GetPendingMetadataSyncAsync(CancellationToken cancellationToken = default);
    Task<bool> ImportAsync(ProjectRecord project, ProjectActivityRecord activity, CancellationToken cancellationToken = default);
    Task SaveTransferAsync(TransferOperationRecord transfer, CancellationToken cancellationToken = default);
    Task UpdateTransferAsync(TransferOperationRecord transfer, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransferOperationRecord>> GetIncompleteTransfersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TagSummaryRecord>> GetTagSummaryAsync(CancellationToken cancellationToken = default);
    /// <summary>所有等待区间的原因（来自状态历史，含仍在等待中的那次）；统计页据此做等待原因分布。</summary>
    Task<IReadOnlyList<WaitReasonRecord>> GetWaitReasonsAsync(CancellationToken cancellationToken = default);
    /// <summary>重命名或合并标签，返回受影响的 ProjectId 列表（供上层同步项目档案）。</summary>
    Task<IReadOnlyList<long>> RenameTagAsync(string oldName, string newName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<long>> MergeTagsAsync(string sourceName, string targetName, CancellationToken cancellationToken = default);
    Task DeleteTagAsync(string name, CancellationToken cancellationToken = default);
    /// <summary>删除项目记录；依赖外键级联清理版本、活动、状态历史、关联标签/软件与迁移记录。</summary>
    Task DeleteAsync(long projectId, CancellationToken cancellationToken = default);
}

public interface IProjectMetadataStore
{
    Task WriteProjectAsync(string projectDirectory, ProjectMetadataDocument metadata, CancellationToken cancellationToken = default);
    Task<ProjectMetadataDocument?> ReadProjectAsync(string metadataPath, CancellationToken cancellationToken = default);
    Task WriteVersionAsync(string versionDirectory, VersionMetadataDocument metadata, CancellationToken cancellationToken = default);
    /// <summary>读取 <c>version.json</c>；文件缺失或损坏时返回 null（扫描补建版本记录时用）。</summary>
    Task<VersionMetadataDocument?> ReadVersionAsync(string metadataPath, CancellationToken cancellationToken = default);
}

public interface IStorageLocationService
{
    string ResolveRoot(StorageLocationCode location);
    string ResolveProjectPath(ProjectRecord project);
    Task<bool> IsOnlineAsync(StorageLocationCode location, CancellationToken cancellationToken = default);
    Task<long?> GetAvailableBytesAsync(StorageLocationCode location, CancellationToken cancellationToken = default);
}

public interface IProjectService
{
    Task<IReadOnlyList<ProjectRecord>> GetProjectsAsync(CancellationToken cancellationToken = default);
    Task<ProjectRecord> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default);
    Task<ProjectRecord> ChangeStatusAsync(ProjectRecord project, WorkflowStatus targetStatus, string? reason = null, string? note = null, CancellationToken cancellationToken = default);
    Task<ProjectRecord> ToggleFavoriteAsync(ProjectRecord project, CancellationToken cancellationToken = default);
    Task<ProjectRecord> UpdateInfoAsync(ProjectRecord project, UpdateProjectInfoRequest request, CancellationToken cancellationToken = default);
    Task<ProjectRecord> SetCoverImageAsync(ProjectRecord project, string sourcePath, CancellationToken cancellationToken = default);
    Task<ProjectRecord> ClearCoverImageAsync(ProjectRecord project, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TagSummaryRecord>> GetTagSummaryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WaitReasonRecord>> GetWaitReasonsAsync(CancellationToken cancellationToken = default);
    Task RenameTagAsync(string oldName, string newName, CancellationToken cancellationToken = default);
    Task MergeTagsAsync(string sourceName, string targetName, CancellationToken cancellationToken = default);
    Task DeleteUnusedTagAsync(string name, CancellationToken cancellationToken = default);
    Task<ProjectRecord> UpdateNotesAsync(ProjectRecord project, string notes, CancellationToken cancellationToken = default);
    Task<ProjectVersionRecord> CreateVersionAsync(ProjectRecord project, string title, string changeSummary, CancellationToken cancellationToken = default);
    /// <summary>
    /// 删除版本：可选把 <c>Versions\Vxxx</c> 目录移动到项目恢复区。删的是当前版本时，
    /// 当前版本回退到剩余的最高版本；项目至少要保留一个版本。
    /// </summary>
    Task<ProjectRecord> DeleteVersionAsync(ProjectRecord project, ProjectVersionRecord version, bool deleteDirectory, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectVersionRecord>> GetVersionsAsync(ProjectRecord project, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectActivityRecord>> GetActivitiesAsync(ProjectRecord project, CancellationToken cancellationToken = default);
    Task RetryPendingMetadataAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// 删除项目。deleteDirectory 为 true 时先把项目文件夹移动到 Work 根恢复区；
    /// 数据库删除失败时恢复原目录，不执行不可恢复的物理删除。
    /// </summary>
    Task DeleteAsync(ProjectRecord project, bool deleteDirectory, CancellationToken cancellationToken = default);
}

public interface IProjectMigrationService
{
    Task<TransferOperationRecord> MigrateAsync(ProjectRecord project, StorageLocationCode target, IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<TransferOperationRecord> RetryAsync(TransferOperationRecord transfer, IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task AbandonAsync(TransferOperationRecord transfer, CancellationToken cancellationToken = default);
    Task CleanupSourceAsync(TransferOperationRecord transfer, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransferOperationRecord>> GetIncompleteAsync(CancellationToken cancellationToken = default);
}

public sealed record PreparedSimulationReport(string Path, bool Created);

public interface ISimulationReportService
{
    /// <summary>
    /// 打开 Delivery 中唯一的 Word 报告；没有报告时从设置中的模板安全创建；多个报告时拒绝猜测。
    /// </summary>
    Task<PreparedSimulationReport> PrepareAsync(ProjectRecord project, CancellationToken cancellationToken = default);
}

public interface IProjectScannerService
{
    Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default);
    Task ResolveConflictAsync(ScanConflict conflict, ScanConflictResolution resolution, CancellationToken cancellationToken = default);
    /// <summary>
    /// 处理扫描发现的版本一致性问题（用户确认后调用）：
    /// 缺目录 → 删除版本记录（磁盘上本来就没有文件，不会删任何文件）；有目录无记录 → 按目录补建记录。
    /// </summary>
    Task ResolveVersionIssueAsync(ScanVersionIssue issue, CancellationToken cancellationToken = default);
}

/// <summary>
/// 统计分析：对项目列表做聚合，产出统计页需要的全部数字。
/// 输入是领域对象列表（而不是自己查库），因此可以脱离数据库单独测试；
/// 口径见实现 <see cref="StatisticsService"/>。
/// </summary>
public interface IStatisticsService
{
    StatisticsSnapshot Build(
        IReadOnlyList<ProjectRecord> projects,
        StatisticsRange range,
        DateTimeOffset now,
        IReadOnlyList<WaitReasonRecord>? waitReasons = null);
}
