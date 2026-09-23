using Microsoft.Data.Sqlite;
using SimFlow.Models;
using System.Data;

namespace SimFlow.Services;

public sealed class ProjectRepository(DatabaseService database) : IProjectRepository
{
    public async Task<IReadOnlyList<ProjectRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var projects = new List<ProjectRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM Projects ORDER BY UpdatedAtUtc DESC;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                projects.Add(ReadProject(reader));
            }
        }

        if (projects.Count == 0)
        {
            return projects;
        }

        var byId = projects.ToDictionary(project => project.Id);
        await LoadNamesAsync(connection, """
            SELECT pt.ProjectId, t.Name
            FROM ProjectTags pt JOIN Tags t ON t.Id = pt.TagId;
            """, byId, static project => project.Tags, cancellationToken);
        await LoadNamesAsync(connection, """
            SELECT ps.ProjectId, s.Name
            FROM ProjectSoftware ps JOIN Software s ON s.Id = ps.SoftwareId;
            """, byId, static project => project.Software, cancellationToken);
        return projects;
    }

    public async Task<ProjectRecord?> GetByIdAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        ProjectRecord? project = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM Projects WHERE Id=$id LIMIT 1;";
            command.Parameters.AddWithValue("$id", projectId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                project = ReadProject(reader);
            }
        }

        if (project is null)
        {
            return null;
        }

        await LoadProjectNamesAsync(connection, project, cancellationToken);
        return project;
    }

    public async Task<ProjectRecord?> GetByCodeAsync(string projectCode, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        ProjectRecord? project = null;
        await using (var command = connection.CreateCommand())
        {
            // ProjectCode 列使用默认的 BINARY 排序规则，这里显式加 COLLATE NOCASE 以保持
            // 原先内存比较时的 OrdinalIgnoreCase 行为。
            command.CommandText = "SELECT * FROM Projects WHERE ProjectCode = $code COLLATE NOCASE LIMIT 1;";
            command.Parameters.AddWithValue("$code", projectCode);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                project = ReadProject(reader);
            }
        }

        if (project is null)
        {
            return null;
        }

        await LoadProjectNamesAsync(connection, project, cancellationToken);
        return project;
    }

    public async Task<string> GenerateNextProjectCodeAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var prefix = $"SIM_{now:yyyyMMdd}_";
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        // BEGIN IMMEDIATE 立即拿写锁（Microsoft.Data.Sqlite 用 IsolationLevel.Serializable 触发 BEGIN IMMEDIATE）：
        // 并发创建时多次调用会串行读取最大值，避免多个调用读到同一个最大序号而生成重复编号（Project ID 并发）。
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(IsolationLevel.Serializable);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProjectCode FROM Projects WHERE ProjectCode LIKE $prefix ORDER BY ProjectCode DESC LIMIT 1;";
        command.Parameters.AddWithValue("$prefix", prefix + "%");
        var last = await command.ExecuteScalarAsync(cancellationToken) as string;
        var next = 1;
        if (!string.IsNullOrWhiteSpace(last) && last.Length >= prefix.Length + 3 &&
            int.TryParse(last[prefix.Length..], out var sequence))
        {
            next = sequence + 1;
        }

        var code = $"{prefix}{next:000}";
        transaction.Commit();
        return code;
    }

    public async Task<ProjectRecord> CreateAsync(ProjectRecord project, ProjectVersionRecord initialVersion, ProjectActivityRecord activity, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        project.MetadataSyncState = "Pending";
        project.Id = await InsertProjectAsync(connection, transaction, project, cancellationToken);
        initialVersion.ProjectId = project.Id;
        activity.ProjectId = project.Id;
        await InsertVersionAsync(connection, transaction, initialVersion, cancellationToken);
        await ReplaceNamesAsync(connection, transaction, project.Id, project.Tags, true, cancellationToken);
        await ReplaceNamesAsync(connection, transaction, project.Id, project.Software, false, cancellationToken);
        await InsertActivityAsync(connection, transaction, activity, cancellationToken);
        await InsertStatusHistoryAsync(connection, transaction, project.Id, null, project.WorkflowStatus, null, null, project.CreatedAt, cancellationToken);
        await QueueMetadataAsync(connection, transaction, project.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return project;
    }

    public async Task SaveAsync(ProjectRecord project, ProjectActivityRecord activity, WorkflowStatus? previousStatus = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        project.MetadataSyncState = "Pending";
        await UpdateProjectAsync(connection, transaction, project, cancellationToken);
        await ReplaceNamesAsync(connection, transaction, project.Id, project.Tags, true, cancellationToken);
        await ReplaceNamesAsync(connection, transaction, project.Id, project.Software, false, cancellationToken);
        activity.ProjectId = project.Id;
        await InsertActivityAsync(connection, transaction, activity, cancellationToken);
        if (previousStatus.HasValue)
        {
            // 上一个状态的区间在这里收口，新状态从新的一行开始并保持 open（EndedAtUtc 为 NULL）。
            await CloseOpenStatusHistoryAsync(connection, transaction, project.Id, activity.CreatedAt, cancellationToken);
            await InsertStatusHistoryAsync(connection, transaction, project.Id, previousStatus, project.WorkflowStatus,
                project.WaitReason, project.WaitNote, activity.CreatedAt, cancellationToken);
        }

        await QueueMetadataAsync(connection, transaction, project.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectVersionRecord>> GetVersionsAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ProjectVersions WHERE ProjectId=$projectId ORDER BY VersionNumber DESC;";
        command.Parameters.AddWithValue("$projectId", projectId);
        var result = new List<ProjectVersionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ProjectVersionRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                ProjectId = projectId,
                VersionNumber = reader.GetString(reader.GetOrdinal("VersionNumber")),
                Title = reader.GetString(reader.GetOrdinal("Title")),
                Description = reader.GetString(reader.GetOrdinal("Description")),
                ChangeSummary = reader.GetString(reader.GetOrdinal("ChangeSummary")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                CreatedAt = ParseRequired(reader, "CreatedAtUtc"),
                CompletedAt = ParseNullable(reader, "CompletedAtUtc"),
                RelativePath = reader.GetString(reader.GetOrdinal("RelativePath"))
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<ProjectActivityRecord>> GetActivitiesAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ProjectActivities WHERE ProjectId=$projectId ORDER BY CreatedAtUtc DESC;";
        command.Parameters.AddWithValue("$projectId", projectId);
        var result = new List<ProjectActivityRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ProjectActivityRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                ProjectId = projectId,
                ActivityType = reader.GetString(reader.GetOrdinal("ActivityType")),
                Description = reader.GetString(reader.GetOrdinal("Description")),
                MetadataJson = reader.GetString(reader.GetOrdinal("MetadataJson")),
                CreatedAt = ParseRequired(reader, "CreatedAtUtc")
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<ProjectStatusHistoryRecord>> GetStatusHistoryAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ProjectStatusHistory WHERE ProjectId=$projectId ORDER BY StartedAtUtc ASC, Id ASC;";
        command.Parameters.AddWithValue("$projectId", projectId);
        var result = new List<ProjectStatusHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var durationOrdinal = reader.GetOrdinal("DurationSeconds");
            result.Add(new ProjectStatusHistoryRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                ProjectId = projectId,
                FromStatus = GetNullableEnum<WorkflowStatus>(reader, "FromStatus"),
                ToStatus = ParseEnum<WorkflowStatus>(reader.GetString(reader.GetOrdinal("ToStatus"))),
                Reason = GetNullableString(reader, "Reason"),
                Note = GetNullableString(reader, "Note"),
                StartedAt = ParseRequired(reader, "StartedAtUtc"),
                EndedAt = ParseNullable(reader, "EndedAtUtc"),
                DurationSeconds = reader.IsDBNull(durationOrdinal) ? null : reader.GetInt64(durationOrdinal)
            });
        }

        return result;
    }

    public async Task AddVersionAsync(ProjectRecord project, ProjectVersionRecord version, ProjectActivityRecord activity, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        project.MetadataSyncState = "Pending";
        await UpdateProjectAsync(connection, transaction, project, cancellationToken);
        version.ProjectId = project.Id;
        await InsertVersionAsync(connection, transaction, version, cancellationToken);
        activity.ProjectId = project.Id;
        await InsertActivityAsync(connection, transaction, activity, cancellationToken);
        await QueueMetadataAsync(connection, transaction, project.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkMetadataSyncedAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE Projects SET MetadataSyncState='Synced' WHERE Id=$id;";
            update.Parameters.AddWithValue("$id", projectId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM MetadataSyncQueue WHERE ProjectId=$id;";
            delete.Parameters.AddWithValue("$id", projectId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectRecord>> GetPendingMetadataSyncAsync(CancellationToken cancellationToken = default)
        => (await GetAllAsync(cancellationToken)).Where(project => project.MetadataSyncState != "Synced").ToList();

    public async Task<bool> ImportAsync(ProjectRecord project, ProjectActivityRecord activity, CancellationToken cancellationToken = default)
    {
        if (await GetByCodeAsync(project.ProjectCode, cancellationToken) is not null)
        {
            return false;
        }

        var version = new ProjectVersionRecord
        {
            ProjectId = 0,
            VersionNumber = project.CurrentVersion,
            Title = "扫描恢复版本",
            Status = "Active",
            CreatedAt = project.CreatedAt,
            RelativePath = Path.Combine("Versions", project.CurrentVersion)
        };
        await CreateAsync(project, version, activity, cancellationToken);
        return true;
    }

    public Task SaveTransferAsync(TransferOperationRecord transfer, CancellationToken cancellationToken = default)
        => UpsertTransferAsync(transfer, cancellationToken);

    public Task UpdateTransferAsync(TransferOperationRecord transfer, CancellationToken cancellationToken = default)
        => UpsertTransferAsync(transfer, cancellationToken);

    public async Task<IReadOnlyList<TransferOperationRecord>> GetIncompleteTransfersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TransferOperations WHERE State NOT IN ('Completed','Abandoned') ORDER BY UpdatedAtUtc DESC;";
        var result = new List<TransferOperationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TransferOperationRecord
            {
                Id = reader.GetString(reader.GetOrdinal("Id")),
                ProjectId = reader.GetInt64(reader.GetOrdinal("ProjectId")),
                SourceLocation = ParseEnum<StorageLocationCode>(reader.GetString(reader.GetOrdinal("SourceLocation"))),
                TargetLocation = ParseEnum<StorageLocationCode>(reader.GetString(reader.GetOrdinal("TargetLocation"))),
                SourcePath = reader.GetString(reader.GetOrdinal("SourcePath")),
                TargetPath = reader.GetString(reader.GetOrdinal("TargetPath")),
                State = ParseEnum<TransferState>(reader.GetString(reader.GetOrdinal("State"))),
                TotalBytes = reader.GetInt64(reader.GetOrdinal("TotalBytes")),
                ProcessedBytes = reader.GetInt64(reader.GetOrdinal("ProcessedBytes")),
                Error = GetNullableString(reader, "Error"),
                CreatedAt = ParseRequired(reader, "CreatedAtUtc"),
                UpdatedAt = ParseRequired(reader, "UpdatedAtUtc")
            });
        }
        return result;
    }

    public async Task DeleteVersionAsync(long projectId, long versionId, string description,
        Action<ProjectRecord>? beforeDelete = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        // 立即拿写锁：检查、回退、删除和活动记录必须使用同一个数据库快照。
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var versions = new Dictionary<long, string>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT Id, VersionNumber FROM ProjectVersions WHERE ProjectId=$project;";
            query.Parameters.AddWithValue("$project", projectId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) versions.Add(reader.GetInt64(0), reader.GetString(1));
        }
        if (!versions.TryGetValue(versionId, out var number))
            throw new InvalidOperationException("版本记录已变化，请刷新后重试。");
        if (versions.Count <= 1)
            throw new InvalidOperationException("项目至少需要保留一个版本，无法删除。");

        var fallback = VersionNumbers.Highest(versions.Where(item => item.Key != versionId).Select(item => item.Value))!;
        var now = DateTimeOffset.Now;
        ProjectRecord storedProject;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT * FROM Projects WHERE Id=$id;";
            query.Parameters.AddWithValue("$id", projectId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("项目已不存在。");
            storedProject = ReadProject(reader);
        }
        var current = storedProject.CurrentVersion;
        var wasCurrent = string.Equals(current, number, StringComparison.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
        // 先通过事务内数量检查，再操作目录；目录操作抛错时数据库事务回滚。
        beforeDelete?.Invoke(storedProject);
        // 文件删除后不再接受取消，保证数据库随本次操作提交。
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM ProjectVersions WHERE Id=$version AND ProjectId=$project;
                UPDATE Projects SET CurrentVersion=$current, UpdatedAtUtc=$now, MetadataSyncState='Pending' WHERE Id=$project;
                """;
            command.Parameters.AddWithValue("$version", versionId);
            command.Parameters.AddWithValue("$project", projectId);
            command.Parameters.AddWithValue("$current", wasCurrent ? fallback : current);
            command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        await InsertActivityAsync(connection, transaction, new ProjectActivityRecord
        {
            ProjectId = projectId,
            ActivityType = ActivityTypes.VersionDeleted,
            Description = description + (wasCurrent ? $"，当前版本回退到 {fallback}" : string.Empty),
            CreatedAt = now
        }, CancellationToken.None);
        await QueueMetadataAsync(connection, transaction, projectId, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async Task DeleteAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        // 子表（版本、活动、状态历史、标签/软件关联、迁移记录、元数据队列）均声明 ON DELETE CASCADE。
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Projects WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", projectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TagSummaryRecord>> GetTagSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Name, COUNT(pt.ProjectId) AS ProjectCount
            FROM Tags t LEFT JOIN ProjectTags pt ON pt.TagId = t.Id
            GROUP BY t.Id, t.Name
            ORDER BY ProjectCount DESC, t.Name COLLATE NOCASE;
            """;
        var result = new List<TagSummaryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TagSummaryRecord(
                reader.GetString(reader.GetOrdinal("Name")),
                reader.GetInt32(reader.GetOrdinal("ProjectCount"))));
        }

        return result;
    }

    /// <summary>所有等待区间的原因（状态历史里 ToStatus=Waiting 的行，含当前仍在等待的那次）。</summary>
    public async Task<IReadOnlyList<WaitReasonRecord>> GetWaitReasonsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ProjectId, Reason FROM ProjectStatusHistory
            WHERE ToStatus = 'Waiting' AND Reason IS NOT NULL AND TRIM(Reason) <> '';
            """;
        var result = new List<WaitReasonRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WaitReasonRecord(reader.GetInt64(0), reader.GetString(1)));
        }

        return result;
    }

    public async Task<IReadOnlyList<long>> RenameTagAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var oldId = await FindTagIdAsync(connection, transaction, oldName, cancellationToken);
        if (oldId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var newId = await FindTagIdAsync(connection, transaction, newName, cancellationToken);
        var affected = await CollectProjectIdsAsync(connection, transaction, oldId.Value, newId, cancellationToken);
        if (newId is null || newId == oldId)
        {
            // 目标名称不存在：原地重命名
            await using var rename = connection.CreateCommand();
            rename.Transaction = transaction;
            rename.CommandText = "UPDATE Tags SET Name=$new WHERE Id=$id;";
            rename.Parameters.AddWithValue("$new", newName);
            rename.Parameters.AddWithValue("$id", oldId.Value);
            await rename.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            // 目标名称已存在：合并到目标
            await MergeTagJoinsAsync(connection, transaction, oldId.Value, newId.Value, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return affected;
    }

    public async Task<IReadOnlyList<long>> MergeTagsAsync(string sourceName, string targetName, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var sourceId = await FindTagIdAsync(connection, transaction, sourceName, cancellationToken);
        var targetId = await FindTagIdAsync(connection, transaction, targetName, cancellationToken);
        if (sourceId is null || targetId is null || sourceId == targetId)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var affected = await CollectProjectIdsAsync(connection, transaction, sourceId.Value, targetId, cancellationToken);
        await MergeTagJoinsAsync(connection, transaction, sourceId.Value, targetId.Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected;
    }

    public async Task DeleteTagAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var tagId = await FindTagIdAsync(connection, transaction, name, cancellationToken);
        if (tagId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using (var detach = connection.CreateCommand())
        {
            detach.Transaction = transaction;
            detach.CommandText = "DELETE FROM ProjectTags WHERE TagId=$id;";
            detach.Parameters.AddWithValue("$id", tagId.Value);
            await detach.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var drop = connection.CreateCommand();
        drop.Transaction = transaction;
        drop.CommandText = "DELETE FROM Tags WHERE Id=$id;";
        drop.Parameters.AddWithValue("$id", tagId.Value);
        await drop.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<long?> FindTagIdAsync(SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id FROM Tags WHERE Name=$name COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync(cancellationToken) is long id ? id : null;
    }

    private static async Task MergeTagJoinsAsync(SqliteConnection connection, SqliteTransaction transaction, long sourceId, long targetId, CancellationToken cancellationToken)
    {
        await using (var repoint = connection.CreateCommand())
        {
            repoint.Transaction = transaction;
            repoint.CommandText = """
                INSERT OR IGNORE INTO ProjectTags(ProjectId, TagId)
                SELECT ProjectId, $targetId FROM ProjectTags WHERE TagId = $sourceId;
                """;
            repoint.Parameters.AddWithValue("$targetId", targetId);
            repoint.Parameters.AddWithValue("$sourceId", sourceId);
            await repoint.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var detach = connection.CreateCommand())
        {
            detach.Transaction = transaction;
            detach.CommandText = "DELETE FROM ProjectTags WHERE TagId=$sourceId;";
            detach.Parameters.AddWithValue("$sourceId", sourceId);
            await detach.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var drop = connection.CreateCommand();
        drop.Transaction = transaction;
        drop.CommandText = "DELETE FROM Tags WHERE Id=$sourceId;";
        drop.Parameters.AddWithValue("$sourceId", sourceId);
        await drop.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>收集受影响的 ProjectId（包含新/旧两个标签关联的项目）。</summary>
    private static async Task<IReadOnlyList<long>> CollectProjectIdsAsync(SqliteConnection connection, SqliteTransaction transaction, long oldId, long? newId, CancellationToken cancellationToken)
    {
        var result = new HashSet<long>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (newId is null)
        {
            command.CommandText = "SELECT DISTINCT ProjectId FROM ProjectTags WHERE TagId=$a;";
            command.Parameters.AddWithValue("$a", oldId);
        }
        else
        {
            command.CommandText = "SELECT DISTINCT ProjectId FROM ProjectTags WHERE TagId=$a OR TagId=$b;";
            command.Parameters.AddWithValue("$a", oldId);
            command.Parameters.AddWithValue("$b", newId.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt64(0));
        }

        return result.ToList();
    }

    private async Task UpsertTransferAsync(TransferOperationRecord transfer, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TransferOperations(Id, ProjectId, SourceLocation, TargetLocation, SourcePath, TargetPath, State, TotalBytes, ProcessedBytes, Error, CreatedAtUtc, UpdatedAtUtc)
            VALUES($id,$projectId,$sourceLocation,$targetLocation,$sourcePath,$targetPath,$state,$totalBytes,$processedBytes,$error,$created,$updated)
            ON CONFLICT(Id) DO UPDATE SET State=excluded.State, TotalBytes=excluded.TotalBytes,
                ProcessedBytes=excluded.ProcessedBytes, Error=excluded.Error, UpdatedAtUtc=excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$id", transfer.Id);
        command.Parameters.AddWithValue("$projectId", transfer.ProjectId);
        command.Parameters.AddWithValue("$sourceLocation", transfer.SourceLocation.ToString());
        command.Parameters.AddWithValue("$targetLocation", transfer.TargetLocation.ToString());
        command.Parameters.AddWithValue("$sourcePath", transfer.SourcePath);
        command.Parameters.AddWithValue("$targetPath", transfer.TargetPath);
        command.Parameters.AddWithValue("$state", transfer.State.ToString());
        command.Parameters.AddWithValue("$totalBytes", transfer.TotalBytes);
        command.Parameters.AddWithValue("$processedBytes", transfer.ProcessedBytes);
        command.Parameters.AddWithValue("$error", (object?)transfer.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", transfer.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$updated", transfer.UpdatedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> InsertProjectAsync(SqliteConnection connection, SqliteTransaction transaction, ProjectRecord project, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Projects(ProjectCode,Name,Description,Requester,Notes,WorkflowStatus,StorageStatus,StorageLocation,RelativePath,
                WaitReason,WaitNote,WaitStartedAtUtc,IsFavorite,CreatedAtUtc,StartedAtUtc,CompletedAtUtc,ArchivedAtUtc,UpdatedAtUtc,CurrentVersion,
                AccumulatedWaitSeconds,CoverImage,SimulationType,MetadataSyncState)
            VALUES($code,$name,$description,$requester,$notes,$workflow,$storageStatus,$storageLocation,$relativePath,
                $waitReason,$waitNote,$waitStarted,$favorite,$created,$started,$completed,$archived,$updated,$currentVersion,$waitSeconds,$coverImage,$simulationType,$syncState);
            SELECT last_insert_rowid();
            """;
        AddProjectParameters(command, project);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task UpdateProjectAsync(SqliteConnection connection, SqliteTransaction transaction, ProjectRecord project, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Projects SET Name=$name,Description=$description,Requester=$requester,Notes=$notes,WorkflowStatus=$workflow,
                StorageStatus=$storageStatus,StorageLocation=$storageLocation,RelativePath=$relativePath,WaitReason=$waitReason,
                WaitNote=$waitNote,WaitStartedAtUtc=$waitStarted,IsFavorite=$favorite,CreatedAtUtc=$created,StartedAtUtc=$started,CompletedAtUtc=$completed,
                ArchivedAtUtc=$archived,UpdatedAtUtc=$updated,CurrentVersion=$currentVersion,AccumulatedWaitSeconds=$waitSeconds,
                CoverImage=$coverImage,SimulationType=$simulationType,MetadataSyncState=$syncState WHERE Id=$id;
            """;
        AddProjectParameters(command, project);
        command.Parameters.AddWithValue("$id", project.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddProjectParameters(SqliteCommand command, ProjectRecord project)
    {
        command.Parameters.AddWithValue("$code", project.ProjectCode);
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$description", project.Description);
        command.Parameters.AddWithValue("$requester", project.Requester);
        command.Parameters.AddWithValue("$notes", project.Notes);
        command.Parameters.AddWithValue("$workflow", project.WorkflowStatus.ToString());
        command.Parameters.AddWithValue("$storageStatus", project.StorageStatus.ToString());
        command.Parameters.AddWithValue("$storageLocation", project.StorageLocation.ToString());
        command.Parameters.AddWithValue("$relativePath", project.RelativePath);
        command.Parameters.AddWithValue("$waitReason", (object?)project.WaitReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$waitNote", (object?)project.WaitNote ?? DBNull.Value);
        command.Parameters.AddWithValue("$waitStarted", ToDb(project.WaitStartedAt));
        command.Parameters.AddWithValue("$favorite", project.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$created", project.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$started", ToDb(project.StartedAt));
        command.Parameters.AddWithValue("$completed", ToDb(project.CompletedAt));
        command.Parameters.AddWithValue("$archived", ToDb(project.ArchivedAt));
        command.Parameters.AddWithValue("$updated", project.UpdatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$currentVersion", project.CurrentVersion);
        command.Parameters.AddWithValue("$waitSeconds", project.AccumulatedWaitSeconds);
        command.Parameters.AddWithValue("$coverImage", project.CoverImage ?? string.Empty);
        // 仿真类型是可选列：未分类写 NULL，不用空字符串冒充“无值”。
        command.Parameters.AddWithValue("$simulationType", (object?)project.SimulationType?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$syncState", project.MetadataSyncState);
    }

    private static async Task InsertVersionAsync(SqliteConnection connection, SqliteTransaction transaction, ProjectVersionRecord version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ProjectVersions(ProjectId,VersionNumber,Title,Description,ChangeSummary,Status,CreatedAtUtc,CompletedAtUtc,RelativePath)
            VALUES($projectId,$number,$title,$description,$summary,$status,$created,$completed,$path);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$projectId", version.ProjectId);
        command.Parameters.AddWithValue("$number", version.VersionNumber);
        command.Parameters.AddWithValue("$title", version.Title);
        command.Parameters.AddWithValue("$description", version.Description);
        command.Parameters.AddWithValue("$summary", version.ChangeSummary);
        command.Parameters.AddWithValue("$status", version.Status);
        command.Parameters.AddWithValue("$created", version.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$completed", ToDb(version.CompletedAt));
        command.Parameters.AddWithValue("$path", version.RelativePath);
        // 回填自增主键：调用方（创建版本 / 扫描补建）拿到的对象必须能直接用于后续按 Id 的操作。
        version.Id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task InsertActivityAsync(SqliteConnection connection, SqliteTransaction transaction, ProjectActivityRecord activity, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ProjectActivities(ProjectId,ActivityType,Description,MetadataJson,CreatedAtUtc)
            VALUES($projectId,$type,$description,$metadata,$created);
            """;
        command.Parameters.AddWithValue("$projectId", activity.ProjectId);
        command.Parameters.AddWithValue("$type", activity.ActivityType);
        command.Parameters.AddWithValue("$description", activity.Description);
        command.Parameters.AddWithValue("$metadata", activity.MetadataJson);
        command.Parameters.AddWithValue("$created", activity.CreatedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 关闭本项目当前仍然 open 的状态历史行，按该行自己的 StartedAtUtc 计算真实时长。
    /// 区间起点取自被关闭的行，因此无需调用方额外传入。
    /// 时长在 C# 中计算，避免依赖 SQLite 对 ISO-8601 小数秒的解析能力。
    /// </summary>
    private static async Task CloseOpenStatusHistoryAsync(SqliteConnection connection, SqliteTransaction transaction,
        long projectId, DateTimeOffset endedAt, CancellationToken cancellationToken)
    {
        long rowId;
        DateTimeOffset startedAt;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT Id, StartedAtUtc FROM ProjectStatusHistory
                WHERE ProjectId = $projectId AND EndedAtUtc IS NULL
                ORDER BY StartedAtUtc DESC, Id DESC LIMIT 1;
                """;
            select.Parameters.AddWithValue("$projectId", projectId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            rowId = reader.GetInt64(0);
            startedAt = DateTimeOffset.Parse(reader.GetString(1)).ToLocalTime();
        }

        var duration = Math.Max(0, (long)(endedAt - startedAt).TotalSeconds);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE ProjectStatusHistory SET EndedAtUtc=$ended, DurationSeconds=$duration WHERE Id=$id;";
        update.Parameters.AddWithValue("$id", rowId);
        update.Parameters.AddWithValue("$ended", endedAt.ToUniversalTime().ToString("O"));
        update.Parameters.AddWithValue("$duration", duration);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertStatusHistoryAsync(SqliteConnection connection, SqliteTransaction transaction, long projectId,
        WorkflowStatus? fromStatus, WorkflowStatus toStatus, string? reason, string? note, DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ProjectStatusHistory(ProjectId,FromStatus,ToStatus,Reason,Note,StartedAtUtc,EndedAtUtc,DurationSeconds)
            VALUES($projectId,$fromStatus,$toStatus,$reason,$note,$started,NULL,NULL);
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$fromStatus", (object?)fromStatus?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$toStatus", toStatus.ToString());
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", startedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceNamesAsync(SqliteConnection connection, SqliteTransaction transaction, long projectId,
        IEnumerable<string> names, bool tags, CancellationToken cancellationToken)
    {
        var joinTable = tags ? "ProjectTags" : "ProjectSoftware";
        var lookupTable = tags ? "Tags" : "Software";
        var foreignKey = tags ? "TagId" : "SoftwareId";
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {joinTable} WHERE ProjectId=$projectId;";
            delete.Parameters.AddWithValue("$projectId", projectId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var name in names.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT OR IGNORE INTO {lookupTable}(Name) VALUES($name);
                INSERT OR IGNORE INTO {joinTable}(ProjectId,{foreignKey})
                SELECT $projectId, Id FROM {lookupTable} WHERE Name=$name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$projectId", projectId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task QueueMetadataAsync(SqliteConnection connection, SqliteTransaction transaction, long projectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO MetadataSyncQueue(ProjectId,Attempts,UpdatedAtUtc) VALUES($projectId,0,$updated)
            ON CONFLICT(ProjectId) DO UPDATE SET UpdatedAtUtc=excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task LoadNamesAsync(SqliteConnection connection, string sql, IReadOnlyDictionary<long, ProjectRecord> projects,
        Func<ProjectRecord, List<string>> selector, CancellationToken cancellationToken, long? filterProjectId = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (filterProjectId.HasValue)
        {
            command.Parameters.AddWithValue("$projectId", filterProjectId.Value);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var projectId = reader.GetInt64(0);
            if (projects.TryGetValue(projectId, out var project))
            {
                selector(project).Add(reader.GetString(1));
            }
        }
    }

    private static async Task LoadProjectNamesAsync(SqliteConnection connection, ProjectRecord project, CancellationToken cancellationToken)
    {
        var byId = new Dictionary<long, ProjectRecord> { [project.Id] = project };
        await LoadNamesAsync(connection, """
            SELECT pt.ProjectId, t.Name
            FROM ProjectTags pt JOIN Tags t ON t.Id = pt.TagId
            WHERE pt.ProjectId = $projectId;
            """, byId, static item => item.Tags, cancellationToken, project.Id);
        await LoadNamesAsync(connection, """
            SELECT ps.ProjectId, s.Name
            FROM ProjectSoftware ps JOIN Software s ON s.Id = ps.SoftwareId
            WHERE ps.ProjectId = $projectId;
            """, byId, static item => item.Software, cancellationToken, project.Id);
    }

    private static ProjectRecord ReadProject(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        ProjectCode = reader.GetString(reader.GetOrdinal("ProjectCode")),
        Name = reader.GetString(reader.GetOrdinal("Name")),
        Description = reader.GetString(reader.GetOrdinal("Description")),
        Requester = reader.GetString(reader.GetOrdinal("Requester")),
        Notes = reader.GetString(reader.GetOrdinal("Notes")),
        WorkflowStatus = ParseEnum<WorkflowStatus>(reader.GetString(reader.GetOrdinal("WorkflowStatus"))),
        StorageStatus = ParseEnum<StorageStatus>(reader.GetString(reader.GetOrdinal("StorageStatus"))),
        StorageLocation = ParseEnum<StorageLocationCode>(reader.GetString(reader.GetOrdinal("StorageLocation"))),
        SimulationType = ParseOptionalEnum<SimulationType>(reader, "SimulationType"),
        RelativePath = reader.GetString(reader.GetOrdinal("RelativePath")),
        WaitReason = GetNullableString(reader, "WaitReason"),
        WaitNote = GetNullableString(reader, "WaitNote"),
        WaitStartedAt = ParseNullable(reader, "WaitStartedAtUtc"),
        IsFavorite = reader.GetInt32(reader.GetOrdinal("IsFavorite")) != 0,
        CreatedAt = ParseRequired(reader, "CreatedAtUtc"),
        StartedAt = ParseNullable(reader, "StartedAtUtc"),
        CompletedAt = ParseNullable(reader, "CompletedAtUtc"),
        ArchivedAt = ParseNullable(reader, "ArchivedAtUtc"),
        UpdatedAt = ParseRequired(reader, "UpdatedAtUtc"),
        CurrentVersion = reader.GetString(reader.GetOrdinal("CurrentVersion")),
        AccumulatedWaitSeconds = reader.GetInt64(reader.GetOrdinal("AccumulatedWaitSeconds")),
        CoverImage = GetNullableString(reader, "CoverImage") ?? string.Empty,
        MetadataSyncState = reader.GetString(reader.GetOrdinal("MetadataSyncState"))
    };

    private static T ParseEnum<T>(string value) where T : struct, Enum
        => Enum.TryParse<T>(value, true, out var result) ? result : throw new InvalidDataException($"无法识别枚举值 {typeof(T).Name}.{value}");

    private static T? GetNullableEnum<T>(SqliteDataReader reader, string column) where T : struct, Enum
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : ParseEnum<T>(reader.GetString(ordinal));
    }

    /// <summary>
    /// 可选枚举列的宽容解析：NULL 与无法识别的值都按“未分类”处理。
    /// 仿真类型是后加的字段，单个坏值不应该让整个项目列表加载失败（工作流状态等历史字段仍是严格解析）。
    /// </summary>
    private static T? ParseOptionalEnum<T>(SqliteDataReader reader, string column) where T : struct, Enum
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return Enum.TryParse<T>(reader.GetString(ordinal), true, out var result) ? result : null;
    }

    private static DateTimeOffset ParseRequired(SqliteDataReader reader, string column)
        => DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal(column))).ToLocalTime();

    private static DateTimeOffset? ParseNullable(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal)).ToLocalTime();
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object ToDb(DateTimeOffset? value)
        => value.HasValue ? value.Value.ToUniversalTime().ToString("O") : DBNull.Value;
}
