using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimFlow.Models;
using SimFlow.Services;
using System.Collections.ObjectModel;

namespace SimFlow.ViewModels;

public sealed partial class MainViewModel(
    IProjectService projectService,
    IStorageLocationService storage,
    IConfigurationService configuration,
    IProjectMigrationService migrationService) : ObservableObject
{
    private List<ProjectRecord> _allProjects = [];
    private List<string> _allTags = [];
    private readonly Dictionary<StorageLocationCode, bool> _locationAvailability = [];
    private int _detailsRequest;

    public ObservableCollection<ProjectCardViewModel> Projects { get; } = [];
    public ObservableCollection<ProjectCardViewModel> NotStartedProjects { get; } = [];
    public ObservableCollection<ProjectCardViewModel> ActiveProjects { get; } = [];
    public ObservableCollection<ProjectCardViewModel> WaitingProjects { get; } = [];
    public ObservableCollection<ProjectCardViewModel> CompletedProjects { get; } = [];
    public ObservableCollection<StatCardViewModel> Stats { get; } = [];
    public ObservableCollection<TagSummaryViewModel> Tags { get; } = [];
    public ObservableCollection<SoftwareConfiguration> SoftwareList { get; } = [];
    public ObservableCollection<ProjectVersionRecord> Versions { get; } = [];
    public ObservableCollection<ProjectActivityRecord> Activities { get; } = [];

    /// <summary>所有项目出现过的标签，供标签编辑器做“点击加入”的候选。</summary>
    public IReadOnlyList<string> AllTags => _allTags;

    /// <summary>当前全部项目（含被筛选隐藏的），供设置页做目录复制时统计。</summary>
    public IReadOnlyList<ProjectRecord> AllProjects => _allProjects;

    [ObservableProperty]
    public partial ProjectCardViewModel? SelectedProject { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilterKey { get; set; } = "home";

    [ObservableProperty]
    public partial ProjectSortMode SortMode { get; set; } = ProjectSortMode.RecentlyUpdated;

    /// <summary>首页是看板而不是列表，XAML 用它切换两块内容。</summary>
    [ObservableProperty]
    public partial bool IsHome { get; set; } = true;

    /// <summary>设置独立页（常规/项目目录/标签/软件标签/迁移与归档/数据与备份）。</summary>
    [ObservableProperty]
    public partial bool IsSettings { get; set; }

    /// <summary>统计分析页（独立页面，不显示项目列表与详情栏）。</summary>
    [ObservableProperty]
    public partial bool IsStatistics { get; set; }

    /// <summary>非首页、非设置、非统计页时显示项目列表。</summary>
    public bool ShowProjectList => !IsHome && !IsSettings && !IsStatistics;

    [ObservableProperty]
    public partial string CurrentSectionTitle { get; set; } = "项目列表";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool WorkstationOnline { get; set; }

    [ObservableProperty]
    public partial bool HasProjects { get; set; }

    public int TotalProjectCount => _allProjects.Count;
    public string ProjectCountText => $"共 {Projects.Count} 项";
    public int NotStartedProjectCount => NotStartedProjects.Count;
    public int ActiveProjectCount => ActiveProjects.Count;
    public int WaitingProjectCount => WaitingProjects.Count;
    public int CompletedProjectCount => CompletedProjects.Count;
    /// <summary>侧边栏徽章用：已归档 / 收藏的项目总数（UI 展示用计算属性，不参与业务逻辑）。</summary>
    public int ArchivedProjectCount => _allProjects.Count(project => project.StorageStatus == StorageStatus.Archived);
    public int FavoriteProjectCount => _allProjects.Count(project => project.IsFavorite);
    /// <summary>看板列空状态（纯展示，不参与业务逻辑）。</summary>
    public bool NotStartedEmpty => NotStartedProjects.Count == 0;
    public bool ActiveEmpty => ActiveProjects.Count == 0;
    public bool WaitingEmpty => WaitingProjects.Count == 0;
    public bool CompletedEmpty => CompletedProjects.Count == 0;
    public TransferOperationRecord? LastTransfer { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await projectService.RetryPendingMetadataAsync(cancellationToken);
            await RefreshStorageAvailabilityAsync(cancellationToken);
            await RefreshAsync(cancellationToken);
            RefreshSoftwareList();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var selectedCode = SelectedProject?.Project.ProjectCode;
        _allProjects = (await projectService.GetProjectsAsync(cancellationToken)).ToList();
        _allTags = _allProjects
            .SelectMany(project => project.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.CurrentCulture)
            .ToList();
        ApplyFilter();
        UpdateStats();
        OnPropertyChanged(nameof(TotalProjectCount));
        // 首页是看板、统计页有自己的内容：都不自动选中项目，让“点一下才出详情”这件事一眼可见。
        // 其余页面保留自动选中首个项目的习惯。
        var fallback = IsHome || IsStatistics ? null : Projects.FirstOrDefault();
        var restored = selectedCode is null
            ? null
            : Projects.FirstOrDefault(item => item.Project.ProjectCode == selectedCode);
        SelectedProject = restored ?? fallback;
    }

    public async Task RefreshStorageAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var locations = Enum.GetValues<StorageLocationCode>();
        var checks = locations.Select(async location =>
            (Location: location, Online: await storage.IsOnlineAsync(location, cancellationToken))).ToArray();
        foreach (var result in await Task.WhenAll(checks))
        {
            _locationAvailability[result.Location] = result.Online;
        }

        WorkstationOnline = _locationAvailability.GetValueOrDefault(StorageLocationCode.WorkstationWork);
    }

    public void SetFilter(string key)
    {
        FilterKey = key;
        IsHome = string.Equals(key, "home", StringComparison.Ordinal);
        IsSettings = string.Equals(key, "settings", StringComparison.Ordinal);
        IsStatistics = string.Equals(key, "statistics", StringComparison.Ordinal);
        OnPropertyChanged(nameof(ShowProjectList));
        CurrentSectionTitle = key switch
        {
            "home" => "工作台",
            "all" => "所有项目",
            "notstarted" => "未开始",
            "active" => "进行中",
            "waiting" => "等待中",
            "completed" => "已完成",
            "favorite" => "收藏项目",
            "archived" => "已归档",
            "statistics" => "统计分析",
            "settings" => "设置",
            _ => "项目列表"
        };
        // 切换分区后清掉选中：详情面板不再残留上一个分区的项目（SF-027 行为修复）。
        SelectedProject = null;
        ApplyFilter();
        if (IsSettings)
        {
            _ = RefreshTagSummaryAsync(CancellationToken.None);
        }
    }

    public void ApplySearch(string value)
    {
        SearchText = value;
        ApplyFilter();
    }

    public void SetSortMode(ProjectSortMode mode)
    {
        SortMode = mode;
        ApplyFilter();
    }

    public async Task<ProjectRecord> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var project = await projectService.CreateAsync(request, cancellationToken);
            StatusMessage = $"已创建 {project.ProjectCode}";
            await RefreshAsync(cancellationToken);
            SelectedProject = Projects.FirstOrDefault(item => item.Project.ProjectCode == project.ProjectCode);
            return project;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ChangeStatusAsync(WorkflowStatus status, string? reason = null, string? note = null, CancellationToken cancellationToken = default)
    {
        if (SelectedProject is null) return;
        await ChangeStatusAsync(SelectedProject.Project, status, reason, note, cancellationToken);
    }

    /// <summary>看板拖拽等场景需要直接指定项目，而不是依赖当前选中项。</summary>
    public async Task ChangeStatusAsync(ProjectRecord project, WorkflowStatus status, string? reason = null, string? note = null, CancellationToken cancellationToken = default)
    {
        // 状态变更除了 SQLite 记录，还需要同步项目目录中的 project.json。
        // 在 UI 入口之外再做一次实时门禁，避免位置在页面刷新后掉线时出现
        // “界面提示失败，但数据库状态已经改变”的不一致。
        if (!await storage.IsOnlineAsync(project.StorageLocation, cancellationToken))
        {
            throw new InvalidOperationException("项目存储位置离线或未配置，无法修改状态。请恢复连接后重试。");
        }

        await projectService.ChangeStatusAsync(project, status, reason, note, cancellationToken);
        StatusMessage = "项目状态已更新";
        await RefreshAsync(cancellationToken);
    }

    public async Task ToggleFavoriteAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedProject is null) return;
        await projectService.ToggleFavoriteAsync(SelectedProject.Project, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public async Task UpdateInfoAsync(UpdateProjectInfoRequest request, CancellationToken cancellationToken = default)
    {
        if (SelectedProject is null) return;
        await projectService.UpdateInfoAsync(SelectedProject.Project, request, cancellationToken);
        StatusMessage = "项目信息已更新";
        await RefreshAsync(cancellationToken);
    }

    public async Task SetCoverImageAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (SelectedProject is null) return;
        await projectService.SetCoverImageAsync(SelectedProject.Project, sourcePath, cancellationToken);
        StatusMessage = "封面已更新";
        await RefreshAsync(cancellationToken);
    }

    public async Task ClearCoverImageAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedProject is null) return;
        await projectService.ClearCoverImageAsync(SelectedProject.Project, cancellationToken);
        StatusMessage = "封面已移除";
        await RefreshAsync(cancellationToken);
    }

    public async Task DeleteProjectAsync(bool deleteDirectory, CancellationToken cancellationToken = default)
    {
        if (SelectedProject is null) return;
        var project = SelectedProject.Project;
        await projectService.DeleteAsync(project, deleteDirectory, cancellationToken);
        StatusMessage = deleteDirectory ? "项目及文件夹已删除" : "项目记录已删除，文件夹保留";
        SelectedProject = null;
        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshTagSummaryAsync(CancellationToken cancellationToken = default)
    {
        Tags.Clear();
        foreach (var item in await projectService.GetTagSummaryAsync(cancellationToken))
        {
            Tags.Add(new TagSummaryViewModel(item.Name, item.Count));
        }
    }

    public async Task RenameTagAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        await projectService.RenameTagAsync(oldName, newName, cancellationToken);
        StatusMessage = "标签已重命名";
        await RefreshAsync(cancellationToken);
        await RefreshTagSummaryAsync(cancellationToken);
    }

    public async Task MergeTagsAsync(string sourceName, string targetName, CancellationToken cancellationToken = default)
    {
        await projectService.MergeTagsAsync(sourceName, targetName, cancellationToken);
        StatusMessage = "标签已合并";
        await RefreshAsync(cancellationToken);
        await RefreshTagSummaryAsync(cancellationToken);
    }

    public async Task DeleteUnusedTagAsync(string name, CancellationToken cancellationToken = default)
    {
        await projectService.DeleteUnusedTagAsync(name, cancellationToken);
        StatusMessage = "标签已删除";
        await RefreshTagSummaryAsync(cancellationToken);
    }

    public async Task AddSoftwareAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("软件名称不能为空。");
        }

        if (configuration.Current.Software.Any(item => string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"软件「{trimmed}」已存在。");
        }

        var next = CopyConfiguration(configuration.Current);
        next.Software.Add(new SoftwareConfiguration { Name = trimmed });
        await configuration.SaveAsync(next, cancellationToken);
        RefreshSoftwareList();
    }

    public async Task UpdateSoftwareAsync(SoftwareConfiguration item, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("软件名称不能为空。");
        }

        var current = configuration.Current;
        var index = current.Software.IndexOf(item);
        if (index < 0)
        {
            throw new InvalidOperationException("软件候选已变更，请刷新后重试。");
        }

        if (current.Software.Where((_, candidateIndex) => candidateIndex != index)
            .Any(candidate => string.Equals(candidate.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"软件「{trimmed}」已存在。");
        }

        var next = CopyConfiguration(current);
        next.Software[index].Name = trimmed;
        await configuration.SaveAsync(next, cancellationToken);
        RefreshSoftwareList();
    }

    public async Task RemoveSoftwareAsync(SoftwareConfiguration item, CancellationToken cancellationToken = default)
    {
        var current = configuration.Current;
        var index = current.Software.IndexOf(item);
        if (index < 0)
        {
            throw new InvalidOperationException("软件候选已变更，请刷新后重试。");
        }

        var next = CopyConfiguration(current);
        next.Software.RemoveAt(index);
        await configuration.SaveAsync(next, cancellationToken);
        RefreshSoftwareList();
    }

    /// <summary>
    /// 为设置修改创建独立副本。调用方只修改副本，交给 ConfigurationService 保存；
    /// 只有文件写入成功后，ConfigurationService 才会用副本替换 Current。
    /// </summary>
    internal static AppConfiguration CopyConfiguration(AppConfiguration source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        LocalWorkRoot = source.LocalWorkRoot,
        WorkstationWorkRoot = source.WorkstationWorkRoot,
        WorkstationArchiveRoot = source.WorkstationArchiveRoot,
        ProjectPlaceholderImage = source.ProjectPlaceholderImage,
        SimulationReportTemplatePath = source.SimulationReportTemplatePath,
        CheckWorkstationOnStartup = source.CheckWorkstationOnStartup,
        Software = source.Software.Select(candidate => new SoftwareConfiguration { Name = candidate.Name }).ToList()
    };

    private void RefreshSoftwareList()
    {
        SoftwareList.Clear();
        foreach (var item in configuration.Current.Software)
        {
            SoftwareList.Add(item);
        }
    }

    public async Task UpdateNotesAsync(string notes, CancellationToken cancellationToken = default)
    {
        if (SelectedProject is null) return;
        await projectService.UpdateNotesAsync(SelectedProject.Project, notes, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public async Task CreateVersionAsync(string title, string summary, CancellationToken cancellationToken = default)
    {
        if (SelectedProject is null) return;
        var version = await projectService.CreateVersionAsync(SelectedProject.Project, title, summary, cancellationToken);
        StatusMessage = $"已创建 {version.VersionNumber}";
        await RefreshAsync(cancellationToken);
    }

    /// <summary>删除版本（可选同时删除磁盘目录）；当前版本被删时由服务层回退到剩余最高版本。</summary>
    public async Task DeleteVersionAsync(ProjectVersionRecord version, bool deleteDirectory, CancellationToken cancellationToken = default)
    {
        if (SelectedProject is null) return;
        await projectService.DeleteVersionAsync(SelectedProject.Project, version, deleteDirectory, cancellationToken);
        StatusMessage = $"已删除版本 {version.VersionNumber}";
        await RefreshAsync(cancellationToken);
    }

    public async Task<TransferOperationRecord> MigrateAsync(StorageLocationCode target, IProgress<MigrationProgress> progress, CancellationToken cancellationToken)
    {
        if (SelectedProject is null) throw new InvalidOperationException("请先选择项目。");
        IsBusy = true;
        try
        {
            LastTransfer = await migrationService.MigrateAsync(SelectedProject.Project, target, progress, cancellationToken);
            await RefreshAsync(cancellationToken);
            return LastTransfer;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CleanupLastTransferAsync(CancellationToken cancellationToken = default)
    {
        if (LastTransfer is null) return;
        await migrationService.CleanupSourceAsync(LastTransfer, cancellationToken);
        StatusMessage = "源目录已清理";
    }

    public Task<IReadOnlyList<TransferOperationRecord>> GetIncompleteTransfersAsync(CancellationToken cancellationToken = default)
        => migrationService.GetIncompleteAsync(cancellationToken);

    public async Task<TransferOperationRecord> RetryTransferAsync(TransferOperationRecord transfer,
        IProgress<MigrationProgress> progress, CancellationToken cancellationToken = default)
    {
        LastTransfer = await migrationService.RetryAsync(transfer, progress, cancellationToken);
        await RefreshAsync(CancellationToken.None);
        return LastTransfer;
    }

    public async Task AbandonTransferAsync(TransferOperationRecord transfer, CancellationToken cancellationToken = default)
    {
        await migrationService.AbandonAsync(transfer, cancellationToken);
        StatusMessage = "已放弃暂存副本";
    }

    public async Task CleanupTransferAsync(TransferOperationRecord transfer, CancellationToken cancellationToken = default)
    {
        await migrationService.CleanupSourceAsync(transfer, cancellationToken);
        StatusMessage = "源目录已清理";
    }

    public string? GetSelectedProjectPath()
        => SelectedProject is null ? null : storage.ResolveProjectPath(SelectedProject.Project);

    partial void OnSelectedProjectChanged(ProjectCardViewModel? value)
    {
        _ = LoadDetailsAsync(value);
    }

    private async Task LoadDetailsAsync(ProjectCardViewModel? item)
    {
        var request = ++_detailsRequest;
        Versions.Clear();
        Activities.Clear();
        if (item is null) return;
        try
        {
            var versions = await projectService.GetVersionsAsync(item.Project);
            if (request != _detailsRequest) return;
            var activities = await projectService.GetActivitiesAsync(item.Project);
            if (request != _detailsRequest) return;
            foreach (var version in versions) Versions.Add(version);
            foreach (var activity in activities) Activities.Add(activity);
        }
        catch (Exception ex)
        {
            if (request == _detailsRequest) StatusMessage = $"详情加载失败：{ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<ProjectRecord> query = _allProjects;
        query = FilterKey switch
        {
            "notstarted" => query.Where(project => project.WorkflowStatus == WorkflowStatus.NotStarted && project.StorageStatus == StorageStatus.Working),
            "active" => query.Where(project => project.WorkflowStatus == WorkflowStatus.Active && project.StorageStatus == StorageStatus.Working),
            "waiting" => query.Where(project => project.WorkflowStatus == WorkflowStatus.Waiting && project.StorageStatus == StorageStatus.Working),
            "completed" => query.Where(project => project.WorkflowStatus == WorkflowStatus.Completed && project.StorageStatus == StorageStatus.Working),
            "favorite" => query.Where(project => project.IsFavorite),
            "archived" => query.Where(project => project.StorageStatus == StorageStatus.Archived),
            _ => query
        };
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(project =>
                Contains(project.Name, search) || Contains(project.ProjectCode, search) ||
                Contains(project.Description, search) || Contains(project.Requester, search) ||
                Contains(project.Notes, search) || project.Tags.Any(tag => Contains(tag, search)) ||
                project.Software.Any(item => Contains(item, search)));
        }

        var filtered = SortMode switch
        {
            ProjectSortMode.RecentlyCreated => query.OrderByDescending(project => project.CreatedAt).ThenBy(project => project.ProjectCode).ToList(),
            ProjectSortMode.Name => query.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(project => project.ProjectCode).ToList(),
            _ => query.OrderByDescending(project => project.UpdatedAt).ThenBy(project => project.ProjectCode).ToList()
        };
        Projects.Clear();
        foreach (var project in filtered) Projects.Add(CreateCard(project));
        HasProjects = Projects.Count > 0;
        OnPropertyChanged(nameof(ProjectCountText));
        UpdateBoard();
    }

    /// <summary>
    /// 首页是看板：四个状态列（未开始、进行中、等待中、已完成）只放未归档的项目。
    /// 其余筛选（含“所有项目”）走完整列表。
    /// </summary>
    private void UpdateBoard()
    {
        NotStartedProjects.Clear();
        ActiveProjects.Clear();
        WaitingProjects.Clear();
        CompletedProjects.Clear();
        if (IsHome)
        {
            foreach (var card in Projects.Where(card => card.Project.StorageStatus == StorageStatus.Working))
            {
                var column = card.Project.WorkflowStatus switch
                {
                    WorkflowStatus.NotStarted => NotStartedProjects,
                    WorkflowStatus.Active => ActiveProjects,
                    WorkflowStatus.Waiting => WaitingProjects,
                    WorkflowStatus.Completed => CompletedProjects,
                    _ => null
                };
                column?.Add(card);
            }
        }

        OnPropertyChanged(nameof(NotStartedProjectCount));
        OnPropertyChanged(nameof(ActiveProjectCount));
        OnPropertyChanged(nameof(WaitingProjectCount));
        OnPropertyChanged(nameof(CompletedProjectCount));
        OnPropertyChanged(nameof(NotStartedEmpty));
        OnPropertyChanged(nameof(ActiveEmpty));
        OnPropertyChanged(nameof(WaitingEmpty));
        OnPropertyChanged(nameof(CompletedEmpty));
    }

    private ProjectCardViewModel CreateCard(ProjectRecord project) => new(
        project,
        ResolveCoverPath(project),
        _locationAvailability.GetValueOrDefault(project.StorageLocation),
        ResolvePlaceholderPath());

    /// <summary>
    /// 只拼路径，不检查文件是否存在——项目可能位于离线的工作站共享上，存在性检查会卡住 UI。
    /// 图片真的加载不出来时，由 XAML 里的占位图层兜底显示默认图。
    /// </summary>
    private string? ResolveCoverPath(ProjectRecord project)
    {
        if (string.IsNullOrWhiteSpace(project.CoverImage))
        {
            return null;
        }

        try
        {
            return Path.Combine(storage.ResolveProjectPath(project), project.CoverImage);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 自定义占位图允许在设置中配置；只有确认文件存在时才使用，避免坏路径把内置占位图也顶掉。
    /// </summary>
    private string? ResolvePlaceholderPath()
    {
        var configured = configuration.Current.ProjectPlaceholderImage;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        try
        {
            return File.Exists(configured) ? configured : null;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateStats()
    {
        Stats.Clear();
        // Key 用于点击统计卡片时跳转到对应筛选页。
        Stats.Add(new("进行中", Count(WorkflowStatus.Active), "\uE768", "#E8F1FF", "#2563EB", "active"));
        Stats.Add(new("等待中", Count(WorkflowStatus.Waiting), "\uE823", "#FFF3DA", "#F59E0B", "waiting"));
        Stats.Add(new("已完成", Count(WorkflowStatus.Completed), "\uE73E", "#E1F7EC", "#16A34A", "completed"));
        Stats.Add(new("已归档", _allProjects.Count(project => project.StorageStatus == StorageStatus.Archived), "\uE7B8", "#EDF1F6", "#64748B", "archived"));
        Stats.Add(new("收藏", _allProjects.Count(project => project.IsFavorite), "\uE735", "#FFF8DB", "#F5B400", "favorite"));
        OnPropertyChanged(nameof(ArchivedProjectCount));
        OnPropertyChanged(nameof(FavoriteProjectCount));
    }

    private int Count(WorkflowStatus status) => _allProjects.Count(project => project.WorkflowStatus == status && project.StorageStatus == StorageStatus.Working);

    private static bool Contains(string? value, string search)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.CurrentCultureIgnoreCase);
}

public sealed partial class ProjectCardViewModel : ObservableObject
{
    public ProjectCardViewModel(ProjectRecord project, string? coverPath = null, bool isLocationAvailable = true, string? placeholderPath = null)
    {
        Project = project;
        // 占位图先建好，封面加载失败时会切到它。
        PlaceholderSource = CreatePlaceholderSource(placeholderPath);
        CoverSource = CreateCoverSource(coverPath, () => CoverFailed = true);
        IsLocationAvailable = isLocationAvailable;
    }

    /// <summary>
    /// 封面是否加载失败（文件被删、项目位置离线、格式损坏）。
    /// 注意方向：初值是"没失败"，即默认认为封面可用、直接显示封面，只有真的收到
    /// BitmapImage.ImageFailed 才改用占位图。
    /// 反过来做（等 ImageOpened 才切到封面）在真机上不成立 —— 图片没有被真正显示出来时
    /// 并不会开始加载，"等加载完成"就永远等不到，结果是封面永远不显示（已实测踩过）。
    /// 这样无论失败事件是否触发，最差结果都只是"封面位置空白"，不会退化成"封面消失"。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySource))]
    public partial bool CoverFailed { get; set; }

    /// <summary>
    /// 卡片与详情实际显示的那张图：有封面就用封面，没有或已确认加载失败才用占位图。
    /// 只渲染一张 —— 以前是"占位图 + 封面两张叠放"、靠封面盖住占位图，但 WinUI 的 Opacity
    /// 是逐元素生效的，卡片变淡时两张图各自变淡再叠加，占位图会透出来。
    /// </summary>
    public ImageSource DisplaySource => CoverFailed || CoverSource is null ? PlaceholderSource : CoverSource;

    /// <summary>
    /// 是否正在被拖动。拖动中的原始卡片变淡，让视线跟着拖拽预览走。
    /// 这个状态挂在项目数据上而不是直接改控件：看板列表会复用卡片容器，
    /// 直接改控件属性会在容器被复用给别的项目时留下"某个无关卡片也半透明"的残留。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardOpacity))]
    public partial bool IsDragging { get; set; }

    /// <summary>卡片不透明度：拖动中变淡，避免原位置和拖动预览重复显示。</summary>
    public double CardOpacity => IsDragging ? 0.45 : 1.0;

    public ProjectRecord Project { get; }
    public bool IsLocationAvailable { get; }
    public bool HasLocationWarning => !IsLocationAvailable;
    public string LocationStateText => IsLocationAvailable ? string.Empty : "存储位置离线或未配置";
    public bool HasCustomCover => !string.IsNullOrWhiteSpace(Project.CoverImage);
    public bool HasSoftware => Project.Software.Count > 0;

    /// <summary>项目封面图；路径为空、无法构造 Uri 或加载失败时由 <see cref="PlaceholderSource"/> 兜底。</summary>
    public ImageSource? CoverSource { get; }

    /// <summary>占位图：默认内置图，可在设置中替换。没有可用封面时显示的就是它。</summary>
    public ImageSource PlaceholderSource { get; }

    private static ImageSource? CreateCoverSource(string? coverPath, Action? onFailed = null)
    {
        if (string.IsNullOrWhiteSpace(coverPath))
        {
            return null;
        }

        try
        {
            // 显式拼 file:// 形式，避免依赖 Uri 对盘符/UNC 的推断。
            var normalized = Path.GetFullPath(coverPath).Replace('\\', '/');
            var uri = normalized.StartsWith("//", StringComparison.Ordinal)
                ? new Uri("file:" + normalized)
                : new Uri("file:///" + normalized);
            var image = new BitmapImage { UriSource = uri, DecodePixelWidth = 360 };
            if (onFailed is not null)
            {
                image.ImageFailed += (_, _) => onFailed();
            }

            return image;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource CreatePlaceholderSource(string? placeholderPath)
    {
        if (!string.IsNullOrWhiteSpace(placeholderPath) && CreateCoverSource(placeholderPath) is { } custom)
        {
            return custom;
        }

        return new BitmapImage { UriSource = new Uri("ms-appx:///Assets/ProjectPreview.png"), DecodePixelWidth = 360 };
    }

    public string Name => Project.Name;
    public string ProjectCode => Project.ProjectCode;
    public string Description => string.IsNullOrWhiteSpace(Project.Description) ? "暂无项目描述" : Project.Description;
    public string RequesterText => string.IsNullOrWhiteSpace(Project.Requester) ? "未填写需求人" : $"需求人：{Project.Requester}";
    public string RequesterDisplay => string.IsNullOrWhiteSpace(Project.Requester) ? "未填写" : Project.Requester;
    /// <summary>仿真类型（为空显示“未分类”）。只放在详情与所有项目列表，不塞进看板卡片。</summary>
    public string SimulationTypeText => SimulationTypes.Describe(Project.SimulationType);
    public string StatusText => Project.WorkflowStatus switch
    {
        WorkflowStatus.NotStarted => "未开始",
        WorkflowStatus.Active => "进行中",
        WorkflowStatus.Waiting => "等待中",
        WorkflowStatus.Completed => "已完成",
        _ => "未知"
    };
    public string StatusBackground => Project.WorkflowStatus switch
    {
        WorkflowStatus.NotStarted => "#EEF1F5",
        WorkflowStatus.Active => "#DCEAFF",
        WorkflowStatus.Waiting => "#FFE7B8",
        WorkflowStatus.Completed => "#D5F3E3",
        _ => "#EEF1F5"
    };
    public string StatusForeground => Project.WorkflowStatus switch
    {
        WorkflowStatus.Active => "#1261D8",
        WorkflowStatus.Waiting => "#A85A00",
        WorkflowStatus.Completed => "#087B3F",
        _ => "#52627A"
    };
    public string FavoriteGlyph => Project.IsFavorite ? "\uE735" : "\uE734";
    public string FavoriteColor => Project.IsFavorite ? "#F5B400" : "#94A3B8";
    public string SoftwareText => Project.Software.Count == 0 ? "未指定软件" : string.Join("  ·  ", Project.Software);
    public string TagsText => Project.Tags.Count == 0 ? "暂无标签" : string.Join("   ", Project.Tags.Select(tag => $"#{tag}"));
    public string WaitText => Project.WorkflowStatus == WorkflowStatus.Waiting && !string.IsNullOrWhiteSpace(Project.WaitReason) ? Project.WaitReason : string.Empty;
    public bool HasWaitText => Project.WorkflowStatus == WorkflowStatus.Waiting && !string.IsNullOrWhiteSpace(Project.WaitReason);
    public string CurrentVersion => Project.CurrentVersion;
    public string UpdatedText => Project.UpdatedAt.ToString("yyyy-MM-dd HH:mm");
    public string CreatedText => Project.CreatedAt.ToString("yyyy-MM-dd");
    public string StartedText => Project.StartedAt?.ToString("yyyy-MM-dd HH:mm") ?? "尚未开始";
    public string CompletedText => Project.CompletedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string StorageText => Project.StorageLocation switch
    {
        StorageLocationCode.LocalWork => "本机",
        StorageLocationCode.WorkstationWork => "工作站",
        StorageLocationCode.WorkstationArchive => "已归档",
        _ => "未知"
    };
    public string DurationText
    {
        get
        {
            if (Project.StartedAt is null) return "尚未开始";
            var end = Project.CompletedAt ?? DateTimeOffset.Now;
            var active = Math.Max(0, (long)(end - Project.StartedAt.Value).TotalSeconds - TotalWaitSeconds);
            return FormatDuration(active);
        }
    }
    public string WaitDurationText => FormatDuration(TotalWaitSeconds);
    public long TotalWaitSeconds => Project.AccumulatedWaitSeconds +
        (Project.WaitStartedAt.HasValue ? Math.Max(0, (long)(DateTimeOffset.Now - Project.WaitStartedAt.Value).TotalSeconds) : 0);
    public string MetadataStateText => Project.MetadataSyncState == "Synced" ? string.Empty : "项目档案待同步";
    public bool IsMetadataPending => Project.MetadataSyncState != "Synced";
    public string PreviewUri => "ms-appx:///Assets/ProjectPreview.png";

    private static string FormatDuration(long seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays} 天 {duration.Hours} 小时";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟";
        return $"{Math.Max(0, duration.Minutes)} 分钟";
    }
}

public sealed record StatCardViewModel(string Title, int Count, string IconGlyph, string IconBackground, string AccentColor, string Key)
{
    /// <summary>供读屏器朗读统计值与按钮行为，避免只读出装饰性卡片内容。</summary>
    public string AutomationName => $"{Title}，{Count} 个项目，打开筛选";
}

/// <summary>标签管理页的一行：名称、使用项目数、是否可删除。</summary>
public sealed record TagSummaryViewModel(string Name, int Count)
{
    public string CountText => $"{Count} 个项目";
    public bool CanDelete => Count == 0;
}
