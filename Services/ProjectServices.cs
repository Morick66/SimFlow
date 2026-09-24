using Microsoft.Extensions.Logging;
using SimFlow.Models;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimFlow.Services;

public sealed class ProjectMetadataStore(ILogger<ProjectMetadataStore> logger) : IProjectMetadataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public Task WriteProjectAsync(string projectDirectory, ProjectMetadataDocument metadata, CancellationToken cancellationToken = default)
        => WriteAtomicAsync(Path.Combine(projectDirectory, "project.json"), metadata, cancellationToken);

    public async Task<ProjectMetadataDocument?> ReadProjectAsync(string metadataPath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return await JsonSerializer.DeserializeAsync<ProjectMetadataDocument>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read project metadata at {MetadataPath}", metadataPath);
            return null;
        }
    }

    public Task WriteVersionAsync(string versionDirectory, VersionMetadataDocument metadata, CancellationToken cancellationToken = default)
        => WriteAtomicAsync(Path.Combine(versionDirectory, "version.json"), metadata, cancellationToken);

    /// <summary>读取版本档案；文件缺失或损坏时返回 null（扫描补建版本记录时用它决定标题/时间）。</summary>
    public async Task<VersionMetadataDocument?> ReadVersionAsync(string metadataPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(metadataPath))
            {
                return null;
            }

            await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return await JsonSerializer.DeserializeAsync<VersionMetadataDocument>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read version metadata at {MetadataPath}", metadataPath);
            return null;
        }
    }

    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public sealed class StorageLocationService(IConfigurationService configuration) : IStorageLocationService
{
    public string ResolveRoot(StorageLocationCode location) => location switch
    {
        StorageLocationCode.LocalWork => configuration.Current.LocalWorkRoot,
        StorageLocationCode.WorkstationWork => configuration.Current.WorkstationWorkRoot,
        StorageLocationCode.WorkstationArchive => configuration.Current.WorkstationArchiveRoot,
        _ => throw new ArgumentOutOfRangeException(nameof(location), location, null)
    };

    public string ResolveProjectPath(ProjectRecord project)
    {
        var root = ResolveRoot(project.StorageLocation);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException($"尚未配置 {project.StorageLocation} 路径。");
        }

        return project.StorageLocation == StorageLocationCode.WorkstationArchive && project.ArchivedAt.HasValue
            ? Path.Combine(root, project.ArchivedAt.Value.ToString("yyyy"), project.ArchivedAt.Value.ToString("MM"), project.RelativePath)
            : Path.Combine(root, project.RelativePath);
    }

    public Task<bool> IsOnlineAsync(StorageLocationCode location, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = ResolveRoot(location);
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);
        }, cancellationToken);

    public Task<long?> GetAvailableBytesAsync(StorageLocationCode location, CancellationToken cancellationToken = default)
        => Task.Run<long?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = ResolveRoot(location);
            if (string.IsNullOrWhiteSpace(root)) return null;
            try
            {
                var driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
                return string.IsNullOrWhiteSpace(driveRoot) ? null : new DriveInfo(driveRoot).AvailableFreeSpace;
            }
            catch
            {
                return null;
            }
        }, cancellationToken);
}

