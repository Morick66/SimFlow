using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SimFlow.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimFlow.Services;

public sealed class AppPaths
{
    public required string AppDataDirectory { get; init; }
    public required string DatabasePath { get; init; }
    public required string ConfigurationPath { get; init; }
    public required string LogsDirectory { get; init; }

    /// <summary>
    /// 解析运行数据目录（数据库、配置、日志）。
    /// </summary>
    /// <param name="hasPackageIdentity">
    /// 是否以 MSIX 打包方式运行（由应用层判断后传入，Core 不引用 WinRT）。
    /// </param>
    public static AppPaths Create(bool hasPackageIdentity = false)
    {
        var overrideRoot = Environment.GetEnvironmentVariable("SIMFLOW_DATA_HOME");
        var appData = !string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(overrideRoot, "AppData")
            : ResolveLocalDataDirectory(hasPackageIdentity);

        return new AppPaths
        {
            AppDataDirectory = appData,
            DatabasePath = Path.Combine(appData, "simflow.db"),
            ConfigurationPath = Path.Combine(appData, "config.json"),
            LogsDirectory = Path.Combine(appData, "logs")
        };
    }

    /// <summary>
    /// 打包（MSIX）运行时，<c>Environment.GetFolderPath(LocalApplicationData)</c> 返回的是包容器内的重定向路径
    /// （<c>...\Packages\&lt;包族名&gt;\LocalCache\Local</c>），而 **卸载会连同它一起删除**。
    /// 为了让数据库、配置、日志在卸载/重装后仍然存在，打包运行时改用用户配置文件的真实路径。
    /// </summary>
    private static string ResolveLocalDataDirectory(bool hasPackageIdentity)
    {
        if (!hasPackageIdentity)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimFlow");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            return Path.Combine(profile, "AppData", "Local", "SimFlow");
        }

        // 兜底：环境变量不受包重定向影响，同样指向真实路径。
        return Path.Combine(Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%"), "SimFlow");
    }

    /// <summary>
    /// 只创建运行数据目录（数据库、配置、日志）。
    /// 本机 Work 根由用户在设置里配置，这里不预设、也不创建：未配置时应用不能在本机创建项目，
    /// 真正需要时由 ConfigurationService/ProjectService 创建已配置的那个路径。
    /// （此前这里会无条件创建 D:\SimFlowData\Work 这个默认根，用户改了 Work 路径后它就成了空壳。）
    /// </summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}

public sealed class ConfigurationService(AppPaths paths, ILogger<ConfigurationService> logger) : IConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppConfiguration Current { get; private set; } = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();
        if (!File.Exists(paths.ConfigurationPath))
        {
            Current = CreateDefault();
            await SaveAsync(Current, cancellationToken);
            return;
        }

        try
        {
            await using var stream = File.OpenRead(paths.ConfigurationPath);
            Current = await JsonSerializer.DeserializeAsync<AppConfiguration>(stream, JsonOptions, cancellationToken) ?? CreateDefault();
            // 本机 Work 默认允许为空：用户未配置时不能在本机创建项目，
            // 由用户在设置中配置后再使用（配置处会提示复制旧目录项目）。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load the SimFlow configuration; defaults will be used.");
            Current = CreateDefault();
            throw new InvalidOperationException($"配置文件无法读取：{paths.ConfigurationPath}", ex);
        }
    }

    public async Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // 本机 Work 允许为空（未配置状态）；空时不创建目录。
        Directory.CreateDirectory(paths.AppDataDirectory);
        if (!string.IsNullOrWhiteSpace(configuration.LocalWorkRoot))
        {
            Directory.CreateDirectory(configuration.LocalWorkRoot);
        }

        var temporaryPath = paths.ConfigurationPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, paths.ConfigurationPath, true);
        Current = configuration;
    }

    private AppConfiguration CreateDefault() => new();
}

