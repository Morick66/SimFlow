using Microsoft.Extensions.Logging.Abstractions;
using SimFlow.Models;
using SimFlow.Services;
using System.Text.Json.Nodes;
using Xunit;

namespace SimFlow.Tests;

public sealed class ProjectLifecycleTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SimFlowTests", Guid.NewGuid().ToString("N"));
    private IProjectService _projects = null!;
    private IProjectScannerService _scanner = null!;
    private IProjectMigrationService _migration = null!;
    private IProjectRepository _repository = null!;
    private IConfigurationService _configuration = null!;
    private DatabaseService _database = null!;
    private AppPaths _paths = null!;

    /// <summary>定稿结构（2026-09-17）中随项目创建即存在的标准目录，相对项目根。</summary>
    private static readonly string[] StandardProjectDirectories =
    [
        "Documents",
        Path.Combine("Versions", "V001"),
        Path.Combine("Versions", "V001", "3D_Model"),
        Path.Combine("Versions", "V001", "CAE_Model"),
        Path.Combine("Versions", "V001", "Data"),
        Path.Combine("Versions", "V001", "Results"),
        "Delivery",
        Path.Combine("Delivery", "Figures"),
        Path.Combine("Delivery", "Animation")
    ];

    /// <summary>定稿结构里随项目创建即存在的版本数据目录，测试用它放样本文件。</summary>
    private static string VersionDataDirectory(string projectPath) => Path.Combine(projectPath, "Versions", "V001", "Data");

    /// <summary>自动化测试各自使用独立临时目录；本机 Work 根由测试显式配置。</summary>
    private string LocalWorkRoot => Path.Combine(_root, "LocalWork");

    public async Task InitializeAsync()
    {
        _paths = new AppPaths
        {
            AppDataDirectory = Path.Combine(_root, "AppData"),
            DatabasePath = Path.Combine(_root, "AppData", "simflow.db"),
            ConfigurationPath = Path.Combine(_root, "AppData", "config.json"),
            LogsDirectory = Path.Combine(_root, "AppData", "logs")
        };
        _paths.EnsureDirectories();
        _configuration = new ConfigurationService(_paths, NullLogger<ConfigurationService>.Instance);
        await _configuration.InitializeAsync();
        // 配置默认不再给本机 Work 指定路径，测试必须显式配置。
        _configuration.Current.LocalWorkRoot = LocalWorkRoot;
        _configuration.Current.WorkstationWorkRoot = Path.Combine(_root, "WorkstationWork");
        _configuration.Current.WorkstationArchiveRoot = Path.Combine(_root, "Archive");
        Directory.CreateDirectory(_configuration.Current.WorkstationWorkRoot);
        Directory.CreateDirectory(_configuration.Current.WorkstationArchiveRoot);
        await _configuration.SaveAsync(_configuration.Current);
        var database = new DatabaseService(_paths, NullLogger<DatabaseService>.Instance);
        await database.InitializeAsync();
        _database = database;
        _repository = new ProjectRepository(database);
        var metadata = new ProjectMetadataStore(NullLogger<ProjectMetadataStore>.Instance);
        var storage = new StorageLocationService(_configuration);
        _projects = new ProjectService(_repository, metadata, storage, NullLogger<ProjectService>.Instance);
        _scanner = new ProjectScannerService(_repository, metadata, storage, NullLogger<ProjectScannerService>.Instance);
        _migration = new ProjectMigrationService(_repository, metadata, storage, _configuration,
            NullLogger<ProjectMigrationService>.Instance);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateProject_WritesDatabaseAndPortableMetadata()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest
        {
            Name = "短耐分析",
            Requester = "张三",
            Tags = ["短耐", "150kA"],
            Software = ["Adams"],
            StartImmediately = false
        });

        Assert.Matches(@"^SIM_\d{8}_001$", project.ProjectCode);
        Assert.Equal(WorkflowStatus.NotStarted, project.WorkflowStatus);
        var projectPath = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        Assert.True(File.Exists(Path.Combine(projectPath, "project.json")));
        Assert.True(File.Exists(Path.Combine(projectPath, "Versions", "V001", "version.json")));

        // 定稿结构：每个标准目录都必须存在。这里刻意不放任何占位文件，
        // 空目录靠迁移/复制时重建目录结构保留（见 Migration_VerifiesThenWaitsForExplicitCleanup）。
        foreach (var relative in StandardProjectDirectories)
        {
            Assert.True(Directory.Exists(Path.Combine(projectPath, relative)), relative);
        }

        Assert.False(File.Exists(Path.Combine(projectPath, "README.txt")));

        // 旧结构里的 ProjectData / Export / Simulation_Model 已从创建逻辑移除。
        Assert.False(Directory.Exists(Path.Combine(projectPath, "ProjectData")));
        Assert.False(Directory.Exists(Path.Combine(projectPath, "Export")));
        Assert.False(Directory.Exists(Path.Combine(projectPath, "Versions", "V001", "Simulation_Model")));

        Assert.Single(await _projects.GetProjectsAsync());
    }

    [Fact]
    public async Task CreateVersion_ProducesStandardVersionDirectories()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "版本结构" });
        var version = await _projects.CreateVersionAsync(project, "优化方案", "把主拉簧刚度提高 10%");

        Assert.Equal("V002", version.VersionNumber);
        Assert.Equal(Path.Combine("Versions", "V002"), version.RelativePath);
        Assert.Equal("V002", project.CurrentVersion);
        // 创建时就要回填主键：调用方随后可能直接拿这个对象去删除/更新版本。
        Assert.True(version.Id > 0, "创建版本应回填数据库主键");

        var projectPath = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        var versionPath = Path.Combine(projectPath, version.RelativePath);
        Assert.True(File.Exists(Path.Combine(versionPath, "version.json")));
        foreach (var name in new[] { "3D_Model", "CAE_Model", "Data", "Results" })
        {
            Assert.True(Directory.Exists(Path.Combine(versionPath, name)), name);
        }

        // 新建版本不会动到已有版本。
        Assert.True(Directory.Exists(Path.Combine(projectPath, "Versions", "V001", "Data")));
    }

    [Fact]
    public async Task Lifecycle_AllowsStatusCorrectionsAndKeepsTimestampsConsistent()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "机构优化", StartImmediately = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _projects.ChangeStatusAsync(project, WorkflowStatus.NotStarted));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _projects.ChangeStatusAsync(project, WorkflowStatus.Waiting));

        // 允许直接完成，也允许从完成重新启动；重新启动后完成时间清空、开始时间重新计时。
        await _projects.ChangeStatusAsync(project, WorkflowStatus.Completed);
        Assert.NotNull(project.CompletedAt);
        await _projects.ChangeStatusAsync(project, WorkflowStatus.Active);
        Assert.NotNull(project.StartedAt);
        Assert.Null(project.CompletedAt);

        await _projects.ChangeStatusAsync(project, WorkflowStatus.Waiting, "等待试验", "9月20日试验");
        Assert.NotNull(project.WaitStartedAt);

        // 任意状态可纠正为未开始；当前生命周期时间清空，但历史记录仍保留。
        await _projects.ChangeStatusAsync(project, WorkflowStatus.NotStarted);
        Assert.Null(project.WaitStartedAt);
        Assert.Null(project.StartedAt);
        Assert.Null(project.CompletedAt);

        await _projects.ChangeStatusAsync(project, WorkflowStatus.Waiting, "等待参数");
        await _projects.ChangeStatusAsync(project, WorkflowStatus.Completed);
        Assert.NotNull(project.CompletedAt);
        Assert.True((await _projects.GetActivitiesAsync(project)).Count >= 7);
    }

    [Fact]
    public async Task Scanner_DoesNotOverwriteConflictingProject()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "原始名称" });
        var path = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode, "project.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json["name"] = "外部修改名称";
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var result = await _scanner.ScanAsync();
        Assert.Single(result.Conflicts);
        Assert.Equal("原始名称", (await _repository.GetByCodeAsync(project.ProjectCode))!.Name);
    }

    [Fact]
    public async Task Scanner_ConflictCopyDoesNotInjectVersionIssues()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "正式项目" });
        var duplicate = Path.Combine(_configuration.Current.WorkstationWorkRoot, project.ProjectCode);
        Directory.CreateDirectory(Path.Combine(duplicate, "Versions", "V999"));
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(LocalWorkRoot, project.ProjectCode, "project.json")))!.AsObject();
        metadata["name"] = "冲突副本";
        await File.WriteAllTextAsync(Path.Combine(duplicate, "project.json"), metadata.ToJsonString());

        var result = await _scanner.ScanAsync();

        Assert.Single(result.Conflicts);
        Assert.Empty(result.VersionIssues);
        Assert.DoesNotContain(await _repository.GetVersionsAsync(project.Id),
            version => version.VersionNumber == "V999");
    }

    [Fact]
    public async Task Scanner_ResolvesConflictWithExplicitChoice()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "数据库名称", Tags = ["数据库标签"] });
        var path = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode, "project.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json["name"] = "档案名称";
        json["tags"] = new JsonArray("档案标签");
        json["updatedAt"] = DateTimeOffset.Now.AddMinutes(1);
        await File.WriteAllTextAsync(path, json.ToJsonString());

        var conflict = Assert.Single((await _scanner.ScanAsync()).Conflicts);
        await _scanner.ResolveConflictAsync(conflict, ScanConflictResolution.UseMetadata);

        var restored = await _repository.GetByCodeAsync(project.ProjectCode);
        Assert.NotNull(restored);
        Assert.Equal("档案名称", restored!.Name);
        Assert.Equal(["档案标签"], restored.Tags);
        Assert.Equal("档案名称", JsonNode.Parse(await File.ReadAllTextAsync(path))!["name"]!.GetValue<string>());

        json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json["name"] = "再次外部修改";
        json["updatedAt"] = DateTimeOffset.Now.AddMinutes(2);
        await File.WriteAllTextAsync(path, json.ToJsonString());
        conflict = Assert.Single((await _scanner.ScanAsync()).Conflicts);
        await _scanner.ResolveConflictAsync(conflict, ScanConflictResolution.KeepDatabase);
        Assert.Equal("档案名称", JsonNode.Parse(await File.ReadAllTextAsync(path))!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Scanner_RebuildsCoreDatabaseAfterDatabaseDeletion()
    {
        var created = await _projects.CreateAsync(new CreateProjectRequest
        {
            Name = "数据库重建",
            Requester = "测试用户",
            Description = "从 project.json 恢复",
            Tags = ["恢复", "中文标签"],
            Software = ["Adams"],
            IsFavorite = true
        });

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_paths.DatabasePath);
        File.Delete(_paths.DatabasePath + "-wal");
        File.Delete(_paths.DatabasePath + "-shm");

        var rebuiltDatabase = new DatabaseService(_paths, NullLogger<DatabaseService>.Instance);
        await rebuiltDatabase.InitializeAsync();
        var rebuiltRepository = new ProjectRepository(rebuiltDatabase);
        var scanner = new ProjectScannerService(
            rebuiltRepository,
            new ProjectMetadataStore(NullLogger<ProjectMetadataStore>.Instance),
            new StorageLocationService(_configuration),
            NullLogger<ProjectScannerService>.Instance);

        var result = await scanner.ScanAsync();
        Assert.Equal(1, result.ImportedProjects);
        Assert.Empty(result.Conflicts);
        Assert.Empty(result.Errors);
        var restored = await rebuiltRepository.GetByCodeAsync(created.ProjectCode);
        Assert.NotNull(restored);
        Assert.Equal(created.Name, restored!.Name);
        Assert.Equal(created.Requester, restored.Requester);
        Assert.Equal(created.Description, restored.Description);
        Assert.Equal(created.Tags, restored.Tags);
        Assert.Equal(created.Software, restored.Software);
        Assert.True(restored.IsFavorite);
    }

    [Fact]
    public async Task Scanner_ReportsCorruptJsonWithoutStoppingOtherImports()
    {
        var corruptDirectory = Path.Combine(_configuration.Current.LocalWorkRoot, "SIM_CORRUPT");
        Directory.CreateDirectory(corruptDirectory);
        await File.WriteAllTextAsync(Path.Combine(corruptDirectory, "project.json"), "{not-json");
        var valid = await _projects.CreateAsync(new CreateProjectRequest { Name = "有效项目" });

        var result = await _scanner.ScanAsync();
        Assert.Contains(result.Errors, error => error.Contains("SIM_CORRUPT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("有效项目", (await _repository.GetByCodeAsync(valid.ProjectCode))!.Name);
    }

    [Fact]
    public async Task Migration_VerifiesThenAllowsExplicitCleanup()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "迁移测试", Description = "只使用临时测试目录" });
        var source = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        var target = Path.Combine(_configuration.Current.WorkstationWorkRoot, project.ProjectCode);
        await File.WriteAllBytesAsync(Path.Combine(VersionDataDirectory(source), "sample.bin"), Enumerable.Range(0, 8192).Select(value => (byte)(value % 251)).ToArray());
        var deepDirectory = Path.Combine(VersionDataDirectory(source), "中文目录", "第一级", "第二级", "第三级");
        Directory.CreateDirectory(deepDirectory);
        await File.WriteAllTextAsync(Path.Combine(deepDirectory, "仿真结果 数据.txt"), "深层中文路径");
        var transfer = await _migration.MigrateAsync(project, StorageLocationCode.WorkstationWork);
        Assert.Equal(TransferState.CleanupPending, transfer.State);
        Assert.True(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(VersionDataDirectory(target), "sample.bin")));
        Assert.Equal(
            "深层中文路径",
            await File.ReadAllTextAsync(Path.Combine(
                VersionDataDirectory(target), "中文目录", "第一级", "第二级", "第三级", "仿真结果 数据.txt")));

        // 空目录也必须迁移过去：源里 Documents、Delivery\Animation、Versions\V001\Results 都是空的，
        // 文件复制不会创建它们，必须靠目录重建（否则迁移后结构与源不一致）。
        Assert.True(Directory.Exists(Path.Combine(target, "Documents")));
        Assert.True(Directory.Exists(Path.Combine(target, "Delivery", "Animation")));
        Assert.True(Directory.Exists(Path.Combine(target, "Delivery", "Figures")));
        Assert.True(Directory.Exists(Path.Combine(target, "Versions", "V001", "Results")));
        await _migration.CleanupSourceAsync(transfer);
        Assert.False(Directory.Exists(source));
        Assert.Equal(TransferState.Completed, transfer.State);
        Assert.Empty(await _migration.GetIncompleteAsync());
    }

    [Fact]
    public async Task Archive_DeletesSourceAfterVerifiedSwitchByDefault()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "自动清理归档" });
        var source = Path.Combine(LocalWorkRoot, project.ProjectCode);
        await File.WriteAllTextAsync(Path.Combine(VersionDataDirectory(source), "result.bin"), "verified result");

        var transfer = await _migration.MigrateAsync(project, StorageLocationCode.WorkstationArchive);

        Assert.Equal(TransferState.Completed, transfer.State);
        Assert.False(Directory.Exists(source));
        Assert.Equal("verified result", await File.ReadAllTextAsync(
            Path.Combine(transfer.TargetPath, "Versions", "V001", "Data", "result.bin")));
        var archived = (await _repository.GetByIdAsync(project.Id))!;
        Assert.Equal(StorageLocationCode.WorkstationArchive, archived.StorageLocation);
        Assert.Equal(StorageStatus.Archived, archived.StorageStatus);
        Assert.Empty(await _migration.GetIncompleteAsync());
    }

    [Fact]
    public async Task Archive_KeepsSourceWhenAutomaticCleanupIsDisabled()
    {
        _configuration.Current.DeleteSourceAfterArchive = false;
        await _configuration.SaveAsync(_configuration.Current);
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "保留归档源副本" });
        var source = Path.Combine(LocalWorkRoot, project.ProjectCode);

        var transfer = await _migration.MigrateAsync(project, StorageLocationCode.WorkstationArchive);

        Assert.Equal(TransferState.CleanupPending, transfer.State);
        Assert.True(Directory.Exists(source));
        Assert.True(Directory.Exists(transfer.TargetPath));
        Assert.Single(await _migration.GetIncompleteAsync());
    }

    [Fact]
    public async Task Archive_CleanupFailureKeepsArchivedTargetAsPrimaryAndSourceForRetry()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "归档清理失败" });
        var source = Path.Combine(LocalWorkRoot, project.ProjectCode);
        var sourceFile = Path.Combine(VersionDataDirectory(source), "changing.bin");
        await File.WriteAllTextAsync(sourceFile, "before");
        var changed = false;
        var progress = new CallbackProgress(value =>
        {
            if (!changed && value.Message.Contains("正在重新校验源目录", StringComparison.Ordinal))
            {
                File.WriteAllText(sourceFile, "changed after switch");
                changed = true;
            }
        });

        var transfer = await _migration.MigrateAsync(project, StorageLocationCode.WorkstationArchive, progress);

        Assert.True(changed);
        Assert.Equal(TransferState.CleanupPending, transfer.State);
        Assert.NotNull(transfer.Error);
        Assert.Contains("归档已完成，但源目录未删除", transfer.Error);
        Assert.True(Directory.Exists(source));
        Assert.True(Directory.Exists(transfer.TargetPath));
        var archived = (await _repository.GetByIdAsync(project.Id))!;
        Assert.Equal(StorageLocationCode.WorkstationArchive, archived.StorageLocation);
        Assert.Equal(StorageStatus.Archived, archived.StorageStatus);
    }

    [Fact]
    public async Task Migration_StopsBeforeCopyWhenTargetSpaceIsInsufficient()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "空间不足" });
        var source = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        await File.WriteAllBytesAsync(Path.Combine(VersionDataDirectory(source), "large.bin"), new byte[4096]);
        var storage = new AvailableSpaceOverrideStorage(new StorageLocationService(_configuration), 0);
        var migration = new ProjectMigrationService(
            _repository,
            new ProjectMetadataStore(NullLogger<ProjectMetadataStore>.Instance),
            storage,
            _configuration,
            NullLogger<ProjectMigrationService>.Instance);

        var error = await Assert.ThrowsAsync<IOException>(
            () => migration.MigrateAsync(project, StorageLocationCode.WorkstationWork));

        Assert.Contains("目标空间不足", error.Message);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(Path.Combine(_configuration.Current.WorkstationWorkRoot, project.ProjectCode)));
        var transfer = Assert.Single(await migration.GetIncompleteAsync());
        Assert.Equal(TransferState.Failed, transfer.State);
        Assert.Equal(0, transfer.ProcessedBytes);
    }

    [Fact]
    public async Task Migration_CancellationKeepsSourceAndCanAbandonPartialCopy()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "取消迁移" });
        var source = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        await File.WriteAllBytesAsync(Path.Combine(VersionDataDirectory(source), "cancel.bin"), new byte[1024 * 1024]);
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(value =>
        {
            if (value.State == TransferState.Copying)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _migration.MigrateAsync(project, StorageLocationCode.WorkstationWork, progress, cancellation.Token));

        var transfer = Assert.Single(await _migration.GetIncompleteAsync());
        Assert.Equal(TransferState.Cancelled, transfer.State);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(transfer.TargetPath));
        var partial = Path.Combine(
            Path.GetDirectoryName(transfer.TargetPath)!,
            $".{project.ProjectCode}.{transfer.Id}.partial");
        Assert.True(Directory.Exists(partial));

        await _migration.AbandonAsync(transfer);
        Assert.Equal(TransferState.Abandoned, transfer.State);
        Assert.False(Directory.Exists(partial));
        Assert.DoesNotContain(await _migration.GetIncompleteAsync(), item => item.Id == transfer.Id);
    }

    [Fact]
    public async Task Migration_LockedSourceFileFailsWithoutSwitchingPrimaryCopy()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "占用文件" });
        var source = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        var lockedPath = Path.Combine(VersionDataDirectory(source), "locked.bin");
        await File.WriteAllBytesAsync(lockedPath, new byte[4096]);
        await using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAnyAsync<IOException>(
            () => _migration.MigrateAsync(project, StorageLocationCode.WorkstationWork));

        var transfer = Assert.Single(await _migration.GetIncompleteAsync());
        Assert.Equal(TransferState.Failed, transfer.State);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(transfer.TargetPath));
    }

    [Fact]
    public async Task Migration_SourceChangedDuringCopyDoesNotPromoteStaleTarget()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "迁移期间写入" });
        var source = Path.Combine(LocalWorkRoot, project.ProjectCode);
        var sourceFile = Path.Combine(VersionDataDirectory(source), "changing.bin");
        await File.WriteAllTextAsync(sourceFile, "before");
        var changed = false;
        var progress = new CallbackProgress(value =>
        {
            if (!changed && value.State == TransferState.Verifying)
            {
                File.WriteAllText(sourceFile, "after!");
                changed = true;
            }
        });

        await Assert.ThrowsAsync<IOException>(() =>
            _migration.MigrateAsync(project, StorageLocationCode.WorkstationWork, progress));

        var transfer = Assert.Single(await _migration.GetIncompleteAsync());
        Assert.Equal(TransferState.Failed, transfer.State);
        Assert.False(Directory.Exists(transfer.TargetPath));
        Assert.Equal(StorageLocationCode.LocalWork, (await _repository.GetByIdAsync(project.Id))!.StorageLocation);
    }

    [Fact]
    public async Task Migration_RetryRejectsUnverifiedExistingTarget()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "目标冲突" });
        var source = Path.Combine(LocalWorkRoot, project.ProjectCode);
        var target = Path.Combine(_configuration.Current.WorkstationWorkRoot, project.ProjectCode);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "project.json"), "{}");
        var transfer = new TransferOperationRecord
        {
            Id = Guid.NewGuid().ToString("N"), ProjectId = project.Id,
            SourceLocation = StorageLocationCode.LocalWork, TargetLocation = StorageLocationCode.WorkstationWork,
            SourcePath = source, TargetPath = target, State = TransferState.Switched,
            CreatedAt = DateTimeOffset.Now, UpdatedAt = DateTimeOffset.Now
        };
        await _repository.SaveTransferAsync(transfer);

        await Assert.ThrowsAsync<IOException>(() => _migration.RetryAsync(transfer));

        Assert.Equal(StorageLocationCode.LocalWork, (await _repository.GetByIdAsync(project.Id))!.StorageLocation);
        Assert.Equal(TransferState.Switched, Assert.Single(await _migration.GetIncompleteAsync()).State);
    }

    [Fact]
    public async Task Migration_RetriesInterruptedCopyAndCanAbandonStaging()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "恢复迁移" });
        var source = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        var target = Path.Combine(_configuration.Current.WorkstationWorkRoot, project.ProjectCode);
        var interrupted = new TransferOperationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectId = project.Id,
            SourceLocation = StorageLocationCode.LocalWork,
            TargetLocation = StorageLocationCode.WorkstationWork,
            SourcePath = source,
            TargetPath = target,
            State = TransferState.Cancelled,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        await _repository.SaveTransferAsync(interrupted);
        var partial = Path.Combine(_configuration.Current.WorkstationWorkRoot, $".{project.ProjectCode}.{interrupted.Id}.partial");
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, "orphan.tmp"), "partial");

        Assert.Contains(await _migration.GetIncompleteAsync(), item => item.Id == interrupted.Id);
        var retried = await _migration.RetryAsync(interrupted);
        Assert.Equal(TransferState.CleanupPending, retried.State);
        Assert.False(Directory.Exists(partial));
        Assert.True(Directory.Exists(target));

        var abandoned = new TransferOperationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectId = project.Id,
            SourceLocation = StorageLocationCode.WorkstationWork,
            TargetLocation = StorageLocationCode.LocalWork,
            SourcePath = target,
            TargetPath = Path.Combine(_configuration.Current.LocalWorkRoot, "abandoned-target"),
            State = TransferState.Failed,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        await _repository.SaveTransferAsync(abandoned);
        var abandonedPartial = Path.Combine(_configuration.Current.LocalWorkRoot, $".{project.ProjectCode}.{abandoned.Id}.partial");
        Directory.CreateDirectory(abandonedPartial);
        await _migration.AbandonAsync(abandoned);
        Assert.Equal(TransferState.Abandoned, abandoned.State);
        Assert.False(Directory.Exists(abandonedPartial));
        Assert.DoesNotContain(await _migration.GetIncompleteAsync(), item => item.Id == abandoned.Id);
    }

    [Fact]
    public async Task UpdateInfo_EditsFieldsAndRewritesMetadata()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest
        {
            Name = "原始名称",
            Requester = "张三",
            Tags = ["短耐"],
            Software = ["Adams"]
        });

        var updated = await _projects.UpdateInfoAsync(project, new UpdateProjectInfoRequest
        {
            Name = "  修改后的名称  ",
            Requester = "李四",
            Description = "新的描述",
            Tags = ["短耐", "150kA", "短耐"],
            Software = ["Adams", "ANSYS Maxwell"]
        });

        Assert.Equal("修改后的名称", updated.Name);
        Assert.Equal("李四", updated.Requester);
        Assert.Equal("新的描述", updated.Description);
        Assert.Equal(new[] { "短耐", "150kA" }, updated.Tags);
        Assert.Equal(new[] { "Adams", "ANSYS Maxwell" }, updated.Software);
        Assert.Equal("Synced", updated.MetadataSyncState);

        // 项目编号与目录名不随名称改变
        Assert.Matches(@"^SIM_\d{8}_001$", updated.ProjectCode);
        var projectPath = Path.Combine(_configuration.Current.LocalWorkRoot, updated.ProjectCode);
        Assert.True(Directory.Exists(projectPath));

        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(projectPath, "project.json")))!.AsObject();
        Assert.Equal("修改后的名称", json["name"]!.GetValue<string>());
        Assert.Equal("李四", json["requester"]!.GetValue<string>());
        Assert.Equal("新的描述", json["description"]!.GetValue<string>());
        Assert.Equal(2, json["tags"]!.AsArray().Count);
        Assert.Equal(2, json["software"]!.AsArray().Count);

        // 关联表被重建，重复标签被去掉
        var reloaded = await _repository.GetByCodeAsync(updated.ProjectCode);
        Assert.NotNull(reloaded);
        Assert.Equal(new[] { "短耐", "150kA" }.OrderBy(item => item, StringComparer.Ordinal),
            reloaded!.Tags.OrderBy(item => item, StringComparer.Ordinal));
        Assert.Equal(new[] { "Adams", "ANSYS Maxwell" }.OrderBy(item => item, StringComparer.Ordinal),
            reloaded.Software.OrderBy(item => item, StringComparer.Ordinal));

        var activity = (await _projects.GetActivitiesAsync(updated)).First(item => item.ActivityType == ActivityTypes.ProjectUpdated);
        Assert.Contains("项目名称", activity.Description);
        Assert.Contains("标签", activity.Description);
        Assert.Contains("软件", activity.Description);
    }

    [Fact]
    public async Task UpdateInfo_RejectsEmptyName()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "保持名称" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _projects.UpdateInfoAsync(project, new UpdateProjectInfoRequest { Name = "   " }));
        Assert.Equal("保持名称", project.Name);
    }

    [Fact]
    public async Task UpdateInfo_SkipsActivityWhenNothingChanged()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest
        {
            Name = "无变化",
            Requester = "张三",
            Tags = ["短耐"],
            Software = ["Adams"]
        });
        var activityCount = (await _projects.GetActivitiesAsync(project)).Count;
        var updatedAt = project.UpdatedAt;

        await _projects.UpdateInfoAsync(project, new UpdateProjectInfoRequest
        {
            Name = "无变化",
            Requester = "张三",
            Tags = ["短耐"],
            Software = ["Adams"]
        });

        Assert.Equal(activityCount, (await _projects.GetActivitiesAsync(project)).Count);
        Assert.Equal(updatedAt, project.UpdatedAt);
    }

    [Fact]
    public async Task GetByCode_IsCaseInsensitiveAndKeepsAssociations()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest
        {
            Name = "大小写查找",
            Tags = ["短耐"],
            Software = ["Adams"]
        });

        // 直查路径必须保持原先内存比较的忽略大小写行为，并且不能丢掉标签与软件关联。
        var found = await _repository.GetByCodeAsync(project.ProjectCode.ToLowerInvariant());
        Assert.NotNull(found);
        Assert.Equal(project.Id, found!.Id);
        Assert.Equal(new[] { "短耐" }, found.Tags);
        Assert.Equal(new[] { "Adams" }, found.Software);

        Assert.Null(await _repository.GetByCodeAsync("SIM_19700101_999"));
    }

    /// <summary>
    /// 状态历史的每一行应当描述一段真实区间：结束时间不得等于开始时间，
    /// 时长必须与区间一致，只有当前状态那一行保持 open。
    /// </summary>
    [Fact]
    public async Task StatusHistory_ClosesPreviousIntervalAndTracksWaitDuration()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "等待统计", StartImmediately = false });
        await _projects.ChangeStatusAsync(project, WorkflowStatus.Active);
        await _projects.ChangeStatusAsync(project, WorkflowStatus.Waiting, "等待试验", "9月20日试验");
        await _projects.ChangeStatusAsync(project, WorkflowStatus.Active);

        var history = await _repository.GetStatusHistoryAsync(project.Id);
        Assert.Equal(4, history.Count);

        // 前三行都已收口，最后一行是当前状态
        Assert.All(history.Take(3), row => Assert.NotNull(row.EndedAt));
        var open = history[^1];
        Assert.Null(open.EndedAt);
        Assert.Null(open.DurationSeconds);
        Assert.Equal(WorkflowStatus.Active, open.ToStatus);

        foreach (var row in history.Take(3))
        {
            Assert.True(row.EndedAt >= row.StartedAt, "结束时间不应早于开始时间");
            Assert.Equal(Math.Max(0, (long)(row.EndedAt!.Value - row.StartedAt).TotalSeconds), row.DurationSeconds);
        }

        var waiting = Assert.Single(history, row => row.ToStatus == WorkflowStatus.Waiting);
        Assert.Equal("等待试验", waiting.Reason);
        Assert.Equal("9月20日试验", waiting.Note);
        Assert.NotEqual(waiting.StartedAt, waiting.EndedAt!.Value);
    }

    /// <summary>
    /// 结构已是最新时不得产生备份；只有版本落后时才先备份再升级。
    /// </summary>
    [Fact]
    public async Task Database_BacksUpOnlyWhenSchemaNeedsUpgrade()
    {
        var root = Path.Combine(Path.GetTempPath(), "SimFlowTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths
        {
            AppDataDirectory = Path.Combine(root, "AppData"),
            DatabasePath = Path.Combine(root, "AppData", "simflow.db"),
            ConfigurationPath = Path.Combine(root, "AppData", "config.json"),
            LogsDirectory = Path.Combine(root, "AppData", "logs")
        };

        try
        {
            var database = new DatabaseService(paths, NullLogger<DatabaseService>.Instance);
            await database.InitializeAsync();
            await database.InitializeAsync();
            Assert.Empty(Directory.GetFiles(paths.AppDataDirectory, "*.bak"));

            await using (var connection = await database.OpenConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE SchemaMigrations SET Version = 0;";
                await command.ExecuteNonQueryAsync();
            }

            await database.InitializeAsync();
            Assert.NotEmpty(Directory.GetFiles(paths.AppDataDirectory, "*.bak"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SimulationReport_CopiesTemplateThenFindsTheOnlyWordReportAfterRename()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "报告测试" });
        var templatePath = Path.Combine(_root, "仿真分析报告.docx");
        await File.WriteAllTextAsync(templatePath, "template-v1");
        _configuration.Current.SimulationReportTemplatePath = templatePath;
        await _configuration.SaveAsync(_configuration.Current);

        var service = new SimulationReportService(_configuration, new StorageLocationService(_configuration));
        var created = await service.PrepareAsync(project);
        Assert.True(created.Created);
        Assert.Equal(Path.Combine(LocalWorkRoot, project.RelativePath, "Delivery", "仿真分析报告.docx"), created.Path);
        Assert.Equal("template-v1", await File.ReadAllTextAsync(created.Path));

        var renamedPath = Path.Combine(Path.GetDirectoryName(created.Path)!, "框架桥型触头仿真报告.docx");
        File.Move(created.Path, renamedPath);
        await File.WriteAllTextAsync(renamedPath, "user-edited-report");
        await File.WriteAllTextAsync(templatePath, "template-v2");
        _configuration.Current.SimulationReportTemplatePath = Path.Combine(_root, "missing-template.docx");
        var reopened = await service.PrepareAsync(project);

        Assert.False(reopened.Created);
        Assert.Equal(renamedPath, reopened.Path);
        Assert.Equal("user-edited-report", await File.ReadAllTextAsync(reopened.Path));
        Assert.False(File.Exists(created.Path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(created.Path)!, "*.partial"));
    }

    [Fact]
    public async Task SimulationReport_RejectsMultipleWordReportsInsteadOfGuessing()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "多报告测试" });
        var delivery = Path.Combine(LocalWorkRoot, project.RelativePath, "Delivery");
        await File.WriteAllTextAsync(Path.Combine(delivery, "报告A.docx"), "a");
        await File.WriteAllTextAsync(Path.Combine(delivery, "报告B.docm"), "b");
        var service = new SimulationReportService(_configuration, new StorageLocationService(_configuration));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(project));

        Assert.Contains("报告A.docx", error.Message);
        Assert.Contains("报告B.docm", error.Message);
    }

    [Fact]
    public async Task SimulationReport_RequiresConfiguredExistingTemplate()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "缺少模板" });
        var service = new SimulationReportService(_configuration, new StorageLocationService(_configuration));

        _configuration.Current.SimulationReportTemplatePath = string.Empty;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(project));

        _configuration.Current.SimulationReportTemplatePath = Path.Combine(_root, "missing.docx");
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.PrepareAsync(project));
    }

    [Fact]
    public async Task Cover_IsCopiedIntoProjectAndRecordedInMetadata()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "封面测试" });
        var sourcePath = Path.Combine(_root, "source-image.png");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4, 5]);

        var updated = await _projects.SetCoverImageAsync(project, sourcePath);

        Assert.Equal("cover.png", updated.CoverImage);
        var projectDirectory = Path.Combine(_configuration.Current.LocalWorkRoot, updated.ProjectCode);
        Assert.True(File.Exists(Path.Combine(projectDirectory, "cover.png")));

        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(projectDirectory, "project.json")))!.AsObject();
        Assert.Equal("cover.png", json["coverImage"]!.GetValue<string>());
        Assert.Contains(await _projects.GetActivitiesAsync(updated), item => item.ActivityType == ActivityTypes.CoverChanged);

        var cleared = await _projects.ClearCoverImageAsync(updated);
        Assert.Equal(string.Empty, cleared.CoverImage);
        Assert.False(File.Exists(Path.Combine(projectDirectory, "cover.png")));
    }

    [Fact]
    public async Task Cover_RejectsUnsupportedFileType()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "封面格式" });
        var sourcePath = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(sourcePath, "不是图片");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _projects.SetCoverImageAsync(project, sourcePath));
        Assert.Equal(string.Empty, project.CoverImage);
    }

    /// <summary>创建项目把仿真类型写进数据库与 project.json；不选类型时保持“未分类”（null）。</summary>
    [Fact]
    public async Task CreateProject_SavesSimulationTypeAndWritesMetadata()
    {
        var typed = await _projects.CreateAsync(new CreateProjectRequest { Name = "电磁项目", SimulationType = SimulationType.Electromagnetics });
        var untyped = await _projects.CreateAsync(new CreateProjectRequest { Name = "老项目" });

        Assert.Equal(SimulationType.Electromagnetics, typed.SimulationType);
        Assert.Null(untyped.SimulationType);

        var reloaded = await _repository.GetByCodeAsync(typed.ProjectCode);
        Assert.NotNull(reloaded);
        Assert.Equal(SimulationType.Electromagnetics, reloaded!.SimulationType);
        Assert.Null((await _repository.GetByCodeAsync(untyped.ProjectCode))!.SimulationType);

        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
            _configuration.Current.LocalWorkRoot, typed.ProjectCode, "project.json")))!.AsObject();
        Assert.Equal("Electromagnetics", json["simulationType"]!.GetValue<string>());
    }

    /// <summary>修改仿真类型：数据库与 project.json 同步更新，并写一条活动；无变化时不写活动。</summary>
    [Fact]
    public async Task UpdateInfo_ChangesSimulationTypeAndSyncsMetadata()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest
        {
            Name = "类型修改",
            Requester = "张三",
            SimulationType = SimulationType.Structural
        });
        var activityCount = (await _projects.GetActivitiesAsync(project)).Count;

        var updated = await _projects.UpdateInfoAsync(project, new UpdateProjectInfoRequest
        {
            Name = "类型修改",
            Requester = "张三",
            SimulationType = SimulationType.Fatigue
        });

        Assert.Equal(SimulationType.Fatigue, updated.SimulationType);
        Assert.Equal(SimulationType.Fatigue, (await _repository.GetByCodeAsync(project.ProjectCode))!.SimulationType);

        var projectPath = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(projectPath, "project.json")))!.AsObject();
        Assert.Equal("Fatigue", json["simulationType"]!.GetValue<string>());

        var activities = await _projects.GetActivitiesAsync(updated);
        Assert.Equal(activityCount + 1, activities.Count);
        Assert.Contains(activities, item => item.ActivityType == ActivityTypes.ProjectUpdated && item.Description.Contains("仿真类型"));

        // 清空为未分类也算一次真实修改。
        var cleared = await _projects.UpdateInfoAsync(updated, new UpdateProjectInfoRequest { Name = "类型修改", Requester = "张三" });
        Assert.Null(cleared.SimulationType);
        Assert.Null((await _repository.GetByCodeAsync(project.ProjectCode))!.SimulationType);

        // 无变化：不新增活动。
        var afterClear = (await _projects.GetActivitiesAsync(cleared)).Count;
        await _projects.UpdateInfoAsync(cleared, new UpdateProjectInfoRequest { Name = "类型修改", Requester = "张三" });
        Assert.Equal(afterClear, (await _projects.GetActivitiesAsync(cleared)).Count);
    }

    /// <summary>
    /// 老项目兼容：数据库里没有仿真类型（NULL）时能正常加载；即使列里是历史遗留的未知值，
    /// 也只当作“未分类”，不会让整个项目列表加载失败。
    /// </summary>
    [Fact]
    public async Task SimulationType_MissingOrUnknownValueStillLoads()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "旧项目加载" });
        Assert.Null((await _repository.GetByCodeAsync(project.ProjectCode))!.SimulationType);

        await using (var connection = await _database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Projects SET SimulationType='LegacyValue' WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", project.Id);
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await _repository.GetByCodeAsync(project.ProjectCode);
        Assert.NotNull(loaded);
        Assert.Null(loaded!.SimulationType);
        Assert.Single(await _repository.GetAllAsync());
    }

    /// <summary>
    /// 老 project.json 缺 simulationType：扫描仍能恢复项目，字段按“未分类”落库，档案本身不被破坏。
    /// </summary>
    [Fact]
    public async Task Scanner_RestoresLegacyMetadataWithoutSimulationType()
    {
        var created = await _projects.CreateAsync(new CreateProjectRequest
        {
            Name = "旧档案恢复",
            Requester = "李工",
            Tags = ["旧标签"],
            SimulationType = SimulationType.Structural
        });
        var metadataPath = Path.Combine(_configuration.Current.LocalWorkRoot, created.ProjectCode, "project.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!.AsObject();
        json.Remove("simulationType");
        await File.WriteAllTextAsync(metadataPath, json.ToJsonString());

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_paths.DatabasePath);
        File.Delete(_paths.DatabasePath + "-wal");
        File.Delete(_paths.DatabasePath + "-shm");

        var database = new DatabaseService(_paths, NullLogger<DatabaseService>.Instance);
        await database.InitializeAsync();
        var repository = new ProjectRepository(database);
        var scanner = new ProjectScannerService(
            repository,
            new ProjectMetadataStore(NullLogger<ProjectMetadataStore>.Instance),
            new StorageLocationService(_configuration),
            NullLogger<ProjectScannerService>.Instance);

        var result = await scanner.ScanAsync();
        Assert.Equal(1, result.ImportedProjects);
        Assert.Empty(result.Conflicts);
        Assert.Empty(result.Errors);

        var restored = await repository.GetByCodeAsync(created.ProjectCode);
        Assert.NotNull(restored);
        Assert.Null(restored!.SimulationType);
        Assert.Equal("旧档案恢复", restored.Name);
        Assert.Equal(["旧标签"], restored.Tags);
    }

    /// <summary>
    /// 版本 1 的旧库没有 CoverImage / SimulationType 列：启动时应当补列、把版本推进到当前版本，
    /// 并留下升级前备份。
    /// </summary>
    [Fact]
    public async Task Database_UpgradesLegacySchemaAndAddsCoverColumn()
    {
        var root = Path.Combine(Path.GetTempPath(), "SimFlowTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths
        {
            AppDataDirectory = Path.Combine(root, "AppData"),
            DatabasePath = Path.Combine(root, "AppData", "simflow.db"),
            ConfigurationPath = Path.Combine(root, "AppData", "config.json"),
            LogsDirectory = Path.Combine(root, "AppData", "logs")
        };
        Directory.CreateDirectory(paths.AppDataDirectory);

        try
        {
            // 手工造一个版本 1 的最小 Projects 表：索引引用的列必须在，CoverImage 必须缺。
            var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = paths.DatabasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
            }.ToString();
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE SchemaMigrations (Version INTEGER PRIMARY KEY, AppliedAtUtc TEXT NOT NULL);
                    INSERT INTO SchemaMigrations(Version, AppliedAtUtc) VALUES (1, '2026-01-01T00:00:00.0000000+00:00');
                    CREATE TABLE Projects (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ProjectCode TEXT NOT NULL UNIQUE,
                        WorkflowStatus TEXT NOT NULL DEFAULT '',
                        StorageStatus TEXT NOT NULL DEFAULT '',
                        UpdatedAtUtc TEXT NOT NULL DEFAULT '');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var database = new DatabaseService(paths, NullLogger<DatabaseService>.Instance);
            await database.InitializeAsync();

            Assert.NotEmpty(Directory.GetFiles(paths.AppDataDirectory, "*.bak"));

            await using (var connection = await database.OpenConnectionAsync())
            {
                var columns = new List<string>();
                await using (var probe = connection.CreateCommand())
                {
                    probe.CommandText = "PRAGMA table_info(Projects);";
                    await using var reader = await probe.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        columns.Add(reader.GetString(reader.GetOrdinal("name")));
                    }
                }

                Assert.Contains("CoverImage", columns);
                // 版本 3：仿真类型列必须存在，且允许为空（旧项目按“未分类”展示）。
                Assert.Contains("SimulationType", columns);

                await using var version = connection.CreateCommand();
                version.CommandText = "SELECT MAX(Version) FROM SchemaMigrations;";
                Assert.Equal(3, Convert.ToInt32(await version.ExecuteScalarAsync()));

                // 该列必须可空：旧项目升级后 SimulationType 为 NULL，不需要用户补全。
                await using var nullable = connection.CreateCommand();
                nullable.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Projects') WHERE name='SimulationType' AND \"notnull\"=0;";
                Assert.Equal(1L, Convert.ToInt64(await nullable.ExecuteScalarAsync()));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WorkflowRules_AllowAnyDifferentStatus()
    {
        foreach (var from in Enum.GetValues<WorkflowStatus>())
        {
            foreach (var to in Enum.GetValues<WorkflowStatus>())
            {
                Assert.Equal(from != to, WorkflowRules.CanTransition(from, to));
            }
        }

        // 只有进入等待才强制填写原因
        Assert.True(WorkflowRules.RequiresWaitReason(WorkflowStatus.Waiting));
        Assert.False(WorkflowRules.RequiresWaitReason(WorkflowStatus.Completed));
        Assert.False(WorkflowRules.RequiresWaitReason(WorkflowStatus.Active));
    }

    [Fact]
    public async Task Tags_RenameMergeDeleteAndSyncProjects()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "标签整理A", Tags = ["短耐", "150kA"] });
        var other = await _projects.CreateAsync(new CreateProjectRequest { Name = "标签整理B", Tags = ["短耐"] });

        // 摘要：计数正确（summary 中每个名称一条，Count 是使用项目数）
        var summary = await _projects.GetTagSummaryAsync();
        Assert.Equal(2, summary.Single(item => item.Name == "短耐").Count);
        Assert.Equal(1, summary.Single(item => item.Name == "150kA").Count);

        // 重命名到不存在的名称：原地改名
        await _projects.RenameTagAsync("短耐", "短耐分析");
        summary = await _projects.GetTagSummaryAsync();
        Assert.DoesNotContain(summary, item => item.Name == "短耐");
        Assert.Equal(2, summary.Single(item => item.Name == "短耐分析").Count);

        // 项目记录与 project.json 同步
        var reloaded = await _repository.GetByCodeAsync(project.ProjectCode);
        Assert.NotNull(reloaded);
        Assert.Contains("短耐分析", reloaded!.Tags);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_configuration.Current.LocalWorkRoot, reloaded.ProjectCode, "project.json")))!.AsObject();
        Assert.Contains("短耐分析", json["tags"]!.AsArray().Select(node => node!.GetValue<string>()));

        // 合并到已存在的名称：目标标签拿走项目，源标签消失
        await _projects.MergeTagsAsync("短耐分析", "150kA");
        summary = await _projects.GetTagSummaryAsync();
        Assert.Equal(2, summary.Single(item => item.Name == "150kA").Count);
        Assert.DoesNotContain(summary, item => item.Name == "短耐分析");
        reloaded = await _repository.GetByCodeAsync(project.ProjectCode);
        Assert.DoesNotContain(reloaded!.Tags, tag => tag == "短耐分析");

        // 删除未使用标签（先把一个项目里的标签移除，制造孤儿）
        var disposable = await _projects.CreateAsync(new CreateProjectRequest { Name = "标签整理C", Tags = ["临时标签"] });
        await _projects.UpdateInfoAsync(disposable, new UpdateProjectInfoRequest { Name = "标签整理C", Tags = [] });
        await _projects.DeleteUnusedTagAsync("临时标签");
        summary = await _projects.GetTagSummaryAsync();
        Assert.DoesNotContain(summary, item => item.Name == "临时标签");

        // 使用中的标签不能删
        await Assert.ThrowsAsync<InvalidOperationException>(() => _projects.DeleteUnusedTagAsync("150kA"));
    }

    [Fact]
    public async Task Configuration_LegacySoftwarePathsLoadAsNameOnlyLabels()
    {
        // 升级时读取旧配置，不要求用户重新填写软件名，也不恢复已移除的启动能力。
        await File.WriteAllTextAsync(_paths.ConfigurationPath, """
            {"software":[{"name":"Custom CAE","executablePath":"C:\\Tools\\cae.exe","arguments":"{ProjectPath}","configStateText":"configured"}]}
            """);
        var configuration = new ConfigurationService(_paths, NullLogger<ConfigurationService>.Instance);
        await configuration.InitializeAsync();
        Assert.Equal("Custom CAE", Assert.Single(configuration.Current.Software).Name);
        await configuration.SaveAsync(configuration.Current);
        using var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(_paths.ConfigurationPath));
        var label = saved.RootElement.GetProperty("software")[0];
        Assert.Equal("Custom CAE", label.GetProperty("name").GetString());
        Assert.False(label.TryGetProperty("executablePath", out _));
        Assert.False(label.TryGetProperty("arguments", out _));
        await configuration.InitializeAsync();
        Assert.Equal("Custom CAE", Assert.Single(configuration.Current.Software).Name);
    }

    [Fact]
    public async Task Configuration_DefaultsToEmptyWorkRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "SimFlowTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths
        {
            AppDataDirectory = Path.Combine(root, "AppData"),
            DatabasePath = Path.Combine(root, "AppData", "simflow.db"),
            ConfigurationPath = Path.Combine(root, "AppData", "config.json"),
            LogsDirectory = Path.Combine(root, "AppData", "logs")
        };

        try
        {
            var configuration = new ConfigurationService(paths, NullLogger<ConfigurationService>.Instance);
            await configuration.InitializeAsync();
            Assert.Equal(string.Empty, configuration.Current.LocalWorkRoot);

            // 空路径也可以保存（未配置状态是合法的）
            await configuration.SaveAsync(configuration.Current);
            Assert.Equal(string.Empty, configuration.Current.LocalWorkRoot);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DirectoryCopy_CopiesRecursively()
    {
        var root = Path.Combine(Path.GetTempPath(), "SimFlowTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "嵌套", "深层"));
            File.WriteAllText(Path.Combine(source, "cover.png"), "图");
            File.WriteAllText(Path.Combine(source, "project.json"), "{}");
            File.WriteAllText(Path.Combine(source, "嵌套", "深层", "model.bin"), "模型数据");

            DirectoryCopy.Copy(source, destination);

            Assert.True(File.Exists(Path.Combine(destination, "project.json")));
            Assert.True(File.Exists(Path.Combine(destination, "cover.png")));
            Assert.Equal("模型数据", File.ReadAllText(Path.Combine(destination, "嵌套", "深层", "model.bin")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DirectoryCopy_RejectsDestinationInsideSourceAndExistingTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "SimFlowTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var existing = Path.Combine(root, "existing");
        try
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(source, "project.json"), "{}");
            Assert.Throws<IOException>(() => DirectoryCopy.Copy(source, Path.Combine(source, "nested-copy")));
            Assert.Throws<IOException>(() => DirectoryCopy.Copy(source, existing));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// 事务回滚：事务中段（写入活动）失败时，整笔创建必须回滚，
    /// 不允许留下“只有项目行、没有活动/版本”的半截数据。
    /// </summary>
    [Fact]
    public async Task Repository_CreateRollsBackAllRowsWhenInsertFails()
    {
        var database = new DatabaseService(_paths, NullLogger<DatabaseService>.Instance);
        await using (var connection = await database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE ProjectActivities;";
            await command.ExecuteNonQueryAsync();
        }

        var project = new ProjectRecord
        {
            ProjectCode = "SIM_ROLLBACK001",
            Name = "回滚测试",
            WorkflowStatus = WorkflowStatus.NotStarted,
            StorageStatus = StorageStatus.Working,
            StorageLocation = StorageLocationCode.LocalWork,
            RelativePath = "SIM_ROLLBACK001",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _repository.CreateAsync(
            project,
            new ProjectVersionRecord
            {
                ProjectId = 0,
                VersionNumber = "V001",
                Title = "基准方案",
                Status = "Active",
                CreatedAt = DateTimeOffset.Now,
                RelativePath = Path.Combine("Versions", "V001")
            },
            new ProjectActivityRecord
            {
                ProjectId = 0,
                ActivityType = ActivityTypes.ProjectCreated,
                Description = "创建项目",
                CreatedAt = DateTimeOffset.Now
            }));

        Assert.Null(await _repository.GetByCodeAsync("SIM_ROLLBACK001"));
        Assert.Empty(await _repository.GetAllAsync());
    }

    /// <summary>
    /// 同步失败不丢数据：数据库插入成功后档案写入失败时，
    /// 记录保持 Pending（待同步），项目目录与已写入的 version.json 保留，供启动重试。
    /// </summary>
    [Fact]
    public async Task Create_MetadataFailureKeepsPendingDatabaseRecord()
    {
        var failingStore = new SelectiveThrowingMetadataStore(
            new ProjectMetadataStore(NullLogger<ProjectMetadataStore>.Instance),
            throwOnProjectWrite: true);
        var service = new ProjectService(
            _repository,
            failingStore,
            new StorageLocationService(_configuration),
            NullLogger<ProjectService>.Instance);

        await Assert.ThrowsAsync<IOException>(() =>
            service.CreateAsync(new CreateProjectRequest { Name = "档案写入失败但不丢数据" }));

        var remaining = Assert.Single(await _repository.GetAllAsync());
        Assert.Equal("Pending", remaining.MetadataSyncState);
        Assert.True(Directory.Exists(Path.Combine(_configuration.Current.LocalWorkRoot, remaining.ProjectCode)));
        Assert.True(File.Exists(Path.Combine(
            _configuration.Current.LocalWorkRoot, remaining.ProjectCode, "Versions", "V001", "version.json")));
    }

    /// <summary>
    /// 同步失败重试：记录处于 Pending 且档案文件缺失时，
    /// 启动期的 RetryPendingMetadataAsync 会从数据库重建 project.json，并把状态收口为 Synced。
    /// </summary>
    [Fact]
    public async Task MetadataSync_FailureRetryRebuildsProjectJson()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "同步重试", Requester = "张三" });
        var metadataPath = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode, "project.json");
        Assert.True(File.Exists(metadataPath));

        // 制造“待同步”状态：SaveAsync 只写数据库并标记 Pending，不写档案文件；再删掉档案模拟同步失败。
        await _repository.SaveAsync(project, new ProjectActivityRecord
        {
            ProjectId = project.Id,
            ActivityType = ActivityTypes.NoteUpdated,
            Description = "模拟同步前变更",
            CreatedAt = DateTimeOffset.Now
        });
        var pending = await _repository.GetByCodeAsync(project.ProjectCode);
        Assert.NotNull(pending);
        Assert.Equal("Pending", pending!.MetadataSyncState);
        File.Delete(metadataPath);

        await _projects.RetryPendingMetadataAsync();

        Assert.True(File.Exists(metadataPath));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!.AsObject();
        Assert.Equal(project.Name, json["name"]!.GetValue<string>());
        Assert.Equal("Synced", (await _repository.GetByCodeAsync(project.ProjectCode))!.MetadataSyncState);
    }

    /// <summary>
    /// Project ID 并发：多个创建同时进行时，编号必须互不相同且全部成功落库。
    /// 覆盖编号生成器的写锁串行化与服务层的撞号重试。
    /// </summary>
    [Fact]
    public async Task ConcurrentCreate_ProducesUniqueProjectCodes()
    {
        const int count = 8;
        var tasks = Enumerable.Range(0, count)
            .Select(_ => _projects.CreateAsync(new CreateProjectRequest { Name = "并发创建项目" }))
            .ToArray();
        var created = await Task.WhenAll(tasks);

        Assert.Equal(count, created.Select(item => item.ProjectCode).Distinct().Count());
        Assert.Equal(count, (await _projects.GetProjectsAsync()).Count);
        Assert.All(created, item => Assert.Matches(@"^SIM_\d{8}_\d{3}$", item.ProjectCode));
    }

    /// <summary>
    /// 损坏 JSON 只报告不破坏：原文件内容原样保留（便于人工修复），
    /// “能解析但缺编号/名称”的档案同样被报告而非导入，重复扫描结果稳定。
    /// </summary>
    [Fact]
    public async Task Scanner_PreservesCorruptJsonAndRaisesMissingFieldsError()
    {
        var corruptDirectory = Path.Combine(_configuration.Current.LocalWorkRoot, "SIM_CORRUPT2");
        Directory.CreateDirectory(corruptDirectory);
        var corruptPath = Path.Combine(corruptDirectory, "project.json");
        const string corruptContent = "{不是合法 JSON";
        await File.WriteAllTextAsync(corruptPath, corruptContent);

        var incompleteDirectory = Path.Combine(_configuration.Current.LocalWorkRoot, "SIM_INCOMPLETE");
        Directory.CreateDirectory(incompleteDirectory);
        await File.WriteAllTextAsync(Path.Combine(incompleteDirectory, "project.json"), "{\"name\":\"缺少编号\"}");

        var first = await _scanner.ScanAsync();
        Assert.Contains(first.Errors, error => error.Contains("SIM_CORRUPT2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(first.Errors, error => error.Contains("SIM_INCOMPLETE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, first.ImportedProjects);

        // 扫描不修改、不删除原始损坏文件，保留人工修复的可能。
        Assert.Equal(corruptContent, await File.ReadAllTextAsync(corruptPath));

        // 重复扫描不因“已报过错”改变结果或崩溃。
        var second = await _scanner.ScanAsync();
        Assert.Equal(first.Errors.Count, second.Errors.Count);
    }

    /// <summary>删除项目：记录删除 + 外键级联；可选是否删除文件夹。</summary>
    [Fact]
    public async Task DeleteProject_RemovesRecordAndFolderOptionally()
    {
        var keep = await _projects.CreateAsync(new CreateProjectRequest { Name = "保留目录", StartImmediately = false });
        await _projects.ChangeStatusAsync(keep, WorkflowStatus.Active);
        var keepPath = Path.Combine(_configuration.Current.LocalWorkRoot, keep.ProjectCode);

        await _projects.DeleteAsync(keep, deleteDirectory: false);
        Assert.Null(await _repository.GetByCodeAsync(keep.ProjectCode));
        Assert.True(Directory.Exists(keepPath), "未勾选删除文件夹时应保留目录");
        // 外键级联：版本、活动、状态历史一并清空。
        Assert.Empty(await _repository.GetVersionsAsync(keep.Id));
        Assert.Empty(await _repository.GetActivitiesAsync(keep.Id));
        Assert.Empty(await _repository.GetStatusHistoryAsync(keep.Id));

        var remove = await _projects.CreateAsync(new CreateProjectRequest { Name = "删除目录" });
        var removePath = Path.Combine(_configuration.Current.LocalWorkRoot, remove.ProjectCode);
        Assert.True(Directory.Exists(removePath));
        await _projects.DeleteAsync(remove, deleteDirectory: true);
        Assert.False(Directory.Exists(removePath), "勾选删除文件夹时应删除目录");
        var recovered = Assert.Single(Directory.GetDirectories(
            Path.Combine(LocalWorkRoot, ".simflow-recovery", "Projects")));
        Assert.True(File.Exists(Path.Combine(recovered, "project.json")), "项目数据应留在恢复区");
        Assert.Empty(await _repository.GetAllAsync());
    }

    [Fact]
    public async Task DeleteProject_DatabaseFailureRestoresFolderAndRecord()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "删除回滚" });
        var path = Path.Combine(LocalWorkRoot, project.ProjectCode);
        await using (var connection = await _database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailProjectDelete BEFORE DELETE ON Projects BEGIN SELECT RAISE(ABORT, 'delete failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _projects.DeleteAsync(project, true));

        Assert.True(Directory.Exists(path));
        Assert.NotNull(await _repository.GetByIdAsync(project.Id));
    }

    [Fact]
    public async Task Scanner_IgnoresDeletedProjectRecoveryArea()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "恢复区不回灌" });
        await _projects.DeleteAsync(project, true);

        var scan = await _scanner.ScanAsync();

        Assert.Equal(0, scan.ImportedProjects);
        Assert.Null(await _repository.GetByCodeAsync(project.ProjectCode));
        Assert.Single(Directory.GetDirectories(Path.Combine(LocalWorkRoot, ".simflow-recovery", "Projects")));
    }

    /// <summary>目录已被移动/删除（断链）时仍应能删除记录：删除不依赖目录存在。</summary>
    [Fact]
    public async Task DeleteProject_WorksWhenFolderMissingOrMoved()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "目录已移动" });
        var path = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        Directory.Delete(path, true);
        Assert.False(Directory.Exists(path));

        await _projects.DeleteAsync(project, deleteDirectory: true);
        Assert.Null(await _repository.GetByCodeAsync(project.ProjectCode));
        Assert.Empty(await _repository.GetAllAsync());
    }

    /// <summary>
    /// 回归：删记录但保留文件夹后，再建项目必须推进编号——
    /// 生成器按数据库最大值取号，而保留文件夹的旧序号在库里已无行，
    /// 若不做递增约束会反复生成同一序号直到重试耗尽。
    /// </summary>
    [Fact]
    public async Task RecreateAfterFolderRetained_AdvancesProjectCodeSequence()
    {
        var first = await _projects.CreateAsync(new CreateProjectRequest { Name = "先建", StartImmediately = false });
        var firstPath = Path.Combine(_configuration.Current.LocalWorkRoot, first.ProjectCode);
        await _projects.DeleteAsync(first, deleteDirectory: false);
        Assert.True(Directory.Exists(firstPath), "仅删记录时应保留文件夹");

        var second = await _projects.CreateAsync(new CreateProjectRequest { Name = "再建", StartImmediately = false });
        Assert.NotEqual(first.ProjectCode, second.ProjectCode);
        Assert.EndsWith("002", second.ProjectCode, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(_configuration.Current.LocalWorkRoot, second.ProjectCode)));
    }

    /// <summary>
    /// 删除版本：记录与磁盘目录一起删除；删的是当前版本时当前版本回退到剩余最高版本，
    /// 并写一条 VERSION_DELETED 活动、同步 project.json。
    /// </summary>
    [Fact]
    public async Task DeleteVersion_RemovesRecordAndFolderAndFallsBackCurrentVersion()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "删除版本" });
        var second = await _projects.CreateVersionAsync(project, "第二版", "改结构");
        var projectDirectory = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        var secondDirectory = Path.Combine(projectDirectory, "Versions", "V002");
        Assert.True(Directory.Exists(secondDirectory));
        Assert.Equal("V002", project.CurrentVersion);

        await _projects.DeleteVersionAsync(project, second, deleteDirectory: true);

        Assert.False(Directory.Exists(secondDirectory), "勾选删除目录时应删除版本目录");
        Assert.Equal("V001", project.CurrentVersion);
        var remaining = await _repository.GetVersionsAsync(project.Id);
        Assert.Equal(["V001"], remaining.Select(item => item.VersionNumber));
        Assert.Equal("V001", (await _repository.GetByCodeAsync(project.ProjectCode))!.CurrentVersion);

        // project.json 的 currentVersion 也要跟着回退
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(projectDirectory, "project.json")))!.AsObject();
        Assert.Equal("V001", json["currentVersion"]!.GetValue<string>());

        var activities = await _projects.GetActivitiesAsync(project);
        var deleted = Assert.Single(activities, item => item.ActivityType == ActivityTypes.VersionDeleted);
        Assert.Contains("V002", deleted.Description);
        Assert.Contains("回退到 V001", deleted.Description);
    }

    /// <summary>目录已经被手工删除时，只清记录即可；同时项目至少要保留一个版本。</summary>
    [Fact]
    public async Task DeleteVersion_HandlesMissingFolderAndRefusesLastVersion()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "缺目录的版本" });
        var second = await _projects.CreateVersionAsync(project, "第二版", string.Empty);
        var projectDirectory = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        Directory.Delete(Path.Combine(projectDirectory, "Versions", "V002"), true);
        Assert.False(Directory.Exists(Path.Combine(projectDirectory, "Versions", "V002")));

        await _projects.DeleteVersionAsync(project, second, deleteDirectory: true);
        Assert.Equal(["V001"], (await _repository.GetVersionsAsync(project.Id)).Select(item => item.VersionNumber));

        // 唯一版本不能删：记录与目录都保持原样。
        var only = (await _repository.GetVersionsAsync(project.Id)).Single();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _projects.DeleteVersionAsync(project, only, deleteDirectory: true));
        Assert.Contains("至少需要保留一个版本", error.Message);
        Assert.True(Directory.Exists(Path.Combine(projectDirectory, "Versions", "V001")));
        Assert.Single(await _repository.GetVersionsAsync(project.Id));
    }

    /// <summary>
    /// 只删记录保留目录时，新建版本不能复用已占用的编号（否则旧目录内容会被当成新版本）。
    /// </summary>
    [Fact]
    public async Task CreateVersion_SkipsNumberWhoseFolderStillExists()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "编号避让" });
        var second = await _projects.CreateVersionAsync(project, "第二版", string.Empty);
        var projectDirectory = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        await _projects.DeleteVersionAsync(project, second, deleteDirectory: false);
        Assert.True(Directory.Exists(Path.Combine(projectDirectory, "Versions", "V002")), "未勾选删除目录时应保留目录");

        var third = await _projects.CreateVersionAsync(project, "第三版", string.Empty);

        Assert.Equal("V003", third.VersionNumber);
        Assert.True(Directory.Exists(Path.Combine(projectDirectory, "Versions", "V003")));
        Assert.True(Directory.Exists(Path.Combine(projectDirectory, "Versions", "V002")));
    }

    /// <summary>
    /// 扫描对账：目录被手工删掉的版本记录会被列出来（不自动改数据），确认后才清理并回退当前版本。
    /// </summary>
    [Fact]
    public async Task Scanner_ReportsMissingVersionFolderAndCleansItOnDemand()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "扫描缺目录" });
        await _projects.CreateVersionAsync(project, "第二版", string.Empty);
        var projectDirectory = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        Directory.Delete(Path.Combine(projectDirectory, "Versions", "V002"), true);

        var scan = await _scanner.ScanAsync();
        Assert.Empty(scan.Conflicts);
        var issue = Assert.Single(scan.VersionIssues);
        Assert.Equal(ScanVersionIssueKind.MissingFolder, issue.Kind);
        Assert.Equal("V002", issue.VersionNumber);
        Assert.True(issue.CanResolve);

        // 扫描本身不改数据（GetVersionsAsync 按版本号倒序返回）
        Assert.Equal(["V002", "V001"], (await _repository.GetVersionsAsync(project.Id)).Select(item => item.VersionNumber));
        Assert.Equal("V002", (await _repository.GetByCodeAsync(project.ProjectCode))!.CurrentVersion);

        await _scanner.ResolveVersionIssueAsync(issue);

        Assert.Equal(["V001"], (await _repository.GetVersionsAsync(project.Id)).Select(item => item.VersionNumber));
        var reloaded = await _repository.GetByCodeAsync(project.ProjectCode)!;
        Assert.NotNull(reloaded);
        Assert.Equal("V001", reloaded!.CurrentVersion);
        Assert.Contains(await _projects.GetActivitiesAsync(reloaded), item => item.ActivityType == ActivityTypes.VersionDeleted);

        // 再扫一次：已经一致，不再报告。
        Assert.Empty((await _scanner.ScanAsync()).VersionIssues);
    }

    /// <summary>唯一版本缺目录时不能由扫描清理（CanResolve=false），避免把项目清成零版本。</summary>
    [Fact]
    public async Task Scanner_KeepsOnlyVersionWhenItsFolderIsMissing()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "唯一版本缺目录" });
        var projectDirectory = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        Directory.Delete(Path.Combine(projectDirectory, "Versions", "V001"), true);

        var issue = Assert.Single((await _scanner.ScanAsync()).VersionIssues);
        Assert.Equal(ScanVersionIssueKind.MissingFolder, issue.Kind);
        Assert.False(issue.CanResolve);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.ResolveVersionIssueAsync(issue));
        Assert.Single(await _repository.GetVersionsAsync(project.Id));
    }

    /// <summary>整个 Versions 目录都不在时不报“目录缺失”（旧结构/半拷贝项目无法区分），避免误报。</summary>
    [Fact]
    public async Task Scanner_SkipsVersionCheckWhenVersionsRootIsAbsent()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "无版本目录" });
        await _projects.CreateVersionAsync(project, "第二版", string.Empty);
        var projectDirectory = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        Directory.Delete(Path.Combine(projectDirectory, "Versions"), true);

        var scan = await _scanner.ScanAsync();
        Assert.Empty(scan.VersionIssues);
        Assert.Empty(scan.Errors);
        Assert.Equal(2, (await _repository.GetVersionsAsync(project.Id)).Count);
    }

    /// <summary>
    /// 反向不一致：磁盘上有 Versions\V003 但数据库没有记录 → 报告；确认后按目录里的 version.json 补建记录。
    /// </summary>
    [Fact]
    public async Task Scanner_ReportsAndAdoptsUnrecordedVersionFolder()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "多出的版本目录" });
        var projectDirectory = Path.Combine(_configuration.Current.LocalWorkRoot, project.ProjectCode);
        var orphanDirectory = Path.Combine(projectDirectory, "Versions", "V003");
        foreach (var name in new[] { "3D_Model", "CAE_Model", "Data", "Results" })
        {
            Directory.CreateDirectory(Path.Combine(orphanDirectory, name));
        }

        await new ProjectMetadataStore(NullLogger<ProjectMetadataStore>.Instance).WriteVersionAsync(orphanDirectory, new VersionMetadataDocument
        {
            VersionNumber = "V003",
            Title = "手工放入的版本",
            ChangeSummary = "从别处拷贝进来",
            CreatedAt = DateTimeOffset.Now.AddDays(-2)
        });

        var scan = await _scanner.ScanAsync();
        var issue = Assert.Single(scan.VersionIssues);
        Assert.Equal(ScanVersionIssueKind.UnrecordedFolder, issue.Kind);
        Assert.Equal("V003", issue.VersionNumber);
        Assert.Equal("手工放入的版本", issue.Title);
        Assert.Equal(["V001"], (await _repository.GetVersionsAsync(project.Id)).Select(item => item.VersionNumber));

        await _scanner.ResolveVersionIssueAsync(issue);

        var versions = await _repository.GetVersionsAsync(project.Id);
        var adopted = Assert.Single(versions, item => item.VersionNumber == "V003");
        Assert.Equal("手工放入的版本", adopted.Title);
        Assert.Equal("从别处拷贝进来", adopted.ChangeSummary);
        Assert.Equal(Path.Combine("Versions", "V003"), adopted.RelativePath);
        // 补建记录不会改变“当前版本”
        Assert.Equal("V001", (await _repository.GetByCodeAsync(project.ProjectCode))!.CurrentVersion);
        // 已记录后不再重复报告
        Assert.Empty((await _scanner.ScanAsync()).VersionIssues);
    }

    /// <summary>
    /// 删库后扫描恢复：项目按 project.json 重建，磁盘上的其余版本目录由确认后补建，
    /// 版本历史不再只剩一个“当前版本”。
    /// </summary>
    [Fact]
    public async Task Scanner_RebuildsVersionHistoryFromDiskAfterDatabaseDeletion()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "重建版本历史" });
        await _projects.CreateVersionAsync(project, "第二版", "改结构");
        await _projects.CreateVersionAsync(project, "第三版", "再改一版");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_paths.DatabasePath);
        File.Delete(_paths.DatabasePath + "-wal");
        File.Delete(_paths.DatabasePath + "-shm");

        var database = new DatabaseService(_paths, NullLogger<DatabaseService>.Instance);
        await database.InitializeAsync();
        var repository = new ProjectRepository(database);
        var scanner = new ProjectScannerService(
            repository,
            new ProjectMetadataStore(NullLogger<ProjectMetadataStore>.Instance),
            new StorageLocationService(_configuration),
            NullLogger<ProjectScannerService>.Instance);

        var scan = await scanner.ScanAsync();
        Assert.Equal(1, scan.ImportedProjects);
        Assert.Empty(scan.Conflicts);
        // 导入只带一条“当前版本”记录，其余两个目录被列为待补建。
        Assert.Equal(2, scan.VersionIssues.Count);
        Assert.All(scan.VersionIssues, issue => Assert.Equal(ScanVersionIssueKind.UnrecordedFolder, issue.Kind));

        foreach (var issue in scan.VersionIssues)
        {
            await scanner.ResolveVersionIssueAsync(issue);
        }

        var restored = await repository.GetByCodeAsync(project.ProjectCode);
        Assert.NotNull(restored);
        var versions = await repository.GetVersionsAsync(restored!.Id);
        Assert.Equal(["V001", "V002", "V003"], versions.Select(item => item.VersionNumber).OrderBy(item => item, StringComparer.Ordinal));
        Assert.Equal("V003", restored.CurrentVersion);
        Assert.Empty((await scanner.ScanAsync()).VersionIssues);
    }

    /// <summary>
    /// 手工整理旧项目时会用各种编辑器改 project.json：这里确认带 UTF-8 BOM 的档案仍能读取
    /// （Windows 记事本 / PowerShell 5.1 的 -Encoding utf8 都会加 BOM），否则一个 BOM 就会让扫描
    /// 报“无法读取”，用户的手改全部白做。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MetadataStore_ReadsProjectJsonWithOrWithoutBom(bool withBom)
    {
        var directory = Path.Combine(_root, withBom ? "bom" : "nobom");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "project.json");
        const string json = """
            {
              "schemaVersion": 1,
              "id": "SIM_BOM_001",
              "name": "带 BOM 的档案",
              "requester": "张三",
              "workflowStatus": "Completed",
              "storageStatus": "Working",
              "storageLocation": "LocalWork",
              "relativePath": "SIM_BOM_001",
              "createdAt": "2026-01-05T00:00:00+08:00",
              "startedAt": "2026-01-06T00:00:00+08:00",
              "completedAt": "2026-06-15T00:00:00+08:00",
              "updatedAt": "2026-06-15T00:00:00+08:00",
              "currentVersion": "V001"
            }
            """;
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(withBom));

        var store = new ProjectMetadataStore(NullLogger<ProjectMetadataStore>.Instance);
        var metadata = await store.ReadProjectAsync(path);

        Assert.NotNull(metadata);
        Assert.Equal("带 BOM 的档案", metadata!.Name);
        Assert.Equal(2026, metadata.CompletedAt!.Value.Year);
        Assert.Equal(6, metadata.CompletedAt.Value.Month);
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("missing-file")]
    [InlineData("changed-source")]
    [InlineData("changed-target")]
    [InlineData("new-source-file")]
    [InlineData("bad-metadata")]
    [InlineData("changed-location")]
    [InlineData("stale-transfer")]
    public async Task CleanupSource_RefusesUnsafeOrStaleCleanup(string scenario)
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "保留源副本" });
        var source = Path.Combine(LocalWorkRoot, project.ProjectCode);
        await File.WriteAllTextAsync(Path.Combine(source, "data.bin"), "original");
        var transfer = await _migration.MigrateAsync(project, StorageLocationCode.WorkstationWork);
        switch (scenario)
        {
            case "missing-target": Directory.Delete(transfer.TargetPath, true); break;
            case "missing-file": File.Delete(Path.Combine(transfer.TargetPath, "data.bin")); break;
            case "changed-source": await File.WriteAllTextAsync(Path.Combine(source, "data.bin"), "modified"); break;
            case "changed-target": await File.WriteAllTextAsync(Path.Combine(transfer.TargetPath, "data.bin"), "modified"); break;
            case "new-source-file": await File.WriteAllTextAsync(Path.Combine(source, "new.bin"), "new"); break;
            case "bad-metadata": await File.WriteAllTextAsync(Path.Combine(transfer.TargetPath, "project.json"), "{}"); break;
            case "changed-location":
                project.StorageLocation = StorageLocationCode.LocalWork;
                await _repository.SaveAsync(project, new ProjectActivityRecord
                {
                    ProjectId = project.Id, ActivityType = ActivityTypes.ProjectMoved,
                    Description = "changed", CreatedAt = DateTimeOffset.Now
                });
                break;
            case "stale-transfer":
                var stored = (await _migration.GetIncompleteAsync()).Single();
                stored.State = TransferState.Failed;
                await _repository.UpdateTransferAsync(stored);
                break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => _migration.CleanupSourceAsync(transfer));
        Assert.True(File.Exists(Path.Combine(source, "data.bin")));
        var pending = Assert.Single(await _migration.GetIncompleteAsync());
        Assert.Equal(scenario == "stale-transfer" ? TransferState.Failed : TransferState.CleanupPending, pending.State);
    }

    [Fact]
    public async Task CleanupSource_RejectsCallerThatSubstitutesStoredPaths()
    {
        _configuration.Current.DeleteSourceAfterArchive = false;
        await _configuration.SaveAsync(_configuration.Current);
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "目标新增文件" });
        var transfer = await _migration.MigrateAsync(project, StorageLocationCode.WorkstationArchive);
        await File.WriteAllTextAsync(Path.Combine(transfer.TargetPath, "new-result.bin"), "new result");
        var stale = new TransferOperationRecord
        {
            Id = transfer.Id, ProjectId = project.Id, SourceLocation = transfer.SourceLocation,
            TargetLocation = transfer.TargetLocation, SourcePath = transfer.TargetPath, TargetPath = transfer.SourcePath,
            State = TransferState.CleanupPending
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _migration.CleanupSourceAsync(stale));
        Assert.Contains("迁移记录与当前请求不一致", error.Message);
        Assert.True(Directory.Exists(transfer.SourcePath));
        Assert.True(File.Exists(Path.Combine(transfer.TargetPath, "new-result.bin")));
        Assert.Equal(TransferState.CleanupPending, stale.State);
    }

    [Fact]
    public async Task Scanner_SequentialCleanupKeepsLastVersionAndConsistentCurrentVersion()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "不能清空版本" });
        await _projects.CreateVersionAsync(project, "第二版", "");
        var versionsRoot = Path.Combine(LocalWorkRoot, project.ProjectCode, "Versions");
        foreach (var directory in Directory.GetDirectories(versionsRoot)) Directory.Delete(directory, true);
        var issues = (await _scanner.ScanAsync()).VersionIssues;
        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue => Assert.True(issue.CanResolve));
        await _scanner.ResolveVersionIssueAsync(issues[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.ResolveVersionIssueAsync(issues[1]));
        var only = Assert.Single(await _repository.GetVersionsAsync(project.Id));
        Assert.Equal(only.VersionNumber, (await _repository.GetByIdAsync(project.Id))!.CurrentVersion);
        Assert.Single(await _projects.GetActivitiesAsync(project), item => item.ActivityType == ActivityTypes.VersionDeleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scanner_RefusesCleanupAfterFolderRestoredOrProjectMoved(bool moved)
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "扫描过期" });
        await _projects.CreateVersionAsync(project, "第二版", "");
        var directory = Path.Combine(LocalWorkRoot, project.ProjectCode, "Versions", "V002");
        Directory.Delete(directory, true);
        var issue = Assert.Single((await _scanner.ScanAsync()).VersionIssues);
        if (moved) await _migration.MigrateAsync(project, StorageLocationCode.WorkstationWork);
        else Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.ResolveVersionIssueAsync(issue));
        Assert.Equal(2, (await _repository.GetVersionsAsync(project.Id)).Count);
    }

    [Fact]
    public async Task DeleteVersion_RollsBackRecordCurrentAndActivityWhenQueueFails()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "事务回滚" });
        var version = await _projects.CreateVersionAsync(project, "第二版", "");
        await using (var connection = await _database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailQueue BEFORE INSERT ON MetadataSyncQueue BEGIN SELECT RAISE(ABORT, 'audit failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _projects.DeleteVersionAsync(project, version, false));
        Assert.Equal(2, (await _repository.GetVersionsAsync(project.Id)).Count);
        Assert.Equal("V002", (await _repository.GetByIdAsync(project.Id))!.CurrentVersion);
        Assert.DoesNotContain(await _projects.GetActivitiesAsync(project), item => item.ActivityType == ActivityTypes.VersionDeleted);
        Assert.Empty(await _repository.GetPendingMetadataSyncAsync());
    }

    [Fact]
    public async Task DeleteVersion_DatabaseFailureRestoresMovedDirectory()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "版本目录回滚" });
        var version = await _projects.CreateVersionAsync(project, "第二版", "");
        var directory = Path.Combine(LocalWorkRoot, project.ProjectCode, "Versions", "V002");
        await File.WriteAllTextAsync(Path.Combine(directory, "important.bin"), "keep");
        await using (var connection = await _database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailQueueWithFiles BEFORE INSERT ON MetadataSyncQueue BEGIN SELECT RAISE(ABORT, 'audit failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            _projects.DeleteVersionAsync(project, version, true));

        Assert.True(File.Exists(Path.Combine(directory, "important.bin")));
        Assert.Equal(2, (await _repository.GetVersionsAsync(project.Id)).Count);
        Assert.Equal("V002", (await _repository.GetByIdAsync(project.Id))!.CurrentVersion);
    }

    [Fact]
    public async Task Repository_LastVersionGuardRunsBeforeDirectoryAction()
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "事务内保护" });
        var only = Assert.Single(await _repository.GetVersionsAsync(project.Id));
        var invoked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.DeleteVersionAsync(project.Id, only.Id,
            "delete", _ => invoked = true));
        Assert.False(invoked);
        Assert.Single(await _repository.GetVersionsAsync(project.Id));
    }

    [Fact]
    public async Task StorageSettings_PartialCopyKeepsOldConfigAndRetryAcceptsVerifiedCopy()
    {
        var reportTemplate = Path.Combine(_root, "report-template.docx");
        await File.WriteAllTextAsync(reportTemplate, "template");
        _configuration.Current.SimulationReportTemplatePath = reportTemplate;
        await _configuration.SaveAsync(_configuration.Current);
        await _projects.CreateAsync(new CreateProjectRequest { Name = "项目一" });
        await _projects.CreateAsync(new CreateProjectRequest { Name = "项目二" });
        var ordered = await _repository.GetAllAsync();
        var sourceFile = Path.Combine(LocalWorkRoot, ordered[1].RelativePath, "locked.bin");
        await File.WriteAllTextAsync(sourceFile, "data");
        var destination = Path.Combine(_root, "NewWork");
        var service = new StorageSettingsService(_configuration, _repository);
        var configBefore = await File.ReadAllTextAsync(_paths.ConfigurationPath);
        var currentBefore = _configuration.Current;
        using (var locked = new FileStream(sourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(destination,
                currentBefore.WorkstationWorkRoot, currentBefore.WorkstationArchiveRoot, true,
                currentBefore.DeleteSourceAfterArchive));
        }
        Assert.Same(currentBefore, _configuration.Current);
        Assert.Equal(configBefore, await File.ReadAllTextAsync(_paths.ConfigurationPath));
        Assert.True(Directory.Exists(Path.Combine(destination, ordered[0].RelativePath)));
        Assert.True(File.Exists(sourceFile));
        await service.SaveAsync(destination, currentBefore.WorkstationWorkRoot, currentBefore.WorkstationArchiveRoot, true, false);
        Assert.Equal(destination, _configuration.Current.LocalWorkRoot);
        Assert.NotSame(currentBefore, _configuration.Current);
        Assert.Equal(LocalWorkRoot, currentBefore.LocalWorkRoot);
        Assert.Equal(reportTemplate, _configuration.Current.SimulationReportTemplatePath);
        Assert.False(_configuration.Current.DeleteSourceAfterArchive);
        Assert.Equal("data", await File.ReadAllTextAsync(Path.Combine(destination, ordered[1].RelativePath, "locked.bin")));
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("missing-source")]
    [InlineData("overlap")]
    public async Task StorageSettings_PreflightFailureDoesNotSwitchOrOverwrite(string scenario)
    {
        var project = await _projects.CreateAsync(new CreateProjectRequest { Name = "复制失败" });
        var source = Path.Combine(LocalWorkRoot, project.RelativePath);
        var destination = Path.Combine(_root, "NewWork");
        if (scenario == "missing-source") Directory.Delete(source, true);
        if (scenario == "overlap") destination = Path.Combine(LocalWorkRoot, "NestedWork");
        var targetFile = Path.Combine(destination, project.RelativePath, "sentinel.bin");
        if (scenario == "conflict")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            await File.WriteAllTextAsync(targetFile, "keep");
        }
        var before = await File.ReadAllTextAsync(_paths.ConfigurationPath);
        await Assert.ThrowsAnyAsync<IOException>(() => new StorageSettingsService(_configuration, _repository)
            .SaveAsync(destination, "", "", true, true));
        Assert.Equal(LocalWorkRoot, _configuration.Current.LocalWorkRoot);
        Assert.Equal(before, await File.ReadAllTextAsync(_paths.ConfigurationPath));
        if (scenario == "conflict") Assert.Equal("keep", await File.ReadAllTextAsync(targetFile));
    }

    [Fact]
    public async Task StorageSettings_ConfigWriteFailureDoesNotMutateCurrent()
    {
        var current = _configuration.Current;
        var before = await File.ReadAllTextAsync(_paths.ConfigurationPath);
        using var locked = new FileStream(_paths.ConfigurationPath + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => new StorageSettingsService(_configuration, _repository)
            .SaveAsync(Path.Combine(_root, "NewWork"), "new-station", "new-archive", false, true));
        Assert.Same(current, _configuration.Current);
        Assert.Equal(LocalWorkRoot, current.LocalWorkRoot);
        Assert.Equal(before, await File.ReadAllTextAsync(_paths.ConfigurationPath));
    }

    private sealed class CallbackProgress(Action<MigrationProgress> callback) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value) => callback(value);
    }

    private sealed class AvailableSpaceOverrideStorage(IStorageLocationService inner, long availableBytes) : IStorageLocationService
    {
        public string ResolveRoot(StorageLocationCode location) => inner.ResolveRoot(location);

        public string ResolveProjectPath(ProjectRecord project) => inner.ResolveProjectPath(project);

        public Task<bool> IsOnlineAsync(StorageLocationCode location, CancellationToken cancellationToken = default) =>
            inner.IsOnlineAsync(location, cancellationToken);

        public Task<long?> GetAvailableBytesAsync(StorageLocationCode location, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<long?>(availableBytes);
        }
    }

    /// <summary>按开关选择是否让“写 project.json”失败，用于验证档案写入失败时的数据安全。</summary>
    private sealed class SelectiveThrowingMetadataStore(IProjectMetadataStore inner, bool throwOnProjectWrite) : IProjectMetadataStore
    {
        public Task WriteProjectAsync(string projectDirectory, ProjectMetadataDocument metadata, CancellationToken cancellationToken = default)
            => throwOnProjectWrite
                ? throw new IOException("模拟 project.json 写入失败")
                : inner.WriteProjectAsync(projectDirectory, metadata, cancellationToken);

        public Task<ProjectMetadataDocument?> ReadProjectAsync(string metadataPath, CancellationToken cancellationToken = default) =>
            inner.ReadProjectAsync(metadataPath, cancellationToken);

        public Task WriteVersionAsync(string versionDirectory, VersionMetadataDocument metadata, CancellationToken cancellationToken = default) =>
            inner.WriteVersionAsync(versionDirectory, metadata, cancellationToken);

        public Task<VersionMetadataDocument?> ReadVersionAsync(string metadataPath, CancellationToken cancellationToken = default) =>
            inner.ReadVersionAsync(metadataPath, cancellationToken);
    }
}