public sealed class ProjectService(
    IProjectRepository repository,
    IProjectMetadataStore metadataStore,
    IStorageLocationService storage,
    ILogger<ProjectService> logger) : IProjectService
{
    private static readonly string[] SupportedCoverExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];

    public Task<IReadOnlyList<ProjectRecord>> GetProjectsAsync(CancellationToken cancellationToken = default)
        => repository.GetAllAsync(cancellationToken);

    public async Task<ProjectRecord> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("项目名称不能为空。");
        }

        var root = storage.ResolveRoot(request.InitialLocation);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("所选存储位置尚未配置。");
        }
        Directory.CreateDirectory(root);

        // 并发创建时两个请求可能拿到同一序号（编目录与插入之间存在时间窗）；
        // 撞上 Unique 约束或目录已被占用（例如“仅删记录保留文件夹”后重建）时换新编号重试，最多 5 次。
        // 注意：目录占用的序号在数据库里可能已不存在行，生成器会重复返回同一个号，
        // 因此必须记录已跳过的最大序号，强制新编号严格递增（minSequence + 1）。
        const int maxCreateAttempts = 5;
        var minSequence = 0;
        for (var attempt = 0; attempt < maxCreateAttempts; attempt++)
        {
            var now = DateTimeOffset.Now;
            var code = await repository.GenerateNextProjectCodeAsync(now, cancellationToken);
            var sequence = ParseSequence(code);
            if (sequence <= minSequence)
            {
                sequence = minSequence + 1;
                code = $"{code[..^3]}{sequence:000}";
            }

            var projectDirectory = Path.Combine(root, code);
            if (Directory.Exists(projectDirectory))
            {
                // 同序号目录已被占用（并发请求或保留文件夹），换下一个序号。
                minSequence = Math.Max(minSequence, sequence);
                continue;
            }

            CreateProjectDirectories(projectDirectory, "V001");
            var project = new ProjectRecord
            {
                ProjectCode = code,
                Name = request.Name.Trim(),
                Requester = request.Requester.Trim(),
                Description = request.Description.Trim(),
                WorkflowStatus = request.StartImmediately ? WorkflowStatus.Active : WorkflowStatus.NotStarted,
                StorageStatus = StorageStatus.Working,
                StorageLocation = request.InitialLocation,
                SimulationType = request.SimulationType,
                RelativePath = code,
                IsFavorite = request.IsFavorite,
                CreatedAt = now,
                StartedAt = request.StartImmediately ? now : null,
                UpdatedAt = now,
                CurrentVersion = "V001",
                Tags = Normalize(request.Tags),
                Software = Normalize(request.Software),
                MetadataSyncState = "Pending"
            };
            var version = new ProjectVersionRecord
            {
                ProjectId = 0,
                VersionNumber = "V001",
                Title = "基准方案",
                Status = "Active",
                CreatedAt = now,
                RelativePath = Path.Combine("Versions", "V001")
            };
            var activity = new ProjectActivityRecord
            {
                ProjectId = 0,
                ActivityType = ActivityTypes.ProjectCreated,
                Description = request.StartImmediately ? "创建项目并开始" : "创建项目",
                CreatedAt = now
            };

            try
            {
                await repository.CreateAsync(project, version, activity, cancellationToken);
                await metadataStore.WriteVersionAsync(Path.Combine(projectDirectory, version.RelativePath), new VersionMetadataDocument
                {
                    VersionNumber = version.VersionNumber,
                    Title = version.Title,
                    Status = version.Status,
                    CreatedAt = version.CreatedAt
                }, cancellationToken);
                await SyncMetadataAsync(project, projectDirectory, cancellationToken);
                return project;
            }
            catch (Exception ex)
            {
                // 数据库插入成功但档案写入失败时 project.Id 已分配，保留目录与 Pending 记录，交给启动重试。
                if (project.Id == 0 && Directory.Exists(projectDirectory))
                {
                    Directory.Delete(projectDirectory, true);
                }
                if (IsDuplicateProjectCode(ex))
                {
                    logger.LogWarning("项目编号 {Code} 并发冲突，换一个新编号重试。", code);
                    minSequence = Math.Max(minSequence, sequence);
                    continue;
                }
                throw;
            }
        }

        throw new InvalidOperationException("多次尝试后仍无法生成唯一的项目编号，请稍后重试。");
    }

    /// <summary>
    /// 命中 Projects.ProjectCode 的 UNIQUE 约束（SQLite 错误码 19）即视为并发撞号。
    /// </summary>
    private static bool IsDuplicateProjectCode(Exception ex) =>
        ex is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } sqlite
        && sqlite.Message?.Contains("Projects.ProjectCode", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>从 SIM_yyyyMMdd_NNN 中取出末尾三位序号；格式异常时返回 0。</summary>
    private static int ParseSequence(string code)
        => code.Length >= 3 && int.TryParse(code[^3..], out var value) ? value : 0;

    public async Task<ProjectRecord> ChangeStatusAsync(ProjectRecord project, WorkflowStatus targetStatus, string? reason = null, string? note = null, CancellationToken cancellationToken = default)
    {
        ValidateTransition(project.WorkflowStatus, targetStatus, reason);
        var now = DateTimeOffset.Now;
        var previous = project.WorkflowStatus;

        if (targetStatus == WorkflowStatus.NotStarted)
        {
            // 明确重置到“未开始”：当前生命周期重新计时，历史区间仍完整保留在状态历史表中。
            project.StartedAt = null;
            project.CompletedAt = null;
        }
        else if (targetStatus == WorkflowStatus.Active)
        {
            project.StartedAt = previous == WorkflowStatus.Completed ? now : project.StartedAt ?? now;
            project.CompletedAt = null;
        }
        else if (targetStatus == WorkflowStatus.Waiting)
        {
            // 从“已完成”纠正到等待也代表项目重新打开。
            project.CompletedAt = null;
        }
        if (targetStatus == WorkflowStatus.Waiting)
        {
            project.WaitReason = reason!.Trim();
            project.WaitNote = note?.Trim();
            project.WaitStartedAt = now;
        }
        else if (previous == WorkflowStatus.Waiting)
        {
            if (project.WaitStartedAt.HasValue)
            {
                project.AccumulatedWaitSeconds += Math.Max(0, (long)(now - project.WaitStartedAt.Value).TotalSeconds);
            }
            project.WaitStartedAt = null;
            project.WaitReason = null;
            project.WaitNote = null;
        }
        if (targetStatus == WorkflowStatus.Completed)
        {
            // 重新打开后再次完成时，以本次完成时间为准；历次完成仍可从活动/状态历史追溯。
            project.CompletedAt = now;
        }

        project.WorkflowStatus = targetStatus;
        project.UpdatedAt = now;
        var activity = new ProjectActivityRecord
        {
            ProjectId = project.Id,
            ActivityType = targetStatus == WorkflowStatus.Waiting ? ActivityTypes.WaitStarted :
                previous == WorkflowStatus.Waiting ? ActivityTypes.WaitEnded : ActivityTypes.StatusChanged,
            Description = DescribeTransition(previous, targetStatus, reason),
            CreatedAt = now
        };
        await repository.SaveAsync(project, activity, previous, cancellationToken);
        await SyncMetadataAsync(project, storage.ResolveProjectPath(project), cancellationToken);
        return project;
    }

    public async Task<ProjectRecord> ToggleFavoriteAsync(ProjectRecord project, CancellationToken cancellationToken = default)
    {
        project.IsFavorite = !project.IsFavorite;
        project.UpdatedAt = DateTimeOffset.Now;
        await repository.SaveAsync(project, new ProjectActivityRecord
        {
            ProjectId = project.Id,
            ActivityType = ActivityTypes.FavoriteChanged,
            Description = project.IsFavorite ? "加入收藏" : "取消收藏",
            CreatedAt = project.UpdatedAt
        }, cancellationToken: cancellationToken);
        await SyncMetadataAsync(project, storage.ResolveProjectPath(project), cancellationToken);
        return project;
    }

    public async Task<ProjectRecord> UpdateInfoAsync(ProjectRecord project, UpdateProjectInfoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("项目名称不能为空。");
        }

        var name = request.Name.Trim();
        var requester = request.Requester.Trim();
        var description = request.Description.Trim();
        var tags = Normalize(request.Tags);
        var software = Normalize(request.Software);

        var changes = new List<string>();
        var metadata = new Dictionary<string, object?>();
        if (!string.Equals(project.Name, name, StringComparison.Ordinal))
        {
            changes.Add("项目名称");
            metadata["name"] = new { before = project.Name, after = name };
        }
        if (!string.Equals(project.Requester, requester, StringComparison.Ordinal))
        {
            changes.Add("需求人");
            metadata["requester"] = new { before = project.Requester, after = requester };
        }
        if (!string.Equals(project.Description, description, StringComparison.Ordinal))
        {
            changes.Add("项目描述");
            metadata["description"] = new { before = project.Description, after = description };
        }
        if (project.SimulationType != request.SimulationType)
        {
            changes.Add("仿真类型");
            metadata["simulationType"] = new
            {
                before = SimulationTypes.Describe(project.SimulationType),
                after = SimulationTypes.Describe(request.SimulationType)
            };
        }
        if (!tags.SequenceEqual(project.Tags, StringComparer.OrdinalIgnoreCase))
        {
            changes.Add("标签");
            metadata["tags"] = new { before = project.Tags, after = tags };
        }
        if (!software.SequenceEqual(project.Software, StringComparer.OrdinalIgnoreCase))
        {
            changes.Add("软件");
            metadata["software"] = new { before = project.Software, after = software };
        }

        // 没有任何字段变化时直接返回，避免写入一条空的活动记录并触发一次多余的元数据同步。
        if (changes.Count == 0)
        {
            return project;
        }

        project.Name = name;
        project.Requester = requester;
        project.Description = description;
        project.SimulationType = request.SimulationType;
        project.Tags = tags;
        project.Software = software;
        project.UpdatedAt = DateTimeOffset.Now;
        await repository.SaveAsync(project, new ProjectActivityRecord
        {
            ProjectId = project.Id,
            ActivityType = ActivityTypes.ProjectUpdated,
            Description = $"更新项目信息：{string.Join("、", changes)}",
            MetadataJson = JsonSerializer.Serialize(metadata),
            CreatedAt = project.UpdatedAt
        }, cancellationToken: cancellationToken);
        await SyncMetadataAsync(project, storage.ResolveProjectPath(project), cancellationToken);
        return project;
    }

    /// <summary>
    /// 封面图存放在项目目录根部（cover&lt;扩展名&gt;），因此会跟随项目一起迁移、并在项目档案里留下记录。
    /// </summary>
    public async Task<ProjectRecord> SetCoverImageAsync(ProjectRecord project, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new InvalidOperationException("请选择一张图片。");
        }
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"找不到图片：{sourcePath}", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension) || !SupportedCoverExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"不支持的图片格式 {extension}，请使用 {string.Join("、", SupportedCoverExtensions)}。");
        }

        var projectDirectory = storage.ResolveProjectPath(project);
        if (!Directory.Exists(projectDirectory))
        {
            throw new DirectoryNotFoundException(projectDirectory);
        }

        var relativePath = "cover" + extension.ToLowerInvariant();
        var destination = Path.Combine(projectDirectory, relativePath);
        // 先写入新封面，成功后再清理换过格式的旧封面，避免中途失败把原图弄丢。
        File.Copy(sourcePath, destination, true);
        foreach (var stale in Directory.EnumerateFiles(projectDirectory, "cover.*"))
        {
            if (!string.Equals(Path.GetFileName(stale), relativePath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(stale);
            }
        }

        project.CoverImage = relativePath;
        project.UpdatedAt = DateTimeOffset.Now;
        await repository.SaveAsync(project, new ProjectActivityRecord
        {
            ProjectId = project.Id,
            ActivityType = ActivityTypes.CoverChanged,
            Description = "更新项目封面",
            CreatedAt = project.UpdatedAt
        }, cancellationToken: cancellationToken);
        await SyncMetadataAsync(project, projectDirectory, cancellationToken);
        return project;
    }

    public async Task<ProjectRecord> ClearCoverImageAsync(ProjectRecord project, CancellationToken cancellationToken = default)
    {
        var projectDirectory = storage.ResolveProjectPath(project);
        if (Directory.Exists(projectDirectory))
        {
            foreach (var stale in Directory.EnumerateFiles(projectDirectory, "cover.*"))
            {
                File.Delete(stale);
            }
        }

        if (string.IsNullOrWhiteSpace(project.CoverImage))
        {
            project.CoverImage = string.Empty;
            return project;
        }

        project.CoverImage = string.Empty;
        project.UpdatedAt = DateTimeOffset.Now;
        await repository.SaveAsync(project, new ProjectActivityRecord
        {
            ProjectId = project.Id,
            ActivityType = ActivityTypes.CoverChanged,
            Description = "移除项目封面",
            CreatedAt = project.UpdatedAt
        }, cancellationToken: cancellationToken);
        await SyncMetadataAsync(project, projectDirectory, cancellationToken);
        return project;
    }

    public async Task<IReadOnlyList<TagSummaryRecord>> GetTagSummaryAsync(CancellationToken cancellationToken = default)
        => await repository.GetTagSummaryAsync(cancellationToken);

    public Task<IReadOnlyList<WaitReasonRecord>> GetWaitReasonsAsync(CancellationToken cancellationToken = default)
        => repository.GetWaitReasonsAsync(cancellationToken);

    public async Task RenameTagAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        ValidateTagName(oldName);
        ValidateTagName(newName);
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var affected = await repository.RenameTagAsync(oldName, newName, cancellationToken);
        await RebuildTaggedProjectsAsync(affected, $"标签重命名：{oldName} → {newName}", cancellationToken);
    }

    public async Task MergeTagsAsync(string sourceName, string targetName, CancellationToken cancellationToken = default)
    {
        ValidateTagName(sourceName);
        ValidateTagName(targetName);
        if (string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var affected = await repository.MergeTagsAsync(sourceName, targetName, cancellationToken);
        await RebuildTaggedProjectsAsync(affected, $"标签合并：{sourceName} → {targetName}", cancellationToken);
    }

    public async Task DeleteUnusedTagAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateTagName(name);
        var summary = await repository.GetTagSummaryAsync(cancellationToken);
        var item = summary.FirstOrDefault(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item is { Count: > 0 })
        {
            throw new InvalidOperationException($"标签「{name}」仍被 {item.Count} 个项目使用，不能删除。");
        }

        await repository.DeleteTagAsync(name, cancellationToken);
    }

    /// <summary>
    /// 标签整理后受影响项目的 Tags 已随关联表更新，这里把项目行、活动与 project.json 一并同步。
    /// 离线项目写 project.json 失败时保留同步队列，由启动重试兜底。
    /// </summary>
    private async Task RebuildTaggedProjectsAsync(IReadOnlyList<long> projectIds, string description, CancellationToken cancellationToken)
    {
        var all = await repository.GetAllAsync(cancellationToken);
        var now = DateTimeOffset.Now;
        var databaseErrors = new List<string>();
        foreach (var project in all.Where(project => projectIds.Contains(project.Id)))
        {
            try
            {
                await repository.SaveAsync(project, new ProjectActivityRecord
                {
                    ProjectId = project.Id,
                    ActivityType = ActivityTypes.TagsReorganized,
                    Description = description,
                    CreatedAt = now
                }, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                databaseErrors.Add($"{project.ProjectCode}: {ex.Message}");
                logger.LogError(ex, "标签整理后数据库更新失败：{ProjectCode}", project.ProjectCode);
                continue;
            }

            try
            {
                await SyncMetadataAsync(project, storage.ResolveProjectPath(project), cancellationToken);
            }
            catch (Exception ex)
            {
                // SaveAsync 已把项目放入 MetadataSyncQueue；离线时由启动重试继续完成。
                logger.LogWarning(ex, "标签整理后档案同步失败（保留同步队列）：{ProjectCode}", project.ProjectCode);
            }
        }

        if (databaseErrors.Count > 0)
        {
            throw new InvalidOperationException("部分项目的标签数据库更新失败：\n" + string.Join("\n", databaseErrors));
        }
    }

    private static void ValidateTagName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, name.Trim(), StringComparison.Ordinal) || name.Length > 32)
        {
            throw new InvalidOperationException("标签名称需为去首尾空白的 1–32 个字符。");
        }
    }

    public async Task<ProjectRecord> UpdateNotesAsync(ProjectRecord project, string notes, CancellationToken cancellationToken = default)
    {
        project.Notes = notes.Trim();
        project.UpdatedAt = DateTimeOffset.Now;
        await repository.SaveAsync(project, new ProjectActivityRecord
        {
            ProjectId = project.Id,
            ActivityType = ActivityTypes.NoteUpdated,
            Description = "更新项目备注",
            CreatedAt = project.UpdatedAt
        }, cancellationToken: cancellationToken);
        await SyncMetadataAsync(project, storage.ResolveProjectPath(project), cancellationToken);
        return project;
    }

    public async Task<ProjectVersionRecord> CreateVersionAsync(ProjectRecord project, string title, string changeSummary, CancellationToken cancellationToken = default)
    {
        var projectDirectory = storage.ResolveProjectPath(project);
        var versions = await repository.GetVersionsAsync(project.Id, cancellationToken);
        var nextNumber = versions.Select(item => VersionNumbers.Parse(item.VersionNumber)).DefaultIfEmpty().Max() + 1;
        // 记录被删但目录还在（用户只删记录保留文件夹、或手工放过目录）时跳过被占用的编号，
        // 否则新版本会复用旧目录、把旧文件当成新版本内容。
        while (Directory.Exists(Path.Combine(projectDirectory, "Versions", $"V{nextNumber:000}")))
        {
            nextNumber++;
        }

        var versionNumber = $"V{nextNumber:000}";
        var now = DateTimeOffset.Now;
        var relativePath = Path.Combine("Versions", versionNumber);
        CreateVersionDirectories(Path.Combine(projectDirectory, relativePath));
        var version = new ProjectVersionRecord
        {
            ProjectId = project.Id,
            VersionNumber = versionNumber,
            Title = string.IsNullOrWhiteSpace(title) ? versionNumber : title.Trim(),
            ChangeSummary = changeSummary.Trim(),
            Status = "Active",
            CreatedAt = now,
            RelativePath = relativePath
        };
        project.CurrentVersion = versionNumber;
        project.UpdatedAt = now;
        await repository.AddVersionAsync(project, version, new ProjectActivityRecord
        {
            ProjectId = project.Id,
            ActivityType = ActivityTypes.VersionCreated,
            Description = $"创建版本 {versionNumber}：{version.Title}",
            CreatedAt = now
        }, cancellationToken);
        await metadataStore.WriteVersionAsync(Path.Combine(projectDirectory, relativePath), new VersionMetadataDocument
        {
            VersionNumber = versionNumber,
            Title = version.Title,
            ChangeSummary = version.ChangeSummary,
            Status = version.Status,
            CreatedAt = now
        }, cancellationToken);
        await SyncMetadataAsync(project, projectDirectory, cancellationToken);
        return version;
    }

    public Task<IReadOnlyList<ProjectVersionRecord>> GetVersionsAsync(ProjectRecord project, CancellationToken cancellationToken = default)
        => repository.GetVersionsAsync(project.Id, cancellationToken);

    /// <summary>
    /// 删除版本。勾选删除目录时先原子移到项目内的隐藏恢复区；
    /// 数据库事务失败则移回，成功后也不永久删除，便于首发版人工恢复。
    /// 唯一约束：项目至少保留一个版本记录；删的是当前版本时回退到剩余的最高版本。
    /// </summary>
    public async Task<ProjectRecord> DeleteVersionAsync(ProjectRecord project, ProjectVersionRecord version, bool deleteDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(version);
        var versions = await repository.GetVersionsAsync(project.Id, cancellationToken);
        // 优先按主键定位；调用方传进来的对象也可能是刚创建、Id 尚未回填的历史数据，这时退回按版本号匹配。
        var target = versions.FirstOrDefault(item => version.Id > 0 && item.Id == version.Id)
            ?? versions.FirstOrDefault(item => string.Equals(item.VersionNumber, version.VersionNumber, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            throw new InvalidOperationException($"数据库中已找不到版本 {version.VersionNumber}，请刷新后重试。");
        }

        if (versions.Count <= 1)
        {
            throw new InvalidOperationException("项目至少需要保留一个版本，无法删除。");
        }

        var projectDirectory = storage.ResolveProjectPath(project);
        var versionDirectory = Path.Combine(projectDirectory, target.RelativePath);
        string? recoveryPath = null;
        try
        {
            await repository.DeleteVersionAsync(project.Id, target.Id,
                $"删除版本 {target.VersionNumber}：{target.Title}" + (deleteDirectory ? "（目录已移至恢复区）" : "（仅删除记录）"),
                beforeDelete: currentProject =>
                {
                    if (!DirectoryVerification.PathsEqual(storage.ResolveProjectPath(currentProject), projectDirectory))
                        throw new InvalidOperationException("项目位置已变化，请刷新后重试。");
                    if (deleteDirectory && Directory.Exists(versionDirectory))
                        recoveryPath = MoveToRecoveryArea(versionDirectory, projectDirectory, "Versions");
                }, cancellationToken: cancellationToken);
        }
        catch
        {
            RestoreFromRecovery(recoveryPath, versionDirectory);
            throw;
        }
        var updated = await repository.GetByIdAsync(project.Id, CancellationToken.None)
            ?? throw new InvalidOperationException("项目已不存在。");
        project.CurrentVersion = updated.CurrentVersion;
        project.UpdatedAt = updated.UpdatedAt;
        await SyncMetadataAsync(updated, projectDirectory, CancellationToken.None);
        logger.LogInformation("版本 {VersionNumber} 已从项目 {ProjectCode} 删除", target.VersionNumber, project.ProjectCode);
        return project;
    }

    public Task<IReadOnlyList<ProjectActivityRecord>> GetActivitiesAsync(ProjectRecord project, CancellationToken cancellationToken = default)
        => repository.GetActivitiesAsync(project.Id, cancellationToken);

    public async Task RetryPendingMetadataAsync(CancellationToken cancellationToken = default)
    {
        foreach (var project in await repository.GetPendingMetadataSyncAsync(cancellationToken))
        {
            try
            {
                await SyncMetadataAsync(project, storage.ResolveProjectPath(project), cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Metadata retry failed for {ProjectCode}", project.ProjectCode);
            }
        }
    }

    public async Task DeleteAsync(ProjectRecord project, bool deleteDirectory, CancellationToken cancellationToken = default)
    {
        string? originalPath = null;
        string? recoveryPath = null;
        try
        {
            if (deleteDirectory)
            {
                originalPath = storage.ResolveProjectPath(project);
                if (Directory.Exists(originalPath))
                    recoveryPath = MoveToRecoveryArea(originalPath, storage.ResolveRoot(project.StorageLocation), "Projects");
            }
            // 移动成功后不再接受取消，避免恢复区已有数据但记录仍在的半状态。
            await repository.DeleteAsync(project.Id, recoveryPath is null ? cancellationToken : CancellationToken.None);
        }
        catch (Exception ex)
        {
            RestoreFromRecovery(recoveryPath, originalPath);
            logger.LogWarning(ex, "Project deletion failed and folder was restored for {ProjectCode}", project.ProjectCode);
            throw;
        }
    }

    private static string MoveToRecoveryArea(string sourcePath, string recoveryRoot, string category)
    {
        var area = Path.Combine(recoveryRoot, ".simflow-recovery", category);
        Directory.CreateDirectory(area);
        var destination = Path.Combine(area,
            $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath))}.{DateTimeOffset.Now:yyyyMMddHHmmss}.{Guid.NewGuid():N}");
        Directory.Move(sourcePath, destination);
        return destination;
    }

    private static void RestoreFromRecovery(string? recoveryPath, string? originalPath)
    {
        if (string.IsNullOrWhiteSpace(recoveryPath) || string.IsNullOrWhiteSpace(originalPath) || !Directory.Exists(recoveryPath)) return;
        if (Directory.Exists(originalPath))
            throw new IOException($"数据库操作失败，且原路径已被占用；数据保留在恢复区：{recoveryPath}");
        Directory.Move(recoveryPath, originalPath);
    }

    private async Task SyncMetadataAsync(ProjectRecord project, string projectDirectory, CancellationToken cancellationToken)
    {
        await metadataStore.WriteProjectAsync(projectDirectory, ProjectMetadataDocument.FromProject(project), cancellationToken);
        await repository.MarkMetadataSyncedAsync(project.Id, cancellationToken);
        project.MetadataSyncState = "Synced";
    }

    private static void ValidateTransition(WorkflowStatus from, WorkflowStatus to, string? reason)
    {
        if (!WorkflowRules.CanTransition(from, to))
        {
            throw new InvalidOperationException($"不允许从 {WorkflowRules.Describe(from)} 切换到 {WorkflowRules.Describe(to)}。");
        }

        if (WorkflowRules.RequiresWaitReason(to) && string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("进入等待状态必须填写等待原因。");
        }
    }

    private static string DescribeTransition(WorkflowStatus from, WorkflowStatus to, string? reason) => (from, to) switch
    {
        (WorkflowStatus.NotStarted, WorkflowStatus.Active) => "项目开始",
        (_, WorkflowStatus.Waiting) => $"进入等待：{reason}",
        (WorkflowStatus.Waiting, WorkflowStatus.Active) => "结束等待，恢复项目",
        (WorkflowStatus.Completed, WorkflowStatus.Active) => "重新启动项目",
        (_, WorkflowStatus.NotStarted) => "状态调整为未开始",
        (_, WorkflowStatus.Completed) => "项目完成",
        _ => $"状态从{WorkflowRules.Describe(from)}调整为{WorkflowRules.Describe(to)}"
    };

    /// <summary>
    /// 项目标准目录结构（2026-09-17 定稿）：
    /// 项目根 = project.json + cover.* + Documents + Versions\Vxxx + Delivery。
    /// 目录里不放任何占位文件；空目录由迁移/复制时同步重建目录结构来保留
    /// （迁移只枚举文件，不重建目录的话空目录会在目标位置消失）。
    /// </summary>
    private static void CreateProjectDirectories(string projectDirectory, string version)
    {
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(Path.Combine(projectDirectory, "Documents"));
        CreateVersionDirectories(Path.Combine(projectDirectory, "Versions", version));
        CreateDeliveryDirectories(projectDirectory);
    }

    /// <summary>一个版本 = 一套自包含的模型/数据/结果，四件套保持齐全，便于版本之间直接对比。</summary>
    private static void CreateVersionDirectories(string versionDirectory)
    {
        Directory.CreateDirectory(versionDirectory);
        Directory.CreateDirectory(Path.Combine(versionDirectory, "3D_Model"));
        Directory.CreateDirectory(Path.Combine(versionDirectory, "CAE_Model"));
        Directory.CreateDirectory(Path.Combine(versionDirectory, "Data"));
        Directory.CreateDirectory(Path.Combine(versionDirectory, "Results"));
    }

    /// <summary>项目级交付物，与版本无关：正式报告 + 报告用图表 + 演示动画。</summary>
    private static void CreateDeliveryDirectories(string projectDirectory)
    {
        var deliveryDirectory = Path.Combine(projectDirectory, "Delivery");
        Directory.CreateDirectory(deliveryDirectory);
        Directory.CreateDirectory(Path.Combine(deliveryDirectory, "Figures"));
        Directory.CreateDirectory(Path.Combine(deliveryDirectory, "Animation"));
    }

    private static List<string> Normalize(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

public sealed class ProjectScannerService(
    IProjectRepository repository,
    IProjectMetadataStore metadataStore,
    IStorageLocationService storage,
    ILogger<ProjectScannerService> logger) : IProjectScannerService
{
    public async Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var result = new ScanResult();
        foreach (var location in Enum.GetValues<StorageLocationCode>())
        {
            var root = storage.ResolveRoot(location);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            IReadOnlyList<string> metadataFiles;
            try
            {
                metadataFiles = Directory.EnumerateFiles(root, "project.json", SearchOption.AllDirectories)
                    .Where(path => !IsRecoveryPath(root, path))
                    .ToList();
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{root}: {ex.Message}");
                continue;
            }

            foreach (var path in metadataFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.ScannedFiles++;
                var metadata = await metadataStore.ReadProjectAsync(path, cancellationToken);
                if (metadata is null)
                {
                    result.Errors.Add($"无法读取：{path}");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(metadata.Id) || string.IsNullOrWhiteSpace(metadata.Name))
                {
                    result.Errors.Add($"档案缺少项目编号或名称：{path}");
                    continue;
                }

                var projectDirectory = Path.GetDirectoryName(path)!;
                var relativePath = new DirectoryInfo(projectDirectory).Name;
                var existing = await repository.GetByCodeAsync(metadata.Id, cancellationToken);
                if (existing is not null)
                {
                    var same = MetadataMatches(existing, metadata, location, relativePath);
                    if (same)
                    {
                        result.UnchangedProjects++;
                        await ReconcileVersionsAsync(existing, projectDirectory, result, cancellationToken);
                    }
                    else
                    {
                        // 同编号的冲突副本在用户明确选择前不属于当前项目；
                        // 继续对账会把该副本的 Versions 误报为真实项目的待补建版本。
                        result.Conflicts.Add(new ScanConflict(
                            metadata.Id,
                            path,
                            "数据库与 project.json 内容不一致，需要人工确认。",
                            existing,
                            metadata,
                            location,
                            relativePath));
                    }
                    continue;
                }

                var project = metadata.ToProject();
                project.StorageLocation = location;
                project.StorageStatus = location == StorageLocationCode.WorkstationArchive ? StorageStatus.Archived : StorageStatus.Working;
                project.RelativePath = relativePath;
                var imported = await repository.ImportAsync(project, new ProjectActivityRecord
                {
                    ProjectId = 0,
                    ActivityType = ActivityTypes.ProjectImported,
                    Description = "从 project.json 扫描恢复",
                    CreatedAt = DateTimeOffset.Now
                }, cancellationToken);
                if (imported)
                {
                    result.ImportedProjects++;
                    // 刚恢复的项目只带一条“当前版本”记录：这里按磁盘上的 Versions 目录把其余版本列出来补建。
                    await ReconcileVersionsAsync(project, projectDirectory, result, cancellationToken);
                }
            }
        }

        logger.LogInformation("Project scan completed: {Imported} imported, {Conflicts} conflicts", result.ImportedProjects, result.Conflicts.Count);
        return result;
    }

    private static bool IsRecoveryPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, ".simflow-recovery", StringComparison.OrdinalIgnoreCase));
    }

    public async Task ResolveConflictAsync(ScanConflict conflict, ScanConflictResolution resolution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        var existing = await repository.GetByCodeAsync(conflict.ProjectCode, cancellationToken)
            ?? throw new InvalidOperationException($"数据库中已找不到项目 {conflict.ProjectCode}，请重新扫描。");
        var projectDirectory = Path.GetDirectoryName(conflict.MetadataPath)
            ?? throw new InvalidOperationException("冲突档案路径无效。");

        if (resolution == ScanConflictResolution.UseMetadata)
        {
            ApplyMetadata(existing, conflict.Metadata, conflict.DiscoveredLocation, conflict.RelativePath);
            await repository.SaveAsync(existing, new ProjectActivityRecord
            {
                ProjectId = existing.Id,
                ActivityType = ActivityTypes.ProjectImported,
                Description = $"扫描冲突处理：采用项目档案 {conflict.MetadataPath}",
                CreatedAt = DateTimeOffset.Now
            }, cancellationToken: cancellationToken);
        }
        else
        {
            var databaseDirectory = storage.ResolveProjectPath(existing);
            if (!PathsEqual(databaseDirectory, projectDirectory))
            {
                // 同一编号出现在另一个物理目录时不能用数据库内容覆盖那个副本；保留原文件并移出扫描范围。
                var rejectedPath = conflict.MetadataPath + $".rejected.{DateTimeOffset.Now:yyyyMMddHHmmss}.bak";
                File.Move(conflict.MetadataPath, rejectedPath, false);
                logger.LogWarning("Duplicate metadata for {ProjectCode} preserved at {RejectedPath}", conflict.ProjectCode, rejectedPath);
                return;
            }
        }

        // 两种选择都把用户确认后的数据库内容写回当前冲突档案，确保下次扫描不重复报告。
        await metadataStore.WriteProjectAsync(projectDirectory, ProjectMetadataDocument.FromProject(existing), cancellationToken);
        await repository.MarkMetadataSyncedAsync(existing.Id, cancellationToken);
        logger.LogInformation("Scan conflict resolved for {ProjectCode} using {Resolution}", conflict.ProjectCode, resolution);
    }

    /// <summary>
    /// 版本记录 ↔ 版本目录对账（只报告，不改数据）。
    ///
    /// 刻意只在 <c>Versions</c> 目录本身存在时才判定"记录在、目录没了"：
    /// 整个 Versions 目录缺失既可能是用户删掉了，也可能是旧结构项目或半拷贝项目，
    /// 分不清就容易把好数据报成异常，所以直接跳过；这类残留记录仍可在版本页手工删除。
    /// </summary>
    private async Task ReconcileVersionsAsync(ProjectRecord project, string projectDirectory, ScanResult result, CancellationToken cancellationToken)
    {
        var versionsRoot = Path.Combine(projectDirectory, "Versions");
        if (!Directory.Exists(versionsRoot))
        {
            return;
        }

        var recorded = await repository.GetVersionsAsync(project.Id, cancellationToken);
        var onDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(versionsRoot))
            {
                var name = Path.GetFileName(directory);
                if (VersionNumbers.IsFolderName(name))
                {
                    onDisk[name] = directory;
                }
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{versionsRoot}: {ex.Message}");
            return;
        }

        foreach (var version in recorded.Where(item => !onDisk.ContainsKey(item.VersionNumber)))
        {
            // 唯一版本缺目录时不提供“清理”：删掉它项目就一个版本都没有了，只能人工看过磁盘再处理。
            var canResolve = recorded.Count > 1;
            result.VersionIssues.Add(new ScanVersionIssue(
                ScanVersionIssueKind.MissingFolder,
                project.ProjectCode,
                project.Name,
                version.VersionNumber,
                version.Title,
                Path.Combine(versionsRoot, version.VersionNumber),
                canResolve
                    ? "该版本的目录已不存在；确认后只删除数据库里的版本记录，不会删除任何文件。"
                    : "该版本的目录已不存在，但它是项目中唯一的版本记录；请先确认磁盘内容，必要时手工处理。",
                canResolve));
        }

        foreach (var pair in onDisk.Where(item => !recorded.Any(version =>
                     string.Equals(version.VersionNumber, item.Key, StringComparison.OrdinalIgnoreCase))))
        {
            var metadata = await metadataStore.ReadVersionAsync(Path.Combine(pair.Value, "version.json"), cancellationToken);
            result.VersionIssues.Add(new ScanVersionIssue(
                ScanVersionIssueKind.UnrecordedFolder,
                project.ProjectCode,
                project.Name,
                pair.Key,
                string.IsNullOrWhiteSpace(metadata?.Title) ? pair.Key : metadata!.Title,
                pair.Value,
                "磁盘上存在该版本目录但数据库没有记录；确认后补建版本记录，目录里的 version.json 会被优先采用。",
                true));
        }
    }

    /// <summary>按用户确认处理一条版本一致性问题。</summary>
    public async Task ResolveVersionIssueAsync(ScanVersionIssue issue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var project = await repository.GetByCodeAsync(issue.ProjectCode, cancellationToken)
            ?? throw new InvalidOperationException($"数据库中已找不到项目 {issue.ProjectCode}，请重新扫描。");

        if (issue.Kind == ScanVersionIssueKind.MissingFolder)
        {
            if (!issue.CanResolve)
            {
                throw new InvalidOperationException($"版本 {issue.VersionNumber} 是项目中唯一的版本记录，不能清理。");
            }

            var versions = await repository.GetVersionsAsync(project.Id, cancellationToken);
            var target = versions.FirstOrDefault(item =>
                string.Equals(item.VersionNumber, issue.VersionNumber, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return;
            }

            await repository.DeleteVersionAsync(project.Id, target.Id,
                $"扫描清理缺失的版本记录 {target.VersionNumber}：{target.Title}",
                beforeDelete: currentProject =>
                {
                    var expected = Path.Combine(storage.ResolveProjectPath(currentProject), target.RelativePath);
                    if (!PathsEqual(expected, issue.DirectoryPath)
                        || Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(expected)!)
                            .Any(path => PathsEqual(path, expected)))
                        throw new InvalidOperationException("版本目录或项目位置已变化，请重新扫描。");
                }, cancellationToken: cancellationToken);
            project = await repository.GetByIdAsync(project.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("项目已不存在。");
        }
        else
        {
            var expectedDirectory = Path.Combine(storage.ResolveProjectPath(project), "Versions", issue.VersionNumber);
            if (!DirectoryVerification.PathsEqual(expectedDirectory, issue.DirectoryPath)
                || !Directory.Exists(issue.DirectoryPath))
                throw new InvalidOperationException("版本目录不属于项目当前正式位置，请重新扫描。");
            var versions = await repository.GetVersionsAsync(project.Id, cancellationToken);
            if (versions.Any(item => string.Equals(item.VersionNumber, issue.VersionNumber, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var metadata = await metadataStore.ReadVersionAsync(Path.Combine(issue.DirectoryPath, "version.json"), cancellationToken);
            var now = DateTimeOffset.Now;
            var createdAt = metadata?.CreatedAt ?? DateTimeOffset.Now;
            if (metadata is null && Directory.Exists(issue.DirectoryPath))
            {
                // 没有 version.json（手工放的目录或旧结构）：用目录的写入时间当版本创建时间。
                createdAt = Directory.GetLastWriteTime(issue.DirectoryPath);
            }

            var version = new ProjectVersionRecord
            {
                ProjectId = project.Id,
                VersionNumber = issue.VersionNumber,
                Title = string.IsNullOrWhiteSpace(metadata?.Title) ? issue.VersionNumber : metadata!.Title,
                Description = metadata?.Description ?? string.Empty,
                ChangeSummary = metadata?.ChangeSummary ?? string.Empty,
                Status = string.IsNullOrWhiteSpace(metadata?.Status) ? "Active" : metadata!.Status,
                CreatedAt = createdAt,
                CompletedAt = metadata?.CompletedAt,
                RelativePath = Path.Combine("Versions", issue.VersionNumber)
            };
            await repository.AddVersionAsync(project, version, new ProjectActivityRecord
            {
                ProjectId = project.Id,
                ActivityType = ActivityTypes.VersionCreated,
                Description = $"扫描恢复版本记录 {issue.VersionNumber}：{version.Title}",
                CreatedAt = now
            }, cancellationToken);
        }

        // 数据库已更新；档案同步失败不影响结果（记录会留在待同步队列里由启动重试兜底）。
        try
        {
            await metadataStore.WriteProjectAsync(storage.ResolveProjectPath(project), ProjectMetadataDocument.FromProject(project), cancellationToken);
            await repository.MarkMetadataSyncedAsync(project.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "扫描处理版本问题后同步档案失败（保留同步队列）：{ProjectCode}", project.ProjectCode);
        }
    }

    internal static bool MetadataMatches(ProjectRecord project, ProjectMetadataDocument metadata,
        StorageLocationCode discoveredLocation, string relativePath)
    {
        return project.UpdatedAt.ToUniversalTime() == metadata.UpdatedAt.ToUniversalTime()
            && string.Equals(project.Name, metadata.Name, StringComparison.Ordinal)
            && string.Equals(project.Description, metadata.Description, StringComparison.Ordinal)
            && string.Equals(project.Requester, metadata.Requester, StringComparison.Ordinal)
            && string.Equals(project.Notes, metadata.Notes, StringComparison.Ordinal)
            && project.WorkflowStatus == metadata.WorkflowStatus
            && project.StorageStatus == metadata.StorageStatus
            && project.SimulationType == metadata.SimulationType
            && project.StorageLocation == discoveredLocation
            && string.Equals(project.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(project.WaitReason, metadata.WaitReason, StringComparison.Ordinal)
            && string.Equals(project.WaitNote, metadata.WaitNote, StringComparison.Ordinal)
            && NullableUtcEquals(project.WaitStartedAt, metadata.WaitStartedAt)
            && project.AccumulatedWaitSeconds == metadata.AccumulatedWaitSeconds
            && NullableUtcEquals(project.StartedAt, metadata.StartedAt)
            && NullableUtcEquals(project.CompletedAt, metadata.CompletedAt)
            && NullableUtcEquals(project.ArchivedAt, metadata.ArchivedAt)
            && project.IsFavorite == metadata.Favorite
            && string.Equals(project.CurrentVersion, metadata.CurrentVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(project.CoverImage, metadata.CoverImage, StringComparison.OrdinalIgnoreCase)
            && SetEquals(project.Tags, metadata.Tags)
            && SetEquals(project.Software, metadata.Software);
    }

    private static bool NullableUtcEquals(DateTimeOffset? left, DateTimeOffset? right)
        => left.HasValue == right.HasValue
           && (!left.HasValue || left.Value.ToUniversalTime() == right!.Value.ToUniversalTime());

    private static bool SetEquals(IEnumerable<string> left, IEnumerable<string> right)
        => new HashSet<string>(left, StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static void ApplyMetadata(ProjectRecord project, ProjectMetadataDocument metadata,
        StorageLocationCode discoveredLocation, string relativePath)
    {
        project.Name = metadata.Name.Trim();
        project.Description = metadata.Description;
        project.Requester = metadata.Requester;
        project.Notes = metadata.Notes;
        project.WorkflowStatus = metadata.WorkflowStatus;
        // 旧档案没有 simulationType：读出来就是 null，按“未分类”落库，不做推断也不报冲突。
        project.SimulationType = metadata.SimulationType;
        project.StorageLocation = discoveredLocation;
        project.StorageStatus = discoveredLocation == StorageLocationCode.WorkstationArchive
            ? StorageStatus.Archived
            : StorageStatus.Working;
        project.RelativePath = relativePath;
        project.WaitReason = metadata.WaitReason;
        project.WaitNote = metadata.WaitNote;
        project.WaitStartedAt = metadata.WaitStartedAt;
        project.AccumulatedWaitSeconds = metadata.AccumulatedWaitSeconds;
        project.CreatedAt = metadata.CreatedAt;
        project.StartedAt = metadata.StartedAt;
        project.CompletedAt = metadata.CompletedAt;
        project.ArchivedAt = discoveredLocation == StorageLocationCode.WorkstationArchive
            ? metadata.ArchivedAt ?? metadata.UpdatedAt
            : null;
        project.UpdatedAt = metadata.UpdatedAt;
        project.IsFavorite = metadata.Favorite;
        project.Tags = metadata.Tags.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        project.Software = metadata.Software.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        project.CurrentVersion = string.IsNullOrWhiteSpace(metadata.CurrentVersion) ? "V001" : metadata.CurrentVersion;
        project.CoverImage = metadata.CoverImage ?? string.Empty;
    }
}

public sealed class ProjectMigrationService(
    IProjectRepository repository,
    IProjectMetadataStore metadataStore,
    IStorageLocationService storage,
    IConfigurationService configuration,
    ILogger<ProjectMigrationService> logger) : IProjectMigrationService
{
    private sealed record FileManifestEntry(string RelativePath, long Length, string Hash);

    public async Task<TransferOperationRecord> MigrateAsync(ProjectRecord project, StorageLocationCode target,
        IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (project.StorageLocation == target) throw new InvalidOperationException("项目已经位于目标位置。");
        var sourcePath = storage.ResolveProjectPath(project);
        if (!Directory.Exists(sourcePath)) throw new DirectoryNotFoundException(sourcePath);
        var targetRoot = storage.ResolveRoot(target);
        if (string.IsNullOrWhiteSpace(targetRoot) || !Directory.Exists(targetRoot)) throw new IOException("目标存储位置离线或未配置。");

        var archiveTime = target == StorageLocationCode.WorkstationArchive ? DateTimeOffset.Now : project.ArchivedAt;
        var targetParent = target == StorageLocationCode.WorkstationArchive
            ? Path.Combine(targetRoot, archiveTime!.Value.ToString("yyyy"), archiveTime.Value.ToString("MM"))
            : targetRoot;
        Directory.CreateDirectory(targetParent);
        var finalPath = Path.Combine(targetParent, project.RelativePath);
        if (Directory.Exists(finalPath)) throw new IOException($"目标项目目录已存在：{finalPath}");
        var transferId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.Now;
        var transfer = new TransferOperationRecord
        {
            Id = transferId,
            ProjectId = project.Id,
            SourceLocation = project.StorageLocation,
            TargetLocation = target,
            SourcePath = sourcePath,
            TargetPath = finalPath,
            State = TransferState.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.SaveTransferAsync(transfer, cancellationToken);

        return await RunTransferAsync(project, transfer, progress, cancellationToken);
    }

    public async Task<TransferOperationRecord> RetryAsync(TransferOperationRecord transfer,
        IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (transfer.State is TransferState.Completed or TransferState.Abandoned or TransferState.CleanupPending)
        {
            throw new InvalidOperationException("当前迁移记录不需要重试。");
        }

        var project = await repository.GetByIdAsync(transfer.ProjectId, cancellationToken)
            ?? throw new InvalidOperationException("迁移对应的项目已不存在。");

        if (Directory.Exists(transfer.TargetPath))
        {
            // 不信任“目标目录存在”这一个条件：手工创建、旧的半成品或源端继续写入
            // 都不得触发主副本切换。
            await VerifyPromotedTargetAsync(project, transfer, cancellationToken);
            await FinalizePromotedTransferAsync(project, transfer, CancellationToken.None);
            await CleanupArchivedSourceIfConfiguredAsync(project, transfer, progress);
            return transfer;
        }

        if (!Directory.Exists(transfer.SourcePath))
        {
            throw new DirectoryNotFoundException($"源目录与目标目录都不存在，无法自动恢复：{transfer.SourcePath}");
        }

        var stagingPath = GetStagingPath(transfer);
        if (Directory.Exists(stagingPath))
        {
            Directory.Delete(stagingPath, true);
        }

        transfer.State = TransferState.Pending;
        transfer.ProcessedBytes = 0;
        transfer.TotalBytes = 0;
        transfer.Error = null;
        transfer.UpdatedAt = DateTimeOffset.Now;
        await repository.UpdateTransferAsync(transfer, cancellationToken);
        return await RunTransferAsync(project, transfer, progress, cancellationToken);
    }

    public async Task AbandonAsync(TransferOperationRecord transfer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (transfer.State is TransferState.Completed or TransferState.Abandoned)
        {
            return;
        }
        if (transfer.State is TransferState.Switched or TransferState.CleanupPending || Directory.Exists(transfer.TargetPath))
        {
            throw new InvalidOperationException("正式目标目录已经生成，不能放弃；请继续恢复或清理源目录。");
        }

        var stagingPath = GetStagingPath(transfer);
        if (Directory.Exists(stagingPath))
        {
            Directory.Delete(stagingPath, true);
        }

        transfer.State = TransferState.Abandoned;
        transfer.Error = null;
        transfer.UpdatedAt = DateTimeOffset.Now;
        await repository.UpdateTransferAsync(transfer, cancellationToken);
    }

    private async Task<TransferOperationRecord> RunTransferAsync(ProjectRecord project, TransferOperationRecord transfer,
        IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        var sourcePath = transfer.SourcePath;
        var finalPath = transfer.TargetPath;
        var stagingPath = GetStagingPath(transfer);
        var promoted = false;

        try
        {
            var files = EnumerateSafeFiles(sourcePath).ToList();
            // 目录要单独枚举：文件复制只为父目录建目录，空目录（例如刚建好、还没放东西的
            // Versions\V001\Results 或 Delivery\Animation）不会被带过去，迁移后结构就不一致了。
            var directories = EnumerateSafeDirectories(sourcePath).ToList();
            var sourceBytes = files.Sum(file => file.Length);
            var availableBytes = await storage.GetAvailableBytesAsync(transfer.TargetLocation, cancellationToken);
            if (availableBytes.HasValue && availableBytes.Value < sourceBytes)
            {
                throw new IOException($"目标空间不足：需要至少 {sourceBytes:N0} 字节，可用 {availableBytes.Value:N0} 字节。");
            }

            transfer.TotalBytes = checked(sourceBytes * 2);
            transfer.State = TransferState.Copying;
            transfer.Error = null;
            transfer.UpdatedAt = DateTimeOffset.Now;
            await repository.UpdateTransferAsync(transfer, cancellationToken);
            Directory.CreateDirectory(stagingPath);
            foreach (var directory in directories)
            {
                Directory.CreateDirectory(Path.Combine(stagingPath, Path.GetRelativePath(sourcePath, directory.FullName)));
            }

            var manifest = new List<FileManifestEntry>(files.Count);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourcePath, file.FullName);
                var destination = Path.Combine(stagingPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                progress?.Report(new MigrationProgress(TransferState.Copying, "正在复制", relative, transfer.ProcessedBytes, transfer.TotalBytes));
                var hash = await CopyWithHashAsync(file.FullName, destination, bytes =>
                {
                    transfer.ProcessedBytes += bytes;
                    progress?.Report(new MigrationProgress(TransferState.Copying, "正在复制", relative, transfer.ProcessedBytes, transfer.TotalBytes));
                }, cancellationToken);
                File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc);
                manifest.Add(new FileManifestEntry(relative, file.Length, hash));
            }

            transfer.State = TransferState.Verifying;
            transfer.UpdatedAt = DateTimeOffset.Now;
            await repository.UpdateTransferAsync(transfer, cancellationToken);
            foreach (var entry in manifest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(stagingPath, entry.RelativePath);
                progress?.Report(new MigrationProgress(TransferState.Verifying, "正在校验", entry.RelativePath, transfer.ProcessedBytes, transfer.TotalBytes));
                var hash = await HashFileAsync(destination, bytes =>
                {
                    transfer.ProcessedBytes += bytes;
                    progress?.Report(new MigrationProgress(TransferState.Verifying, "正在校验", entry.RelativePath, transfer.ProcessedBytes, transfer.TotalBytes));
                }, cancellationToken);
                if (new FileInfo(destination).Length != entry.Length || !string.Equals(hash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"文件校验失败：{entry.RelativePath}");
            }

            // 目标自校验只能证明“复制出来的内容没坏”。提升前必须再与当前源目录整体比对，
            // 否则求解器在复制期间继续写入时会把过期副本切换为主副本。
            await DirectoryVerification.VerifyAsync(sourcePath, stagingPath, exact: true,
                cancellationToken: cancellationToken);

            Directory.Move(stagingPath, finalPath);
            promoted = true;
            transfer.State = TransferState.Switched;
            transfer.UpdatedAt = DateTimeOffset.Now;
            // 目录已经提升为正式目标后，不再响应用户取消；必须把数据库和迁移记录推进到可恢复状态。
            await repository.UpdateTransferAsync(transfer, CancellationToken.None);
            await FinalizePromotedTransferAsync(project, transfer, CancellationToken.None);
            await CleanupArchivedSourceIfConfiguredAsync(project, transfer, progress);
            return transfer;
        }
        catch (OperationCanceledException)
        {
            transfer.State = promoted ? TransferState.Switched : TransferState.Cancelled;
            transfer.Error = promoted ? "目标目录已生成，等待恢复中心完成切换。" : "用户取消迁移。";
            transfer.UpdatedAt = DateTimeOffset.Now;
            await repository.UpdateTransferAsync(transfer, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            transfer.State = promoted ? TransferState.Switched : TransferState.Failed;
            transfer.Error = ex.Message;
            transfer.UpdatedAt = DateTimeOffset.Now;
            await repository.UpdateTransferAsync(transfer, CancellationToken.None);
            logger.LogError(ex, "Project migration failed for {ProjectCode}", project.ProjectCode);
            throw;
        }
    }

    private async Task FinalizePromotedTransferAsync(ProjectRecord project, TransferOperationRecord transfer, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(transfer.TargetPath))
        {
            throw new DirectoryNotFoundException(transfer.TargetPath);
        }

        var previous = project.StorageLocation;
        if (project.StorageLocation != transfer.TargetLocation || project.StorageStatus != ExpectedStorageStatus(transfer.TargetLocation))
        {
            project.StorageLocation = transfer.TargetLocation;
            project.StorageStatus = ExpectedStorageStatus(transfer.TargetLocation);
            project.ArchivedAt = transfer.TargetLocation == StorageLocationCode.WorkstationArchive ? transfer.CreatedAt : null;
            project.UpdatedAt = DateTimeOffset.Now;
            await repository.SaveAsync(project, new ProjectActivityRecord
            {
                ProjectId = project.Id,
                ActivityType = transfer.TargetLocation == StorageLocationCode.WorkstationArchive ? ActivityTypes.ProjectArchived :
                    previous == StorageLocationCode.WorkstationArchive ? ActivityTypes.ProjectRestored : ActivityTypes.ProjectMoved,
                Description = $"项目从 {previous} 迁移到 {transfer.TargetLocation}",
                CreatedAt = project.UpdatedAt
            }, cancellationToken: cancellationToken);
        }

        await metadataStore.WriteProjectAsync(transfer.TargetPath, ProjectMetadataDocument.FromProject(project), cancellationToken);
        await repository.MarkMetadataSyncedAsync(project.Id, cancellationToken);
        transfer.State = TransferState.CleanupPending;
        transfer.ProcessedBytes = transfer.TotalBytes;
        transfer.Error = null;
        transfer.UpdatedAt = DateTimeOffset.Now;
        await repository.UpdateTransferAsync(transfer, cancellationToken);
    }

    private async Task CleanupArchivedSourceIfConfiguredAsync(ProjectRecord project, TransferOperationRecord transfer,
        IProgress<MigrationProgress>? progress)
    {
        if (transfer.TargetLocation != StorageLocationCode.WorkstationArchive
            || !configuration.Current.DeleteSourceAfterArchive)
        {
            progress?.Report(new MigrationProgress(TransferState.CleanupPending, "迁移完成，旧源副本已保留", null, transfer.TotalBytes, transfer.TotalBytes));
            return;
        }

        try
        {
            progress?.Report(new MigrationProgress(TransferState.CleanupPending, "归档已切换，正在重新校验源目录", null, transfer.TotalBytes, transfer.TotalBytes));
            await CleanupSourceAsync(transfer, CancellationToken.None);
            progress?.Report(new MigrationProgress(TransferState.Completed, "归档完成，源目录已删除", null, transfer.TotalBytes, transfer.TotalBytes));
        }
        catch (Exception cleanupError)
        {
            transfer.State = TransferState.CleanupPending;
            transfer.Error = $"归档已完成，但源目录未删除：{cleanupError.Message}";
            transfer.UpdatedAt = DateTimeOffset.Now;
            await repository.UpdateTransferAsync(transfer, CancellationToken.None);
            logger.LogWarning(cleanupError, "Archive completed but source cleanup failed for {ProjectCode}", project.ProjectCode);
            progress?.Report(new MigrationProgress(TransferState.CleanupPending, "归档完成，源目录待清理", null, transfer.TotalBytes, transfer.TotalBytes));
        }
    }

    private async Task VerifyPromotedTargetAsync(ProjectRecord project, TransferOperationRecord transfer, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(transfer.SourcePath))
            throw new DirectoryNotFoundException($"源目录不可访问，无法确认现有目标副本完整：{transfer.SourcePath}");
        var sourceMetadata = await metadataStore.ReadProjectAsync(Path.Combine(transfer.SourcePath, "project.json"), cancellationToken);
        var targetMetadata = await metadataStore.ReadProjectAsync(Path.Combine(transfer.TargetPath, "project.json"), cancellationToken);
        if (sourceMetadata is null || targetMetadata is null
            || !string.Equals(sourceMetadata.Id, project.ProjectCode, StringComparison.Ordinal)
            || !string.Equals(targetMetadata.Id, project.ProjectCode, StringComparison.Ordinal)
            || sourceMetadata.CreatedAt != project.CreatedAt
            || targetMetadata.CreatedAt != project.CreatedAt)
            throw new IOException("目标副本的项目身份无法确认，已停止自动切换。");
        await DirectoryVerification.VerifyAsync(transfer.SourcePath, transfer.TargetPath, exact: true,
            excludedFile: "project.json", cancellationToken: cancellationToken);
    }

    private static StorageStatus ExpectedStorageStatus(StorageLocationCode location)
        => location == StorageLocationCode.WorkstationArchive ? StorageStatus.Archived : StorageStatus.Working;

    private static string GetStagingPath(TransferOperationRecord transfer)
    {
        var targetParent = Path.GetDirectoryName(transfer.TargetPath)
            ?? throw new InvalidOperationException("迁移目标路径无效。");
        var sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(transfer.SourcePath));
        return Path.Combine(targetParent, $".{sourceName}.{transfer.Id}.partial");
    }

    public async Task CleanupSourceAsync(TransferOperationRecord transfer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        var current = (await repository.GetIncompleteTransfersAsync(cancellationToken))
            .SingleOrDefault(item => item.Id == transfer.Id);
        if (current is null || current.State != TransferState.CleanupPending)
            throw new InvalidOperationException("当前迁移不处于待清理状态，请刷新恢复中心。");

        // 只信任数据库中的迁移记录，拒绝调用方用同一 Id 替换路径或项目身份。
        if (current.ProjectId != transfer.ProjectId
            || current.SourceLocation != transfer.SourceLocation
            || current.TargetLocation != transfer.TargetLocation
            || !DirectoryVerification.PathsEqual(current.SourcePath, transfer.SourcePath)
            || !DirectoryVerification.PathsEqual(current.TargetPath, transfer.TargetPath))
        {
            throw new InvalidOperationException("迁移记录与当前请求不一致，已停止清理源目录。");
        }

        var project = await repository.GetByIdAsync(current.ProjectId, cancellationToken)
            ?? throw new InvalidOperationException("迁移对应的项目已不存在，已停止清理源目录。");
        if (project.StorageLocation != current.TargetLocation
            || !DirectoryVerification.PathsEqual(storage.ResolveProjectPath(project), current.TargetPath))
        {
            throw new InvalidOperationException("项目当前主路径不是迁移目标，已停止清理源目录。");
        }

        var targetMetadata = await metadataStore.ReadProjectAsync(Path.Combine(current.TargetPath, "project.json"), cancellationToken);
        if (targetMetadata is null
            || !string.Equals(targetMetadata.Id, project.ProjectCode, StringComparison.Ordinal)
            || targetMetadata.CreatedAt != project.CreatedAt)
        {
            throw new IOException("目标副本的项目身份无法确认，已停止清理源目录。");
        }

        if (Directory.Exists(current.SourcePath))
        {
            // project.json 因主存储位置已经切换而允许不同，其余目录和普通文件必须再次完整比对。
            await VerifyPromotedTargetAsync(project, current, cancellationToken);
            Directory.Delete(current.SourcePath, true);
        }

        current.State = TransferState.Completed;
        current.Error = null;
        current.UpdatedAt = DateTimeOffset.Now;
        await repository.UpdateTransferAsync(current, cancellationToken);
        transfer.State = current.State;
        transfer.Error = null;
        transfer.UpdatedAt = current.UpdatedAt;
    }

    public Task<IReadOnlyList<TransferOperationRecord>> GetIncompleteAsync(CancellationToken cancellationToken = default)
        => repository.GetIncompleteTransfersAsync(cancellationToken);

    private static IEnumerable<FileInfo> EnumerateSafeFiles(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"V0.1 不支持目录连接或符号链接：{directory.FullName}");
            foreach (var file in directory.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"V0.1 不支持符号链接文件：{file.FullName}");
                yield return file;
            }
            foreach (var child in directory.EnumerateDirectories()) pending.Push(child);
        }
    }

    /// <summary>
    /// 枚举源目录下所有子目录（不含根），用于在目标位置重建目录结构以保留空目录。
    /// 与 <see cref="EnumerateSafeFiles"/> 一致：遇到目录连接/符号链接直接失败，不跟随。
    /// </summary>
    private static IEnumerable<DirectoryInfo> EnumerateSafeDirectories(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"V0.1 不支持目录连接或符号链接：{directory.FullName}");
            foreach (var child in directory.EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"V0.1 不支持目录连接或符号链接：{child.FullName}");
                yield return child;
                pending.Push(child);
            }
        }
    }

    private static async Task<string> CopyWithHashAsync(string source, string destination, Action<int> progress, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            progress(read);
        }
        await output.FlushAsync(cancellationToken);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> HashFileAsync(string path, Action<int> progress, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            progress(read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