public sealed class DatabaseService(AppPaths paths, ILogger<DatabaseService> logger)
{
    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = paths.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        ForeignKeys = true
    }.ToString();

    private const int CurrentSchemaVersion = 3;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();
        var existed = File.Exists(paths.DatabasePath);
        string? backupPath = null;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var currentVersion = await ReadSchemaVersionAsync(connection, cancellationToken);
            if (existed && currentVersion < CurrentSchemaVersion)
            {
                // 只有真正需要升级结构时才备份，避免每次启动都复制整个数据库。
                backupPath = $"{paths.DatabasePath}.v{currentVersion}.{DateTimeOffset.Now:yyyyMMddHHmmss}.bak";
                await BackupAsync(connection, backupPath, cancellationToken);
                logger.LogInformation("Database schema {Current} will be upgraded to {Target}; backup written to {Backup}",
                    currentVersion, CurrentSchemaVersion, backupPath);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = SchemaSql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            // 版本 2：项目封面图。新库由 SchemaSql 直接建好，旧库在这里补列。
            await EnsureColumnAsync(connection, "Projects", "CoverImage", "TEXT NOT NULL DEFAULT ''", cancellationToken);

            // 版本 3：项目仿真类型。允许 NULL —— 旧项目没有这个字段，界面按“未分类”展示，
            // 不要求用户补全，也不回填默认值。
            await EnsureColumnAsync(connection, "Projects", "SimulationType", "TEXT NULL", cancellationToken);

            await using (var migration = connection.CreateCommand())
            {
                migration.CommandText = "INSERT OR IGNORE INTO SchemaMigrations(Version, AppliedAtUtc) VALUES ($version, $now);";
                migration.Parameters.AddWithValue("$version", CurrentSchemaVersion);
                migration.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database initialization failed.");
            if (backupPath is not null && File.Exists(backupPath))
            {
                // 连接池默认开启，Dispose 不会真正关闭文件句柄，复制前必须先清空连接池。
                SqliteConnection.ClearAllPools();
                File.Copy(backupPath, paths.DatabasePath, true);
            }

            throw new InvalidOperationException("SimFlow 数据库初始化失败，已尝试恢复升级前备份。", ex);
        }
    }

    /// <summary>
    /// 幂等补列：新库已经由 SchemaSql 建好该列，旧库在这里补齐，两种情况下都可安全重复执行。
    /// </summary>
    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(reader.GetOrdinal("name")), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SchemaMigrations';";
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken) ?? 0L) == 0)
            {
                return 0;
            }
        }

        await using var latest = connection.CreateCommand();
        latest.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;";
        return Convert.ToInt32(await latest.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    /// <summary>
    /// 使用 SQLite 自身的备份接口，保证在 WAL 模式下也能得到一致的副本。
    /// </summary>
    private static async Task BackupAsync(SqliteConnection source, string backupPath, CancellationToken cancellationToken)
    {
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS SchemaMigrations (
            Version INTEGER PRIMARY KEY,
            AppliedAtUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Projects (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProjectCode TEXT NOT NULL UNIQUE,
            Name TEXT NOT NULL,
            Description TEXT NOT NULL DEFAULT '',
            Requester TEXT NOT NULL DEFAULT '',
            Notes TEXT NOT NULL DEFAULT '',
            WorkflowStatus TEXT NOT NULL,
            StorageStatus TEXT NOT NULL,
            StorageLocation TEXT NOT NULL,
            RelativePath TEXT NOT NULL,
            WaitReason TEXT NULL,
            WaitNote TEXT NULL,
            WaitStartedAtUtc TEXT NULL,
            IsFavorite INTEGER NOT NULL DEFAULT 0,
            CreatedAtUtc TEXT NOT NULL,
            StartedAtUtc TEXT NULL,
            CompletedAtUtc TEXT NULL,
            ArchivedAtUtc TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            CurrentVersion TEXT NOT NULL,
            AccumulatedWaitSeconds INTEGER NOT NULL DEFAULT 0,
            CoverImage TEXT NOT NULL DEFAULT '',
            SimulationType TEXT NULL,
            MetadataSyncState TEXT NOT NULL DEFAULT 'Pending'
        );
        CREATE INDEX IF NOT EXISTS IX_Projects_UpdatedAt ON Projects(UpdatedAtUtc DESC);
        CREATE INDEX IF NOT EXISTS IX_Projects_Status ON Projects(WorkflowStatus, StorageStatus);

        CREATE TABLE IF NOT EXISTS ProjectVersions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            VersionNumber TEXT NOT NULL,
            Title TEXT NOT NULL DEFAULT '',
            Description TEXT NOT NULL DEFAULT '',
            ChangeSummary TEXT NOT NULL DEFAULT '',
            Status TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            CompletedAtUtc TEXT NULL,
            RelativePath TEXT NOT NULL,
            UNIQUE(ProjectId, VersionNumber)
        );

        CREATE TABLE IF NOT EXISTS ProjectStatusHistory (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            FromStatus TEXT NULL,
            ToStatus TEXT NOT NULL,
            Reason TEXT NULL,
            Note TEXT NULL,
            StartedAtUtc TEXT NOT NULL,
            EndedAtUtc TEXT NULL,
            DurationSeconds INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS ProjectActivities (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            ActivityType TEXT NOT NULL,
            Description TEXT NOT NULL,
            MetadataJson TEXT NOT NULL DEFAULT '{}',
            CreatedAtUtc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Activities_Project ON ProjectActivities(ProjectId, CreatedAtUtc DESC);

        CREATE TABLE IF NOT EXISTS Tags (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL COLLATE NOCASE UNIQUE
        );
        CREATE TABLE IF NOT EXISTS ProjectTags (
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            TagId INTEGER NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
            PRIMARY KEY(ProjectId, TagId)
        );

        CREATE TABLE IF NOT EXISTS Software (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL COLLATE NOCASE UNIQUE
        );
        CREATE TABLE IF NOT EXISTS ProjectSoftware (
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            SoftwareId INTEGER NOT NULL REFERENCES Software(Id) ON DELETE CASCADE,
            PRIMARY KEY(ProjectId, SoftwareId)
        );

        CREATE TABLE IF NOT EXISTS StorageLocations (
            Code TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            RootPath TEXT NOT NULL DEFAULT '',
            Type TEXT NOT NULL,
            Enabled INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS TransferOperations (
            Id TEXT PRIMARY KEY,
            ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            SourceLocation TEXT NOT NULL,
            TargetLocation TEXT NOT NULL,
            SourcePath TEXT NOT NULL,
            TargetPath TEXT NOT NULL,
            State TEXT NOT NULL,
            TotalBytes INTEGER NOT NULL DEFAULT 0,
            ProcessedBytes INTEGER NOT NULL DEFAULT 0,
            Error TEXT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS MetadataSyncQueue (
            ProjectId INTEGER PRIMARY KEY REFERENCES Projects(Id) ON DELETE CASCADE,
            Attempts INTEGER NOT NULL DEFAULT 0,
            LastError TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );

        INSERT OR IGNORE INTO StorageLocations(Code, Name, Type) VALUES
            ('LocalWork', '本机 Work', 'Work'),
            ('WorkstationWork', '工作站 Work', 'Work'),
            ('WorkstationArchive', '工作站 Archive', 'Archive');
        """;
}
