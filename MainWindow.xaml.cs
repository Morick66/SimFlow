using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Markup;
using SimFlow.Models;
using SimFlow.Services;
using SimFlow.ViewModels;
using SimFlow.Views;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Storage.Pickers;

namespace SimFlow;

public sealed partial class MainWindow : Window
{
    private readonly IProjectScannerService _scanner;
    private readonly IConfigurationService _configuration;
    private readonly IStorageLocationService _storage;
    private readonly ISimulationReportService _simulationReports;
    private readonly ILogger<MainWindow> _logger;
    private CancellationTokenSource? _migrationCancellation;
    private readonly DispatcherTimer _infoBarTimer = new();
    private ProjectCardViewModel? _draggedProject;

    /// <summary>
    /// 浮动副本的放大比例：按住握把时显示的卡片图比原卡片大一点，像被拎起来。
    /// </summary>
    private const double CardGhostScale = 1.03;

    /// <summary>
    /// 看板卡片封面的固定宽高比（宽 ÷ 高）。看板四列等分，窗口越宽卡片越宽，用固定高度会得到
    /// 又扁又长的横条；改成固定比例后，任何窗口宽度下封面形状都一致。
    /// 数值越小封面越高：2.0 偏扁、1.6（当前）、1.5 接近 3:2、1.33 接近 4:3。
    /// </summary>
    private const double KanbanCoverAspectRatio = 1.6;

    /// <summary>
    /// 封面高度上限，避免超宽窗口下封面把整张卡片撑得过高。
    /// 必须明显大于常见窗口下的高度（约 180–250px），否则上限一旦生效比例就被压平。
    /// </summary>
    private const double KanbanCoverMaxHeight = 320;

    /// <summary>进入拖动的位移阈值（px）：小于它只算"按住"，不高亮目标列、松手也不改状态。</summary>
    private const double KanbanDragThreshold = 6;

    /// <summary>目标列高亮用的边框画刷（半透明主色），四列共用。</summary>
    private static readonly Brush KanbanTargetBorderBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x25, 0x63, 0xEB));

    /// <summary>
    /// 按住握把时预先渲染好的卡片图。整个拖动过程只有这一个视觉对象（浮动副本），
    /// 位置完全由 PointerMoved 驱动，不再交给系统拖拽视觉。
    /// </summary>
    private BitmapImage? _cardGhostImage;

    /// <summary>指针在上面那张图里的位置；副本按"这个点压在指针下"来摆位。</summary>
    private Point _cardGhostAnchor;

    /// <summary>
    /// 浮动副本位图背后的流。必须与位图同生共死：BitmapImage 在设置源之后仍会从流里取数据，
    /// 提前释放会让图片渲染成空白。下一次抓图替换它时才释放。
    /// </summary>
    private InMemoryRandomAccessStream? _cardGhostStream;

    /// <summary>看板拖动的手势状态。</summary>
    private KanbanDragState _dragState;

    /// <summary>发起本次拖动的握把，松手时用它释放指针捕获。</summary>
    private FrameworkElement? _dragHandle;

    /// <summary>按下位置（RootGrid 坐标），用于判断是否越过拖动阈值。</summary>
    private Point _dragOriginInRoot;

    /// <summary>手势代号：预渲染是异步的，回来时若已经开始了新的按压就丢弃这次结果。</summary>
    private int _dragToken;

    /// <summary>四列的命中区域（RootGrid 坐标），进入拖动时刷新一次；拖动期间布局不变。</summary>
    private KanbanColumnArea[]? _columnAreas;

    /// <summary>指针在 RootGrid 坐标系里的最新位置；副本摆位与目标列判定都用它。</summary>
    private Point _pointerInRoot;

    /// <summary>浮动副本当前是否已显示。</summary>
    private bool _cardGhostVisible;

    /// <summary>看板拖动的手势状态机。整个手势不经过任何系统拖放机制。</summary>
    private enum KanbanDragState
    {
        /// <summary>没有按在握把上。</summary>
        Idle,

        /// <summary>已按在握把上并捕获指针，但位移还没越过阈值。</summary>
        Armed,

        /// <summary>位移已越过阈值，正在拖动（目标列参与高亮与命中判定）。</summary>
        Dragging
    }

    /// <summary>一列的命中区域（RootGrid 坐标）与它代表的状态。</summary>
    private sealed record KanbanColumnArea(Border Column, WorkflowStatus Status, Rect Bounds);

    /// <summary>内容区可用宽度小于该值时进入“窄窗口”布局（SF-010 独立详情页），单位 px。</summary>
    private const double NarrowBreakpoint = 900;
    private bool _isNarrow;

    /// <summary>
    /// 首页统计卡片里没有独立一级导航入口的筛选键：点击后落到「所有项目」页并应用筛选。
    /// “收藏”有自己的导航项，因此不在这里。
    /// </summary>
    private static readonly string[] PageFilterKeys = ["notstarted", "active", "waiting", "completed", "archived"];

    /// <summary>首页统计卡片点击后要落到「所有项目」页的那个筛选键。</summary>
    private string? _pendingListFilter;

    /// <summary>正在程序化同步筛选条状态，忽略由此产生的 Checked 回调。</summary>
    private bool _syncingFilterChips;

    private static readonly string[] WaitReasons =
        ["等待试验", "等待CAD模型", "等待工程师提供参数", "等待设计方案", "等待方案确认", "等待评审", "等待其他数据", "其他"];

    private static readonly string[] CoverExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];

    public MainViewModel ViewModel { get; }

    public MainWindow(
        MainViewModel viewModel,
        IProjectScannerService scanner,
        IConfigurationService configuration,
        IStorageLocationService storage,
        ISimulationReportService simulationReports,
        ILogger<MainWindow> logger)
    {
        ViewModel = viewModel;
        _scanner = scanner;
        _configuration = configuration;
        _storage = storage;
        _simulationReports = simulationReports;
        _logger = logger;
        InitializeComponent();
        _infoBarTimer.Tick += (_, _) =>
        {
            _infoBarTimer.Stop();
            StatusInfoBar.IsOpen = false;
        };
        StatusInfoBar.CloseButtonClick += (_, _) => _infoBarTimer.Stop();
        // 可见性统一由 ViewModel 状态驱动。
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Activated += MainWindow_Activated;
        RootGrid.Loaded += RootGrid_Loaded;
        // 统计页卡片上的“查看全部 →”交给壳层导航处理（页本身不直接操作 NavigationView）。
        StatisticsPanel.ViewAllRequested += (_, key) => NavigateToFilter(key);
        TryResizeWindow();
        UpdateViewState();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateViewState();
            if (e.PropertyName == nameof(MainViewModel.SelectedProject)) UpdateSelectedPath();
        }
        else
        {
            DispatcherQueue.TryEnqueue(UpdateViewState);
        }
    }

    private void UpdateViewState()
    {
        var selectedProject = ViewModel.SelectedProject;
        var hasSelection = selectedProject is not null;
        var canAccessFiles = selectedProject?.IsLocationAvailable == true;
        DetailPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        DetailPlaceholder.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        StatsRow.Visibility = AsVisibility(ViewModel.IsHome);
        KanbanBoard.Visibility = AsVisibility(ViewModel.IsHome);
        ProjectListPanel.Visibility = AsVisibility(ViewModel.ShowProjectList);
        ProjectListEmptyState.Visibility = AsVisibility(!ViewModel.HasProjects);
        SettingsPanel.Visibility = AsVisibility(ViewModel.IsSettings);
        StatisticsPanel.Visibility = AsVisibility(ViewModel.IsStatistics);
        // 统计页自带标题与时间范围：整行收掉（行高 + 内容一起），避免标题重复占位或溢出。
        var showSectionHeader = !ViewModel.IsStatistics;
        SectionHeader.Visibility = AsVisibility(showSectionHeader);
        SectionHeaderRow.Height = showSectionHeader ? new GridLength(56) : new GridLength(0);
        SortCombo.Visibility = AsVisibility(ViewModel.ShowProjectList);
        SetCoverButton.IsEnabled = canAccessFiles;
        ClearCoverButton.IsEnabled = canAccessFiles;
        ProjectPathButton.IsEnabled = canAccessFiles;
        OpenFolderQuickButton.IsEnabled = canAccessFiles;
        NewVersionButton.IsEnabled = canAccessFiles;
        SimulationReportQuickButton.IsEnabled = canAccessFiles;
        StatusChangeButton.IsEnabled = canAccessFiles;
        ChangeStatusMenuItem.IsEnabled = canAccessFiles;
        MigrateMenuItem.IsEnabled = canAccessFiles;
        ArchiveMenuItem.IsEnabled = canAccessFiles
            && selectedProject is not null
            && selectedProject.Project.WorkflowStatus == WorkflowStatus.Completed
            && selectedProject.Project.StorageLocation != StorageLocationCode.WorkstationArchive;
        UpdateResponsiveLayout();
    }

    private static Visibility AsVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>内容区尺寸变化（窗口缩放、导航栏收起/展开）时重算布局。</summary>
    private void ContentRoot_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    /// <summary>
    /// SF-010 响应式详情：
    /// 内容区够宽（≥ <see cref="NarrowBreakpoint"/>）时保持“列表 + 详情”双栏；
    /// 内容区变窄时切换为独立的详情页——未选中项目时列表占满整列，
    /// 点击项目后详情占满并显示“返回”按钮，返回后回到列表/看板。
    /// </summary>
    private void UpdateResponsiveLayout()
    {
        if (ListColumn is null || DetailColumn is null || DetailBorder is null || BackButton is null)
        {
            return;
        }

        var narrow = ContentRoot.ActualWidth < NarrowBreakpoint;
        var hasSelection = ViewModel.SelectedProject is not null;
        _isNarrow = narrow;

        if (ViewModel.IsSettings)
        {
            // 设置页：不显示项目详情，右侧列改为 Settings 导航（宽度有限、固定）。
            ListColumn.MinWidth = 360;
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(narrow ? 224 : 256);
            DetailBorder.Visibility = Visibility.Collapsed;
            SettingsNavPane.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (ViewModel.IsStatistics)
        {
            // 统计页自带内容、不需要项目详情：右侧列收成 0，页面本身占满可用宽度。
            ListColumn.MinWidth = 360;
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(0);
            DetailBorder.Visibility = Visibility.Collapsed;
            SettingsNavPane.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (narrow)
        {
            // 独立详情页：列表与详情互斥，谁有用谁占满，另一列收成 0（同时清掉 MinWidth 约束）。
            ListColumn.MinWidth = hasSelection ? 0 : 440;
            ListColumn.Width = hasSelection ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = hasSelection ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            DetailBorder.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            SettingsNavPane.Visibility = Visibility.Collapsed;
            BackButton.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            ListColumn.MinWidth = 440;
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(360);
            DetailBorder.Visibility = Visibility.Visible;
            SettingsNavPane.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.InitializeAsync();
            if (string.Equals(Environment.GetEnvironmentVariable("SIMFLOW_SEED_DEMO"), "1", StringComparison.Ordinal))
            {
                await SeedDemoDataAsync();
            }
            UpdateSelectedPath();
        }
        catch (Exception ex)
        {
            ShowError("加载项目失败", ex);
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) UpdateSelectedPath();
    }

    private void TryResizeWindow()
    {
        try
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "SimFlow.ico"));
            // 按显示区域工作区钳制，避免在较小屏幕上把右侧详情栏顶出屏幕外（曾导致“详情看不见”）。
            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest).WorkArea;
            var width = Math.Min(1536, area.Width - 24);
            var height = Math.Min(960, area.Height - 24);
            appWindow.Resize(new SizeInt32(Math.Max(800, width), Math.Max(600, height)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not set the initial window size.");
        }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string key)
        {
            return;
        }

        // 首页统计卡片跳到某个状态筛选时，先切到「所有项目」再应用筛选；
        // 目标筛选键通过 _pendingListFilter 带过来（避免被这一层的 SetFilter("all") 覆盖）。
        var applied = _pendingListFilter is { Length: > 0 } pending && string.Equals(key, "all", StringComparison.Ordinal)
            ? pending
            : key;
        _pendingListFilter = null;
        ApplySection(applied);
    }

    /// <summary>切换分区：设置/统计页各自有额外动作；窄窗口下同时回到列表。</summary>
    private void ApplySection(string key)
    {
        ViewModel.SetFilter(key);
        SyncFilterChips();
        if (string.Equals(key, "settings", StringComparison.Ordinal))
        {
            RefreshSettingsInputs();
        }
        else if (string.Equals(key, "statistics", StringComparison.Ordinal))
        {
            _ = StatisticsPanel.RefreshAsync();
        }

        // 窄窗口下详情是独立页：切换分区后回到该分区的列表，再由用户点击进入详情。
        if (_isNarrow)
        {
            ViewModel.SelectedProject = null;
            ClearAllSelections();
        }
    }

    /// <summary>「所有项目」页内的筛选条：状态/归档/收藏筛选（原来在侧边栏）。</summary>
    private void ListFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingFilterChips || sender is not FrameworkElement { Tag: string key })
        {
            return;
        }

        ViewModel.SetFilter(key);
        ViewModel.SelectedProject = null;
        ClearAllSelections();
    }

    /// <summary>把筛选条的单选状态同步到当前筛选键（程序化设置时不再回调触发切换）。</summary>
    private void SyncFilterChips()
    {
        _syncingFilterChips = true;
        try
        {
            foreach (var chip in FilterChips())
            {
                // 导航的初始 SelectionChanged 可能早于筛选条创建（XAML 顺序），这里按未创建跳过。
                if (chip is not null)
                {
                    chip.IsChecked = string.Equals(chip.Tag as string, ViewModel.FilterKey, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            _syncingFilterChips = false;
        }
    }

    private IReadOnlyList<RadioButton> FilterChips() =>
    [
        ListFilterAll, ListFilterNotStarted, ListFilterActive, ListFilterWaiting,
        ListFilterCompleted, ListFilterArchived, ListFilterFavorite
    ];

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.ApplySearch(SearchBox.Text);
        if (SearchHintText is not null)
        {
            SearchHintText.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Ctrl+K：聚焦搜索框。</summary>
    private void SearchShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo is null) return;
        ViewModel.SetSortMode(SortCombo.SelectedIndex switch
        {
            1 => ProjectSortMode.RecentlyCreated,
            2 => ProjectSortMode.Name,
            _ => ProjectSortMode.RecentlyUpdated
        });
    }

    private void ProjectList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProjectCardViewModel item)
        {
            ViewModel.SelectedProject = item;
            UpdateSelectedPath();
        }
    }

    /// <summary>
    /// 看板点击：选中项目并清掉其他列的选中态。
    /// 注意：四个列都不能用 SelectedItem 双向绑定同一个 ViewModel 属性——
    /// 列 A 点击会把 SelectedProject 置为 A 的卡片，其余三列收到变化后把 SelectedItem 置空
    /// （它们的列表里没有该卡片），再反向写回 SelectedProject，形成重入循环并立即清掉详情。
    /// 点击由 ItemClick 单一来源完成，SelectionMode 只负责高亮。
    /// </summary>
    private void KanbanCard_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ProjectCardViewModel item)
        {
            return;
        }

        _logger.LogInformation("看板点击选中项目 {Code}", item.ProjectCode);
        ViewModel.SelectedProject = item;
        ClearOtherKanbanSelections(sender);
        UpdateSelectedPath();
    }

    private void ClearOtherKanbanSelections(object current)
    {
        foreach (var list in new ListView[] { NotStartedList, ActiveList, WaitingList, CompletedList })
        {
            if (!ReferenceEquals(list, current) && list.SelectedItem is not null)
            {
                list.SelectedItem = null;
            }
        }
    }

    /// <summary>窄窗口独立详情页的返回：清掉选中，回到列表/看板。</summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedProject = null;
        ClearAllSelections();
    }

    private void ClearAllSelections()
    {
        ProjectList.SelectedItem = null;
        foreach (var list in new ListView[] { NotStartedList, ActiveList, WaitingList, CompletedList })
        {
            list.SelectedItem = null;
        }
    }

    private void UpdateSelectedPath()
    {
        try
        {
            var path = ViewModel.GetSelectedProjectPath() ?? string.Empty;
            ProjectPathText.Text = path;
            if (ViewModel.SelectedProject is not null) NotesTextBox.Text = ViewModel.SelectedProject.Project.Notes;
            _logger.LogInformation("详情路径已更新：{Path}", path);
        }
        catch (Exception ex)
        {
            ProjectPathText.Text = ex.Message;
            _logger.LogWarning(ex, "更新详情路径失败");
        }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        // 首次使用时不要让用户填完整张表单后才得知默认的本机 Work 未配置。
        // 先给出原因与明确去向，确认后直接定位到“设置 > 项目目录”。
        if (string.IsNullOrWhiteSpace(_configuration.Current.LocalWorkRoot))
        {
            var result = await ConfirmAsync(
                "请先配置项目目录",
                "新项目默认保存在本机 Work。请先到“设置 > 项目目录”选择一个文件夹，保存设置后再新建项目。",
                "去设置",
                "稍后");
            if (result == ContentDialogResult.Primary)
            {
                NavigateToFilter("settings");
                SettingsNavDirectories.IsChecked = true;
                SetSettingsSection("directories");
                SettingsLocalWorkBox.Focus(FocusState.Programmatic);
            }
            return;
        }

        // 两列都参与可用宽度分配，不再用固定宽度把需求人输入框顶出 ContentDialog。
        var name = new TextBox { Header = "项目名称 *", PlaceholderText = "例如：150kA短耐动稳定性分析", MinWidth = 0 };
        var requester = new TextBox { Header = "需求人", PlaceholderText = "提出仿真需求的人", MinWidth = 0 };
        var nameRow = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.Children.Add(name);
        Grid.SetColumn(requester, 1);
        nameRow.Children.Add(requester);

        var description = new TextBox { Header = "项目描述", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 78 };
        var simulationType = BuildSimulationTypePicker(null);
        var tags = new TagEditor(ViewModel.AllTags, []);
        var softwarePicker = BuildSoftwareCardPicker([]);
        var startNow = new CheckBox { Content = "创建后立即开始", IsChecked = true };
        var favorite = new CheckBox { Content = "加入收藏" };
        var location = new ComboBox { Header = "初始存储位置", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        location.Items.Add(new ComboBoxItem { Content = "本机 Work", Tag = StorageLocationCode.LocalWork });
        location.Items.Add(new ComboBoxItem { Content = "工作站 Work", Tag = StorageLocationCode.WorkstationWork });
        var content = new StackPanel { Spacing = 14, Width = 500 };
        content.Children.Add(nameRow);
        content.Children.Add(simulationType);
        content.Children.Add(description);
        content.Children.Add(tags.Root);
        content.Children.Add(new TextBlock { Text = "使用软件（可多选）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(softwarePicker);
        content.Children.Add(location);
        content.Children.Add(startNow);
        content.Children.Add(favorite);

        var dialog = CreateDialog("新建项目", new ScrollViewer
        {
            Content = content,
            MaxHeight = 620,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        }, "创建项目");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                args.Cancel = true;
                name.Focus(FocusState.Programmatic);
            }
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            var target = (location.SelectedItem as ComboBoxItem)?.Tag is StorageLocationCode code ? code : StorageLocationCode.LocalWork;
            await ViewModel.CreateProjectAsync(new CreateProjectRequest
            {
                Name = name.Text,
                SimulationType = ReadSimulationType(simulationType),
                Requester = requester.Text,
                Description = description.Text,
                Tags = tags.Tags,
                Software = ReadSoftwarePickerSelection(softwarePicker),
                InitialLocation = target,
                StartImmediately = startNow.IsChecked == true,
                IsFavorite = favorite.IsChecked == true
            });
            UpdateSelectedPath();
            ShowSuccess("项目已创建", ViewModel.StatusMessage);
        }
        catch (Exception ex)
        {
            ShowError("创建项目失败", ex);
        }
    }

    private async void EditProject_Click(object sender, RoutedEventArgs e)
    {
        var project = ViewModel.SelectedProject?.Project;
        if (project is null) return;

        var name = new TextBox { Header = "项目名称 *", Text = project.Name, MinWidth = 0 };
        var requester = new TextBox { Header = "需求人", Text = project.Requester, PlaceholderText = "提出仿真需求的人", MinWidth = 0 };
        var nameRow = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.Children.Add(name);
        Grid.SetColumn(requester, 1);
        nameRow.Children.Add(requester);

        var description = new TextBox { Header = "项目描述", Text = project.Description, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96 };
        var simulationType = BuildSimulationTypePicker(project.SimulationType);
        var tags = new TagEditor(ViewModel.AllTags, project.Tags);
        // 使用软件：图标墙多选，候选与「软件标签」同一来源（SF-024）。
        var softwarePicker = BuildSoftwareCardPicker(project.Software);

        var content = new StackPanel { Spacing = 14, Width = 500 };
        content.Children.Add(nameRow);
        content.Children.Add(simulationType);
        content.Children.Add(description);
        content.Children.Add(tags.Root);
        content.Children.Add(new TextBlock { Text = "使用软件（标签，可多选）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(softwarePicker);
        content.Children.Add(new TextBlock
        {
            Text = "项目编号和目录名不会改变。",
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 104, 119, 146)),
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = CreateDialog("编辑项目信息", new ScrollViewer
        {
            Content = content,
            MaxHeight = 640,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        }, "保存");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                args.Cancel = true;
                name.Focus(FocusState.Programmatic);
            }
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            await ViewModel.UpdateInfoAsync(new UpdateProjectInfoRequest
            {
                Name = name.Text,
                SimulationType = ReadSimulationType(simulationType),
                Requester = requester.Text,
                Description = description.Text,
                Tags = tags.Tags,
                Software = ReadSoftwarePickerSelection(softwarePicker)
            });
            UpdateSelectedPath();
            ShowSuccess("项目信息已更新", ViewModel.StatusMessage);
        }
        catch (Exception ex)
        {
            ShowError("保存项目信息失败", ex);
        }
    }

    /// <summary>
    /// 软件多选卡片（新建/编辑项目对话框共用）：图标 + 名称，整卡可点击，选中态取 ToggleButton 的 Fluent Accent。
    /// 候选来自软件标签，未配置时回退默认集合；自动换行、不横向溢出。
    /// </summary>
    private GridView BuildSoftwareCardPicker(IReadOnlyCollection<string> selected)
    {
        var picker = new GridView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
            MaxHeight = 240
        };
        // 代码中创建 ItemsPanelTemplate：该版本无 Func 构造函数，用 XamlReader 解析受支持的写法。
        picker.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' /></ItemsPanelTemplate>");
        // 候选 = 配置里的软件 ∪ 项目已选的软件（后者即使已从配置删除也保留，避免编辑时静默丢失）。
        var names = SoftwareNames()
            .Union(selected, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var name in names)
        {
            var tile = new ToggleButton
            {
                Tag = name,
                IsChecked = selected.Contains(name, StringComparer.OrdinalIgnoreCase),
                MinWidth = 152,
                Height = 54,
                Margin = new Thickness(0, 0, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var content = new Grid { ColumnSpacing = 8 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 234, 241, 255)) };
            icon.Child = new FontIcon { Glyph = "\uE7F4", FontSize = 15, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 99, 235)) };
            content.Children.Add(icon);
            var label = new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 1);
            content.Children.Add(label);
            tile.Content = content;
            picker.Items.Add(tile);
        }
        return picker;
    }

    private static IReadOnlyList<string> ReadSoftwarePickerSelection(GridView picker)
        => picker.Items.OfType<ToggleButton>()
            .Where(tile => tile.IsChecked == true)
            .Select(tile => tile.Tag?.ToString() ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToList();

    /// <summary>
    /// 仿真类型单选（新建/编辑项目对话框共用）。第一项是“未分类”，对应 null，
    /// 因此旧项目打开编辑时不会凭空产生一个类型，也不是必填项。
    /// </summary>
    private static ComboBox BuildSimulationTypePicker(SimulationType? selected)
    {
        var combo = new ComboBox { Header = "仿真类型", HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.Items.Add(new ComboBoxItem { Content = SimulationTypes.Unclassified, Tag = null });
        foreach (var type in SimulationTypes.All)
        {
            combo.Items.Add(new ComboBoxItem { Content = SimulationTypes.Describe(type), Tag = type });
        }

        combo.SelectedIndex = selected is { } value ? SimulationTypes.IndexOf(value) + 1 : 0;
        return combo;
    }

    private static SimulationType? ReadSimulationType(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as SimulationType?;

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ToggleFavoriteAsync(); }
        catch (Exception ex) { ShowError("更新收藏失败", ex); }
    }

    /// <summary>
    /// 状态便签直接展开可用目标状态；只有进入“等待中”时再补充必要的等待原因。
    /// </summary>
    private void ChangeStatus_Click(object sender, RoutedEventArgs e)
    {
        var card = (sender as FrameworkElement)?.DataContext as ProjectCardViewModel ?? ViewModel.SelectedProject;
        var project = card?.Project;
        if (project is null || sender is not FrameworkElement anchor) return;

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };

        foreach (var option in BuildStatusOptions(project.WorkflowStatus))
        {
            var item = new MenuFlyoutItem { Text = option.Label, Tag = option.Status };
            item.Click += async (_, _) => await ApplyStatusSelectionAsync(project, option.Status);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = "没有可用操作", IsEnabled = false });
        }

        flyout.ShowAt(anchor);
    }

    private async Task ApplyStatusSelectionAsync(ProjectRecord project, WorkflowStatus target)
    {
        if (!await EnsureProjectStorageOnlineAsync(project, "修改状态")) return;

        string? waitReason = null;
        string? waitNote = null;
        if (target == WorkflowStatus.Waiting)
        {
            var reason = new ComboBox { Header = "等待原因 *", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var item in WaitReasons) reason.Items.Add(item);
            reason.SelectedIndex = 0;
            var note = new TextBox { Header = "等待说明", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96 };
            var panel = new StackPanel { Width = 460, Spacing = 14 };
            panel.Children.Add(reason);
            panel.Children.Add(note);

            if (await ShowDialogAsync(CreateDialog("设为等待", panel, "确认")) != ContentDialogResult.Primary) return;
            waitReason = reason.SelectedItem?.ToString();
            waitNote = note.Text.Trim();
            if (string.IsNullOrWhiteSpace(waitReason))
            {
                ShowWarning("需要等待原因", "进入等待状态必须选择等待原因。");
                return;
            }
        }

        try
        {
            await ViewModel.ChangeStatusAsync(project, target, waitReason, waitNote);
            ShowSuccess("状态已更新", ViewModel.StatusMessage);
        }
        catch (Exception ex)
        {
            ShowError("状态操作失败", ex);
        }
    }

    private static List<(string Label, WorkflowStatus Status)> BuildStatusOptions(WorkflowStatus current)
        => Enum.GetValues<WorkflowStatus>()
            .Where(status => status != current)
            .Select(status => ($"设为{WorkflowRules.Describe(status)}", status))
            .ToList();

    private static string Describe(WorkflowStatus status) => status switch
    {
        WorkflowStatus.NotStarted => "未开始",
        WorkflowStatus.Active => "进行中",
        WorkflowStatus.Waiting => "等待中",
        WorkflowStatus.Completed => "已完成",
        _ => "未知"
    };

    private async void SetCover_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is null) return;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var extension in CoverExtensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            await ViewModel.SetCoverImageAsync(file.Path);
            UpdateSelectedPath();
            ShowSuccess("封面已更新", string.Empty);
        }
        catch (Exception ex)
        {
            ShowError("设置封面失败", ex);
        }
    }

    private async void ClearCover_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is null) return;
        try
        {
            await ViewModel.ClearCoverImageAsync();
            UpdateSelectedPath();
            ShowSuccess("封面已移除", string.Empty);
        }
        catch (Exception ex)
        {
            ShowError("移除封面失败", ex);
        }
    }

    private async void NewVersion_Click(object sender, RoutedEventArgs e)
    {
        var title = new TextBox { Header = "版本标题 *", PlaceholderText = "例如：提高触头弹簧力" };
        var summary = new TextBox { Header = "修改内容", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
        var panel = new StackPanel { Width = 460, Spacing = 10 };
        panel.Children.Add(title);
        panel.Children.Add(summary);
        var dialog = CreateDialog("创建新版本", panel, "创建版本");
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try { await ViewModel.CreateVersionAsync(title.Text, summary.Text); ShowSuccess("版本已创建", ViewModel.StatusMessage); }
        catch (Exception ex) { ShowError("创建版本失败", ex); }
    }

    private async void SaveNotes_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.UpdateNotesAsync(NotesTextBox.Text); ShowSuccess("备注已保存", string.Empty); }
        catch (Exception ex) { ShowError("保存备注失败", ex); }
    }

    /// <summary>
    /// 删除版本：可选是否把磁盘上的版本目录移入项目恢复区。目录已不存在时只清记录；
    /// 删的是当前版本时服务层会把当前版本回退到剩余最高版本。
    /// </summary>
    private async void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is null) return;
        if ((sender as FrameworkElement)?.DataContext is not ProjectVersionRecord version) return;
        if (ViewModel.Versions.Count <= 1)
        {
            ShowWarning("无法删除", "项目至少需要保留一个版本。");
            return;
        }

        var project = ViewModel.SelectedProject.Project;
        var projectDirectory = ViewModel.GetSelectedProjectPath();
        var versionDirectory = string.IsNullOrWhiteSpace(projectDirectory)
            ? string.Empty
            : Path.Combine(projectDirectory, version.RelativePath);
        var folderExists = versionDirectory.Length > 0 && Directory.Exists(versionDirectory);

        var deleteFolder = new CheckBox { Content = "同时将版本文件夹移入恢复区", IsChecked = folderExists };
        if (!folderExists)
        {
            deleteFolder.IsChecked = false;
            deleteFolder.IsEnabled = false;
            deleteFolder.Content = "版本文件夹不存在，只清理记录";
        }

        var muted = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 104, 119, 146));
        var panel = new StackPanel { Width = 460, Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"确定删除版本「{version.VersionNumber}　{version.Title}」？",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = folderExists
                ? $"版本目录：{versionDirectory}\n勾选后目录会移动到项目内的 .simflow-recovery，不会永久删除。"
                : "磁盘上找不到这个版本的目录，只会清理数据库记录。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = muted
        });
        if (string.Equals(project.CurrentVersion, version.VersionNumber, StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "这是当前版本：删除后当前版本会回退到剩余的最高版本。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = muted
            });
        }

        panel.Children.Add(deleteFolder);
        if (await ShowDialogAsync(CreateDialog("删除版本", panel, "删除")) != ContentDialogResult.Primary) return;
        try
        {
            await ViewModel.DeleteVersionAsync(version, deleteFolder.IsChecked == true);
            ShowSuccess("版本已删除", ViewModel.StatusMessage);
        }
        catch (Exception ex)
        {
            ShowError("删除版本失败", ex);
        }
    }

    private void CoverPreview_PointerEntered(object sender, PointerRoutedEventArgs e)
        => CoverActionsOverlay.Visibility = Visibility.Visible;

    private void CoverPreview_PointerExited(object sender, PointerRoutedEventArgs e)
        => CoverActionsOverlay.Visibility = Visibility.Collapsed;

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = ViewModel.GetSelectedProjectPath();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("无法打开项目目录", ex); }
    }

    /// <summary>删除项目：可选把文件夹移入 Work 根恢复区；删除不依赖目录是否可用。</summary>
    private async void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        var project = ViewModel.SelectedProject?.Project;
        if (project is null) return;

        var deleteFolder = new CheckBox { Content = "同时将项目文件夹移入恢复区", IsChecked = false };
        var path = ViewModel.GetSelectedProjectPath();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            deleteFolder.Content = "项目文件夹不存在或不可用，只删除列表记录";
            deleteFolder.IsEnabled = false;
        }

        var panel = new StackPanel { Width = 460, Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"确定删除项目「{project.Name}」（{project.ProjectCode}）？",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "仅从列表删除会保留原文件夹；勾选后文件夹会移动到 Work 根的 .simflow-recovery，不会永久删除。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 104, 119, 146))
        });
        panel.Children.Add(deleteFolder);

        var dialog = CreateDialog("删除项目", panel, "删除");
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            await ViewModel.DeleteProjectAsync(deleteFolder.IsChecked == true);
            ClearAllSelections();
            ShowSuccess("项目已删除", ViewModel.StatusMessage);
        }
        catch (Exception ex)
        {
            ShowError("删除项目失败", ex);
        }
    }

    private async void Migrate_Click(object sender, RoutedEventArgs e)
    {
        var project = ViewModel.SelectedProject?.Project;
        if (project is null) return;
        var combo = new ComboBox { Header = "目标位置", HorizontalAlignment = HorizontalAlignment.Stretch };
        if (project.StorageLocation != StorageLocationCode.LocalWork) combo.Items.Add(new ComboBoxItem { Content = "本机 Work", Tag = StorageLocationCode.LocalWork });
        if (project.StorageLocation != StorageLocationCode.WorkstationWork) combo.Items.Add(new ComboBoxItem { Content = "工作站 Work", Tag = StorageLocationCode.WorkstationWork });
        combo.SelectedIndex = 0;
        var dialog = CreateDialog("迁移项目", combo, "开始迁移");
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if ((combo.SelectedItem as ComboBoxItem)?.Tag is StorageLocationCode target) await StartMigrationAsync(target);
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        var project = ViewModel.SelectedProject?.Project;
        if (project is null) return;
        if (project.WorkflowStatus != WorkflowStatus.Completed)
        {
            ShowWarning("暂不能归档", "只有已完成的项目可以归档。");
            return;
        }

        if (project.StorageLocation == StorageLocationCode.WorkstationArchive)
        {
            ShowWarning("项目已归档", "当前项目已经位于工作站 Archive。");
            return;
        }

        var confirmation = await ConfirmAsync(
            "归档项目",
            $"将“{project.Name}”安全迁移到工作站 Archive，并按归档时间存入年/月目录。是否继续？",
            "开始归档");
        if (confirmation == ContentDialogResult.Primary)
        {
            await StartMigrationAsync(StorageLocationCode.WorkstationArchive);
        }
    }

    private async void SimulationReport_Click(object sender, RoutedEventArgs e)
    {
        var project = ViewModel.SelectedProject?.Project;
        if (project is null) return;

        try
        {
            var report = await _simulationReports.PrepareAsync(project);
            Process.Start(new ProcessStartInfo(report.Path) { UseShellExecute = true });
            ShowSuccess(
                report.Created ? "仿真报告已创建" : "已打开仿真报告",
                report.Created ? $"模板已复制到：{report.Path}" : $"已识别并打开 Delivery 中的现有报告：{report.Path}");
        }
        catch (Exception ex)
        {
            ShowError("无法创建仿真报告", ex);
        }
    }

    private async Task StartMigrationAsync(StorageLocationCode target)
    {
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6 };
        var stage = new TextBlock { Text = "准备迁移...", TextWrapping = TextWrapping.Wrap };
        var current = new TextBlock { Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 104, 119, 146)), TextTrimming = TextTrimming.CharacterEllipsis };
        var panel = new StackPanel { Width = 480, Spacing = 12 };
        panel.Children.Add(stage);
        panel.Children.Add(progressBar);
        panel.Children.Add(current);
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "正在安全迁移", Content = panel, CloseButtonText = "取消" };
        _migrationCancellation = new CancellationTokenSource();
        dialog.CloseButtonClick += (_, _) => _migrationCancellation.Cancel();
        var progress = new Progress<MigrationProgress>(value =>
        {
            progressBar.Value = value.Percent;
            stage.Text = value.Message;
            current.Text = value.CurrentFile ?? string.Empty;
        });
        var showTask = dialog.ShowAsync().AsTask();
        try
        {
            await ViewModel.MigrateAsync(target, progress, _migrationCancellation.Token);
            dialog.Hide();
            await showTask;
            UpdateSelectedPath();
            ShowWarning(
                "迁移完成，源副本已保留",
                "目标已校验并成为正式主副本。首发版不会自动永久删除源目录；请停止求解器并人工核对目标后，再在文件系统中备份或清理旧副本。");
        }
        catch (OperationCanceledException)
        {
            dialog.Hide();
            await showTask;
            ShowWarning("迁移已取消", "源目录保持不变，暂存目录可在重试时处理。");
        }
        catch (Exception ex)
        {
            dialog.Hide();
            await showTask;
            ShowError("迁移失败，源目录未删除", ex);
        }
        finally
        {
            _migrationCancellation.Dispose();
            _migrationCancellation = null;
        }
    }

    private void SettingsNav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || radio.Tag is not string key) return;
        SetSettingsSection(key);
    }

    private void SetSettingsSection(string key)
    {
        SettingsGeneralSection.Visibility = AsVisibility(key == "general");
        SettingsDirectoriesSection.Visibility = AsVisibility(key == "directories");
        SettingsTagsSection.Visibility = AsVisibility(key == "tags");
        SettingsSoftwareSection.Visibility = AsVisibility(key == "software");
        SettingsMigrationSection.Visibility = AsVisibility(key == "migration");
        SettingsDataSection.Visibility = AsVisibility(key == "data");
        SettingsHelpSection.Visibility = AsVisibility(key == "help");
    }

    private void RefreshSettingsInputs()
    {
        var config = _configuration.Current;
        SettingsPlaceholderBox.Text = config.ProjectPlaceholderImage;
        SettingsReportTemplateBox.Text = config.SimulationReportTemplatePath;
        SettingsLocalWorkBox.Text = config.LocalWorkRoot;
        SettingsWorkstationBox.Text = config.WorkstationWorkRoot;
        SettingsArchiveBox.Text = config.WorkstationArchiveRoot;
    }

    private void SettingsOpenLocal_Click(object sender, RoutedEventArgs e) => OpenPath(SettingsLocalWorkBox.Text);
    private void SettingsOpenWorkstation_Click(object sender, RoutedEventArgs e) => OpenPath(SettingsWorkstationBox.Text);
    private void SettingsOpenArchive_Click(object sender, RoutedEventArgs e) => OpenPath(SettingsArchiveBox.Text);

    private void SettingsTestWorkstation_Click(object sender, RoutedEventArgs e) => TestPath(SettingsWorkstationBox.Text);
    private void SettingsTestArchive_Click(object sender, RoutedEventArgs e) => TestPath(SettingsArchiveBox.Text);

    private void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowWarning("目录不可用", path);
            return;
        }

        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("无法打开目录", ex); }
    }

    private void TestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowWarning("路径为空", "请先输入路径再测试连接。");
            return;
        }

        if (Directory.Exists(path))
        {
            ShowSuccess("连接正常", path);
        }
        else
        {
            ShowWarning("连接失败", path);
        }
    }

    private async void SettingsBrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                SettingsLocalWorkBox.Text = folder.Path;
            }
        }
        catch (Exception ex)
        {
            ShowError("选择目录失败", ex);
        }
    }

    private async void SettingsBrowsePlaceholder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var extension in CoverExtensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                SettingsPlaceholderBox.Text = file.Path;
            }
        }
        catch (Exception ex)
        {
            ShowError("选择占位图失败", ex);
        }
    }

    private void SettingsClearPlaceholder_Click(object sender, RoutedEventArgs e)
        => SettingsPlaceholderBox.Text = string.Empty;

    private async void SettingsBrowseReportTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            foreach (var extension in new[] { ".docx", ".docm", ".dotx", ".dotm", ".doc", ".dot", ".odt" })
            {
                picker.FileTypeFilter.Add(extension);
            }
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                SettingsReportTemplateBox.Text = file.Path;
            }
        }
        catch (Exception ex)
        {
            ShowError("选择仿真报告模板失败", ex);
        }
    }

    private void SettingsClearReportTemplate_Click(object sender, RoutedEventArgs e)
        => SettingsReportTemplateBox.Text = string.Empty;

    private async void SaveReportTemplateSettings_Click(object sender, RoutedEventArgs e)
    {
        var path = SettingsReportTemplateBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
        {
            ShowWarning("报告模板不可用", "请选择一个实际存在的模板文件。");
            return;
        }

        try
        {
            var next = MainViewModel.CopyConfiguration(_configuration.Current);
            next.SimulationReportTemplatePath = path;
            await _configuration.SaveAsync(next);
            ShowSuccess("报告模板设置已保存", string.IsNullOrWhiteSpace(path) ? "已清除仿真报告模板。" : "现在可以从项目的更多操作中创建仿真报告。");
        }
        catch (Exception ex)
        {
            ShowError("保存报告模板失败", ex);
        }
    }

    private async void SaveGeneralSettings_Click(object sender, RoutedEventArgs e)
    {
        var path = SettingsPlaceholderBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
            {
                ShowWarning("占位图不可用", "请选择一个实际存在的图片文件。");
                return;
            }

            var extension = Path.GetExtension(path);
            if (!CoverExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                ShowWarning("图片格式不支持", $"请使用 {string.Join("、", CoverExtensions)}。");
                return;
            }
        }

        try
        {
            var next = MainViewModel.CopyConfiguration(_configuration.Current);
            next.ProjectPlaceholderImage = path;
            await _configuration.SaveAsync(next);
            await ViewModel.RefreshAsync();
            ShowSuccess("常规设置已保存", string.IsNullOrWhiteSpace(path) ? "已恢复内置项目占位图。" : "项目卡片和详情将使用新的占位图。");
        }
        catch (Exception ex)
        {
            ShowError("保存常规设置失败", ex);
        }
    }

    /// <summary>设置页「保存设置」：读取页面输入并保存（逻辑与旧“设置”对话框一致）。</summary>
    private bool _savingStorageSettings;

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_savingStorageSettings) return;
        _savingStorageSettings = true;
        if (sender is Control button) button.IsEnabled = false;
        try
        {
            var oldRoot = _configuration.Current.LocalWorkRoot;
            var newRoot = SettingsLocalWorkBox.Text.Trim();
            var workstationRoot = SettingsWorkstationBox.Text.Trim();
            var archiveRoot = SettingsArchiveBox.Text.Trim();
            var copyProjects = false;
            if (!string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(oldRoot) && !string.IsNullOrWhiteSpace(newRoot)
                && ViewModel.AllProjects.Any(project => project.StorageLocation == StorageLocationCode.LocalWork))
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "更换本机 Work 路径",
                    Content = $"将本机项目复制到：\n{newRoot}\n\n全部复制并校验成功后才切换路径；失败时继续使用旧路径。旧目录保留。若已自行准备好目标目录，可选择仅更改路径。",
                    PrimaryButtonText = "复制并切换",
                    SecondaryButtonText = "仅更改路径",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary
                };
                var choice = await dialog.ShowAsync();
                if (choice == ContentDialogResult.None) return;
                copyProjects = choice == ContentDialogResult.Primary;
            }
            await Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<StorageSettingsService>(App.Services)
                .SaveAsync(newRoot, workstationRoot, archiveRoot, copyProjects);
            await ViewModel.RefreshStorageAvailabilityAsync();
            await ViewModel.RefreshAsync();
            ShowSuccess("设置已保存", copyProjects ? "全部项目已复制并校验，新存储路径已生效。" : "新的存储路径会立即用于后续操作。");
        }
        catch (Exception ex) { ShowError("保存设置失败", ex); }
        finally
        {
            _savingStorageSettings = false;
            if (sender is Control control) control.IsEnabled = true;
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _scanner.ScanAsync();
            var resolved = 0;
            // 手工整理过一批 project.json 后，冲突会很多：勾选一次即可对本次扫描剩余冲突全部采用项目档案。
            var applyAllMetadata = false;
            var offerApplyAll = result.Conflicts.Count > 1;
            foreach (var conflict in result.Conflicts)
            {
                if (applyAllMetadata)
                {
                    await _scanner.ResolveConflictAsync(conflict, ScanConflictResolution.UseMetadata);
                    resolved++;
                    continue;
                }

                var database = conflict.DatabaseProject;
                var metadata = conflict.Metadata;
                var comparison = new StackPanel { Width = 560, Spacing = 8 };
                comparison.Children.Add(new TextBlock
                {
                    Text = $"项目编号：{conflict.ProjectCode}\n档案位置：{conflict.MetadataPath}",
                    TextWrapping = TextWrapping.Wrap
                });
                comparison.Children.Add(new TextBlock
                {
                    Text = $"数据库：{database.Name}\n状态：{WorkflowRules.Describe(database.WorkflowStatus)}　更新时间：{database.UpdatedAt:yyyy-MM-dd HH:mm:ss}",
                    TextWrapping = TextWrapping.Wrap
                });
                comparison.Children.Add(new TextBlock
                {
                    Text = $"项目档案：{metadata.Name}\n状态：{WorkflowRules.Describe(metadata.WorkflowStatus)}　更新时间：{metadata.UpdatedAt:yyyy-MM-dd HH:mm:ss}",
                    TextWrapping = TextWrapping.Wrap
                });
                comparison.Children.Add(new TextBlock
                {
                    Text = "采用项目档案会更新数据库；保留数据库会重写同位置档案。若这是另一目录中的重复编号，原档案会改名为 rejected 备份，不会删除。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 104, 119, 146))
                });

                CheckBox? applyAllBox = null;
                if (offerApplyAll)
                {
                    applyAllBox = new CheckBox
                    {
                        Content = $"对本次扫描剩余的 {result.Conflicts.Count - resolved - 1} 个冲突也采用项目档案（不再逐个询问）",
                        IsChecked = false
                    };
                    comparison.Children.Add(applyAllBox);
                }

                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "扫描发现冲突",
                    Content = comparison,
                    PrimaryButtonText = "采用项目档案",
                    SecondaryButtonText = "保留数据库",
                    CloseButtonText = "稍后处理",
                    DefaultButton = ContentDialogButton.Close
                };
                var choice = await dialog.ShowAsync();
                if (choice == ContentDialogResult.None) continue;
                if (choice == ContentDialogResult.Primary && applyAllBox?.IsChecked == true)
                {
                    applyAllMetadata = true;
                }

                await _scanner.ResolveConflictAsync(conflict,
                    choice == ContentDialogResult.Primary ? ScanConflictResolution.UseMetadata : ScanConflictResolution.KeepDatabase);
                resolved++;
            }

            // 版本记录 ↔ 版本目录不一致：扫描只报告，这里逐条让用户确认后才处理。
            var resolvedVersions = 0;
            var mutedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 104, 119, 146));
            foreach (var issue in result.VersionIssues)
            {
                var missingFolder = issue.Kind == ScanVersionIssueKind.MissingFolder;
                var panel = new StackPanel { Width = 560, Spacing = 8 };
                panel.Children.Add(new TextBlock
                {
                    Text = $"项目：{issue.ProjectName}（{issue.ProjectCode}）\n版本：{issue.VersionNumber}　{issue.Title}",
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(new TextBlock
                {
                    Text = issue.DirectoryPath,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = mutedBrush
                });
                panel.Children.Add(new TextBlock
                {
                    Text = issue.Detail,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = mutedBrush
                });

                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = missingFolder ? "扫描发现版本目录缺失" : "扫描发现未记录的版本目录",
                    Content = panel,
                    PrimaryButtonText = missingFolder ? "清理版本记录" : "补建版本记录",
                    IsPrimaryButtonEnabled = issue.CanResolve,
                    CloseButtonText = "稍后处理",
                    DefaultButton = ContentDialogButton.Close
                };
                var choice = await dialog.ShowAsync();
                if (choice != ContentDialogResult.Primary) continue;
                await _scanner.ResolveVersionIssueAsync(issue);
                resolvedVersions++;
            }

            await ViewModel.RefreshAsync();
            var message = $"扫描 {result.ScannedFiles} 个档案，导入 {result.ImportedProjects} 个项目，发现 {result.Conflicts.Count} 个冲突，{result.VersionIssues.Count} 个版本记录问题，{result.Errors.Count} 个错误。";
            if (resolved > 0) message += $" 已处理 {resolved} 个冲突。";
            if (applyAllMetadata) message += "（其余冲突按“全部采用项目档案”批量处理）";
            if (resolvedVersions > 0) message += $" 已处理 {resolvedVersions} 个版本记录问题。";
            if (result.Conflicts.Count > resolved || result.VersionIssues.Count > resolvedVersions || result.Errors.Count > 0) ShowWarning("扫描完成，需要检查", message);
            else ShowSuccess("扫描完成", message);
        }
        catch (Exception ex) { ShowError("扫描失败", ex); }
    }

    private async void MigrationRecovery_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var transfers = await ViewModel.GetIncompleteTransfersAsync();
            if (transfers.Count == 0)
            {
                ShowSuccess("迁移记录正常", "没有待恢复或待清理的迁移。");
                return;
            }

            foreach (var transfer in transfers)
            {
                var canAbandon = transfer.State is not TransferState.Switched and not TransferState.CleanupPending;
                var actionText = transfer.State == TransferState.CleanupPending ? "知道了" :
                    transfer.State == TransferState.Switched ? "完成切换" : "继续或重试";
                var message = new TextBlock
                {
                    Width = 560,
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"状态：{transfer.State}\n源：{transfer.SourcePath}\n目标：{transfer.TargetPath}\n{transfer.Error ?? string.Empty}\n\n{(transfer.State == TransferState.CleanupPending ? "目标已经成为主副本。首发版不自动永久删除源目录；请停止求解器并人工核对目标后，在文件系统中备份或清理旧副本。" : "重试会先清理本次未完成的暂存目录，再从源目录重新复制；若正式目标已生成，会先验证源和目标内容再完成数据库切换。")}"
                };
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "迁移恢复",
                    Content = message,
                    PrimaryButtonText = actionText,
                    SecondaryButtonText = canAbandon ? "放弃暂存副本" : string.Empty,
                    CloseButtonText = "稍后处理",
                    DefaultButton = ContentDialogButton.Close
                };
                var choice = await dialog.ShowAsync();
                if (choice == ContentDialogResult.None) continue;
                if (choice == ContentDialogResult.Secondary)
                {
                    await ViewModel.AbandonTransferAsync(transfer);
                    continue;
                }
                if (transfer.State == TransferState.CleanupPending)
                {
                    ShowWarning("源副本保持不变", "SimFlow 没有执行删除。请在确认求解器已停止且目标副本完整后，通过文件系统手工备份或清理源目录。");
                    continue;
                }

                await RetryTransferWithProgressAsync(transfer);
            }

            await ViewModel.RefreshAsync();
            ShowSuccess("迁移恢复已处理", "未完成记录已按你的选择更新。");
        }
        catch (OperationCanceledException)
        {
            ShowWarning("迁移恢复已取消", "源目录保持不变，未完成记录仍可稍后处理。");
        }
        catch (Exception ex)
        {
            ShowError("迁移恢复失败", ex);
        }
    }

    private async Task RetryTransferWithProgressAsync(TransferOperationRecord transfer)
    {
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6 };
        var stage = new TextBlock { Text = "准备恢复...", TextWrapping = TextWrapping.Wrap };
        var current = new TextBlock { Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 104, 119, 146)), TextTrimming = TextTrimming.CharacterEllipsis };
        var panel = new StackPanel { Width = 480, Spacing = 12 };
        panel.Children.Add(stage);
        panel.Children.Add(progressBar);
        panel.Children.Add(current);
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "正在恢复迁移", Content = panel, CloseButtonText = "取消" };
        using var cancellation = new CancellationTokenSource();
        dialog.CloseButtonClick += (_, _) => cancellation.Cancel();
        var progress = new Progress<MigrationProgress>(value =>
        {
            progressBar.Value = value.Percent;
            stage.Text = value.Message;
            current.Text = value.CurrentFile ?? string.Empty;
        });
        var showTask = dialog.ShowAsync().AsTask();
        try
        {
            await ViewModel.RetryTransferAsync(transfer, progress, cancellation.Token);
        }
        finally
        {
            dialog.Hide();
            await showTask;
        }
    }

    private async Task SeedDemoDataAsync()
    {
        if (ViewModel.TotalProjectCount > 0) return;
        var demos = new[]
        {
            ("150kA短耐动稳定性分析", "分析150kA短耐电流下触头系统的动态稳定性，评估触头位移、反力及整体机构的动态响应。", "张三", new[]{"短耐","动稳定性","触头"}, new[]{"ANSYS Maxwell","Adams"}, WorkflowStatus.Active, true, SimulationType.Electromagnetics),
            ("抽屉座摇进机构优化", "降低摇进阻力并验证机构寿命。", "李工", new[]{"机构","优化"}, new[]{"Adams"}, WorkflowStatus.Waiting, false, SimulationType.Dynamics),
            ("操动机构疲劳分析", "识别疲劳薄弱位置并验证目标寿命。", "王工", new[]{"疲劳","机构"}, new[]{"Adams","nCode"}, WorkflowStatus.Completed, true, SimulationType.Fatigue),
            ("触头温升分析", "分析额定工况下触头系统温升。", "赵工", new[]{"温升","触头"}, new[]{"ANSYS Maxwell"}, WorkflowStatus.Completed, false, SimulationType.ThermalFluid),
            ("分闸弹簧优化", "优化弹簧参数和机构分闸速度。", "陈工", new[]{"弹簧","分闸"}, new[]{"ANSYS Mechanical"}, WorkflowStatus.Active, true, SimulationType.Structural),
            ("连杆强度校核", "校核传动连杆极限工况强度。", "周工", new[]{"强度","连杆"}, new[]{"ANSYS Mechanical"}, WorkflowStatus.Completed, false, SimulationType.Structural),
            ("导电桥电磁力分析", "评估短路电流下导电桥电磁力。", "刘工", new[]{"电磁力","短路"}, new[]{"ANSYS Maxwell"}, WorkflowStatus.Waiting, true, SimulationType.Multiphysics)
        };
        foreach (var demo in demos)
        {
            var project = await ViewModel.CreateProjectAsync(new CreateProjectRequest { Name = demo.Item1, SimulationType = demo.Item8, Description = demo.Item2, Requester = demo.Item3, Tags = demo.Item4, Software = demo.Item5, StartImmediately = true, IsFavorite = demo.Item7 });
            if (demo.Item6 == WorkflowStatus.Waiting) await ViewModel.ChangeStatusAsync(WorkflowStatus.Waiting, "等待试验", "演示数据");
            else if (demo.Item6 == WorkflowStatus.Completed) await ViewModel.ChangeStatusAsync(WorkflowStatus.Completed);
        }
        ViewModel.SetFilter("home");
    }

    /// <summary>
    /// 软件列表以配置为准；配置里没有可用项时回退到 V0.1 的默认软件集合。
    /// </summary>
    private IReadOnlyList<string> SoftwareNames()
    {
        var names = _configuration.Current.Software
            .Select(item => item.Name)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        return names.Count > 0
            ? names
            : ["Adams", "ANSYS Mechanical", "ANSYS Maxwell", "nCode", "SolidWorks", "SpaceClaim", "COMSOL"];
    }

    private bool _contentDialogOpen;

    /// <summary>
    /// 打开内容对话框：同一时刻只允许一个 ContentDialog，阻止重入导致的“背景仍被遮罩/第二次点击无反应”。
    /// 调用方如遇 null（已有对话框在显示）应直接返回。
    /// </summary>
    private async Task<ContentDialogResult?> ShowDialogAsync(ContentDialog dialog)
    {
        if (_contentDialogOpen)
        {
            return null;
        }

        _contentDialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _contentDialogOpen = false;
        }
    }

    private ContentDialog CreateDialog(string title, object content, string primary, string close = "取消") => new()
    {
        XamlRoot = RootGrid.XamlRoot,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary
    };

    private Task<ContentDialogResult?> ConfirmAsync(string title, string message, string primary, string close = "取消")
        => ShowDialogAsync(CreateDialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 }, primary, close));

    private void ShowSuccess(string title, string message) => ShowInfo(title, message, InfoBarSeverity.Success);
    private void ShowWarning(string title, string message) => ShowInfo(title, message, InfoBarSeverity.Warning);
    private void ShowError(string title, Exception ex)
    {
        _logger.LogError(ex, "{Title}", title);
        ShowInfo(title, ex.Message, InfoBarSeverity.Error);
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
        // 成功 5 秒、警告 9 秒、错误 14 秒后自动消失；用户也可以手动关闭。
        _infoBarTimer.Interval = TimeSpan.FromSeconds(severity switch
        {
            InfoBarSeverity.Error => 14,
            InfoBarSeverity.Warning => 9,
            _ => 5
        });
        _infoBarTimer.Start();
    }

    /// <summary>
    /// 按卡片宽度算出封面高度，保证固定宽高比。赋值高度会再次触发 SizeChanged，
    /// 所以必须先比较再赋值，否则会自激。
    /// </summary>
    private void KanbanCover_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement cover || e.NewSize.Width <= 0)
        {
            return;
        }

        var target = Math.Min(Math.Round(e.NewSize.Width / KanbanCoverAspectRatio), KanbanCoverMaxHeight);
        if (target > 0 && (double.IsNaN(cover.Height) || Math.Abs(cover.Height - target) > 0.5))
        {
            cover.Height = target;
        }
    }

    /// <summary>
    /// 按住右上角握把：捕获指针、预渲染浮动副本，状态推到 Armed。
    /// 整个看板拖动都是自绘的 —— 不使用 CanDrag / DragStarting / DragOver / Drop，
    /// 被拖对象的位置完全由 PointerMoved 驱动（见 KanbanDragState）。
    /// </summary>
    private async void KanbanHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement handle)
        {
            return;
        }

        // 离线之外不可拖动的卡片不给反馈，避免"看起来能拖但拖不动"。
        var card = handle.DataContext as ProjectCardViewModel;
        if (card?.Project.StorageStatus != StorageStatus.Working || !card.IsLocationAvailable)
        {
            return;
        }

        var container = FindDragVisualContainer(handle);
        if (container is null)
        {
            return;
        }

        // 上一次若因异常路径没收拾干净，先收干净再开始新的。
        ResetKanbanDrag();

        var token = ++_dragToken;
        _draggedProject = card;
        _dragHandle = handle;
        _dragOriginInRoot = e.GetCurrentPoint(RootGrid).Position;
        _pointerInRoot = _dragOriginInRoot;
        _dragState = KanbanDragState.Armed;

        // 捕获指针：此后指针离开握把、离开卡片，PointerMoved / PointerReleased 仍会送到这里
        //（并冒泡到卡片根 Grid 上的处理器）。
        handle.CapturePointer(e.Pointer);

        // 预渲染浮动副本。此刻卡片没有任何变换，抓到的必定是原始尺寸，换算因此是确定的。
        var preview = await CaptureCardPreviewAsync(handle, container);
        if (token != _dragToken || preview is null)
        {
            // 抓图期间已经松手、已经开始了新的按压，或抓图失败：不留任何状态。
            return;
        }

        _cardGhostStream?.Dispose(); // 新图接替旧图时才释放上一个流（上一次拖动早已结束）
        _cardGhostStream = preview.Value.Stream;
        _cardGhostImage = preview.Value.Image;
        _cardGhostAnchor = ResolveCardGhostAnchor(handle, container);

        if (ShowCardGhost())
        {
            // 原卡片变淡，视线交给浮动副本；按住不动也看得到副本，这是用户要求的按压反馈。
            card.IsDragging = true;
        }
    }

    /// <summary>
    /// 指针移动：先判断是否越过拖动阈值（越过才进入拖动、目标列才参与判定与高亮），
    /// 之后每次只做三件事 —— 移动浮动副本、判断指针落在哪一列、目标列变化时更新高亮。
    /// 没有 DataPackage、没有 DragUI、没有 DragUIOverride、没有 DragOver/Drop。
    /// </summary>
    private void KanbanCard_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragState == KanbanDragState.Idle)
        {
            return;
        }

        _pointerInRoot = e.GetCurrentPoint(RootGrid).Position;

        if (_dragState == KanbanDragState.Armed)
        {
            var dx = _pointerInRoot.X - _dragOriginInRoot.X;
            var dy = _pointerInRoot.Y - _dragOriginInRoot.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < KanbanDragThreshold)
            {
                return;
            }

            _dragState = KanbanDragState.Dragging;
            RefreshColumnAreas();
            _logger.LogInformation("看板拖动开始：{Code}", _draggedProject?.ProjectCode);
        }

        if (_cardGhostVisible)
        {
            PositionCardGhost();
        }

        if (_dragState == KanbanDragState.Dragging)
        {
            UpdateColumnHighlight();
        }
    }

    private void KanbanCard_PointerReleased(object sender, PointerRoutedEventArgs e) => FinishKanbanDrag(commit: true);

    private void KanbanCard_PointerCanceled(object sender, PointerRoutedEventArgs e) => FinishKanbanDrag(commit: false);

    private void KanbanCard_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => FinishKanbanDrag(commit: false);

    /// <summary>
    /// 结束拖动。**先彻底收掉视觉状态、把状态机置回 Idle，再去做业务操作** ——
    /// 业务里会 await 对话框（进等待要填原因、进完成要确认），绝不能把指针捕获与半透明状态带进对话框。
    /// </summary>
    private async void FinishKanbanDrag(bool commit)
    {
        if (_dragState == KanbanDragState.Idle)
        {
            return;
        }

        var card = _draggedProject;
        var targetStatus = _dragState == KanbanDragState.Dragging ? ResolveColumnAt(_pointerInRoot)?.Status : null;

        // 1) 先结束视觉拖动，且必须在任何 await 之前完成。
        _dragHandle?.ReleasePointerCaptures();

        ResetKanbanDrag();
        UpdateColumnHighlight();
        if (card is not null)
        {
            card.IsDragging = false;
        }

        // 2) 再做业务操作。
        if (!commit || card is null || targetStatus is null)
        {
            return;
        }

        if (!await EnsureProjectStorageOnlineAsync(card.Project, "修改状态")) return;

        var from = card.Project.WorkflowStatus;
        if (targetStatus.Value == from)
        {
            return; // 放回原列，什么都不做
        }

        if (!WorkflowRules.CanTransition(from, targetStatus.Value))
        {
            ShowWarning("无法移动", $"不允许从「{WorkflowRules.Describe(from)}」直接移动到「{WorkflowRules.Describe(targetStatus.Value)}」。");
            return;
        }

        _logger.LogInformation("看板拖动放下：{Code} → {Target}", card.ProjectCode, WorkflowRules.Describe(targetStatus.Value));
        try
        {
            if (targetStatus.Value == WorkflowStatus.Waiting)
            {
                var (reason, note) = await AskWaitReasonAsync();
                if (reason is null)
                {
                    return; // 用户取消
                }

                await ViewModel.ChangeStatusAsync(card.Project, WorkflowStatus.Waiting, reason, note);
            }
            else if (targetStatus.Value == WorkflowStatus.Completed)
            {
                if (await ConfirmAsync("完成项目", $"把「{card.Name}」移动到已完成？之后仍可重新调整状态。", "完成") != ContentDialogResult.Primary)
                {
                    return;
                }

                await ViewModel.ChangeStatusAsync(card.Project, WorkflowStatus.Completed);
            }
            else
            {
                await ViewModel.ChangeStatusAsync(card.Project, targetStatus.Value);
            }

            // 刷新后按编号找回同一项目，让详情面板保持连贯。
            ViewModel.SelectedProject = ViewModel.Projects.FirstOrDefault(item => item.Project.ProjectCode == card.ProjectCode);
            ShowSuccess("状态已更新", ViewModel.StatusMessage);
        }
        catch (Exception ex)
        {
            ShowError("状态更新失败", ex);
        }
    }

    /// <summary>把拖动状态机与视觉状态一起清干净。</summary>
    private void ResetKanbanDrag()
    {
        _dragState = KanbanDragState.Idle;
        _dragHandle = null;
        _draggedProject = null;
        _cardGhostImage = null;
        HideCardGhost();
        // 流不在这里释放：位图可能仍被引用，交给下一次抓图替换时释放。
    }

    /// <summary>缓存四列的命中区域（RootGrid 坐标）。只在越过阈值进入拖动时刷新一次 —— 拖动期间布局不变。</summary>
    private void RefreshColumnAreas()
    {
        _columnAreas =
        [
            BuildColumnArea(NotStartedColumn, WorkflowStatus.NotStarted),
            BuildColumnArea(ActiveColumn, WorkflowStatus.Active),
            BuildColumnArea(WaitingColumn, WorkflowStatus.Waiting),
            BuildColumnArea(CompletedColumn, WorkflowStatus.Completed)
        ];
    }

    private KanbanColumnArea BuildColumnArea(Border column, WorkflowStatus status)
    {
        var origin = column.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0));
        return new KanbanColumnArea(
            column, status, new Rect(origin.X, origin.Y, column.ActualWidth, column.ActualHeight));
    }

    /// <summary>
    /// 判断指针落在哪一列。列与列之间有 ColumnSpacing 的空隙，把空隙对半分给左右两列 ——
    /// 这样横向拖动时不会出现"两边都不属于"的死区，手感更接近常见看板工具。
    /// </summary>
    private KanbanColumnArea? ResolveColumnAt(Point point)
    {
        var areas = _columnAreas;
        if (areas is null)
        {
            return null;
        }

        var halfGap = KanbanBoard.ColumnSpacing / 2;
        foreach (var area in areas)
        {
            var bounds = area.Bounds;
            if (point.Y < bounds.Y || point.Y > bounds.Y + bounds.Height)
            {
                continue;
            }

            if (point.X >= bounds.X - halfGap && point.X <= bounds.X + bounds.Width + halfGap)
            {
                return area;
            }
        }

        return null;
    }

    /// <summary>
    /// 高亮当前目标列。只换边框颜色、不换粗细 —— 粗细变化会推动列内布局，拖动时看起来会抖。
    /// 只有"确实能落进去"的列才高亮：原列与规则不允许的列都不亮。
    /// </summary>
    private void UpdateColumnHighlight()
    {
        var areas = _columnAreas;
        if (areas is null)
        {
            return;
        }

        var card = _draggedProject;
        var target = _dragState == KanbanDragState.Dragging && card is not null
            ? ResolveColumnAt(_pointerInRoot)
            : null;

        foreach (var area in areas)
        {
            var isTarget = target is not null
                && ReferenceEquals(area.Column, target.Column)
                && area.Status != card!.Project.WorkflowStatus
                && WorkflowRules.CanTransition(card.Project.WorkflowStatus, area.Status);

            area.Column.BorderBrush = isTarget ? KanbanTargetBorderBrush : null;
        }
    }

    /// <summary>显示浮动副本并摆到指针下方；没有预渲染图时什么都不做。</summary>
    private bool ShowCardGhost()
    {
        if (_cardGhostImage is null)
        {
            return false;
        }

        CardGhostImage.Source = _cardGhostImage;
        CardGhostLayer.Visibility = Visibility.Visible;
        _cardGhostVisible = true;
        PositionCardGhost();
        return true;
    }

    private void HideCardGhost()
    {
        _cardGhostVisible = false;
        CardGhostLayer.Visibility = Visibility.Collapsed;
        CardGhostImage.Source = null;
    }

    /// <summary>
    /// 把浮动副本摆到指针下方：让图里的握把位置正好压在指针上。
    /// 用 RenderTransform 位移而不是 Canvas.Left/Top —— 前者不触发布局，每帧成本更低。
    /// </summary>
    private void PositionCardGhost()
    {
        if (CardGhostImage.RenderTransform is not TranslateTransform transform)
        {
            return;
        }

        transform.X = _pointerInRoot.X - _cardGhostAnchor.X;
        transform.Y = _pointerInRoot.Y - _cardGhostAnchor.Y;
    }

    /// <summary>
    /// 把卡片渲染成放大到 <see cref="CardGhostScale"/> 的位图。整个拖动过程只有这一个视觉对象。
    /// 显示尺寸 = 像素数 ÷ (DPI/96)，按"想要的显示尺寸"反推 DPI 就能精确控制大小，
    /// 这个写法同时覆盖屏幕缩放与放大比例。返回的流由调用方持有。
    /// </summary>
    private async Task<(BitmapImage Image, InMemoryRandomAccessStream Stream)?> CaptureCardPreviewAsync(
        FrameworkElement handle, FrameworkElement container)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(container);
            var pixels = WindowsRuntimeBufferExtensions.ToArray(await bitmap.GetPixelsAsync());
            var targetLogicalWidth = Math.Max(1, container.ActualWidth * CardGhostScale);
            var dpi = 96 * bitmap.PixelWidth / targetLogicalWidth;
            var (image, stream) = await CreatePreviewImageAsync(
                bitmap.PixelWidth, bitmap.PixelHeight, dpi, pixels);
            _logger.LogInformation(
                "浮动副本就绪：像素 {Width}x{Height}，目标宽 {Target:0.#}，DPI {Dpi:0.#}，容器宽 {Container:0.#}",
                bitmap.PixelWidth, bitmap.PixelHeight, targetLogicalWidth, dpi, container.ActualWidth);
            return (image, stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "浮动副本抓图失败，本次拖动将没有跟随视觉");
            return null;
        }
    }

    /// <summary>把同一份像素编码成一个独立的流与位图。流不在这里释放：位图之后仍会从流里取数据。</summary>
    private static async Task<(BitmapImage Image, InMemoryRandomAccessStream Stream)> CreatePreviewImageAsync(
        int pixelWidth, int pixelHeight, double dpi, byte[] pixels)
    {
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)pixelWidth,
            (uint)pixelHeight,
            dpi,
            dpi,
            pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return (image, stream);
    }

    /// <summary>握把在卡片图里的位置（按放大比例换算过），用作预览锚点。</summary>
    private static Point ResolveCardGhostAnchor(FrameworkElement handle, FrameworkElement container)
    {
        var gripCenter = handle.TransformToVisual(container).TransformPoint(
            new Point(handle.ActualWidth / 2, handle.ActualHeight / 2));
        return new Point(gripCenter.X * CardGhostScale, gripCenter.Y * CardGhostScale);
    }

    private static FrameworkElement? FindDragVisualContainer(FrameworkElement origin)
    {
        var current = origin;
        while (current is not null)
        {
            if (current is ListViewItem)
            {
                return current;
            }

            current = VisualTreeHelper.GetParent(current) as FrameworkElement;
        }

        return null;
    }

    private async Task<(string? Reason, string? Note)> AskWaitReasonAsync()
    {
        var reason = new ComboBox { Header = "等待原因 *", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var item in WaitReasons) reason.Items.Add(item);
        reason.SelectedIndex = 0;
        var note = new TextBox { Header = "等待说明", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76 };
        var panel = new StackPanel { Width = 500, Spacing = 14 };
        panel.Children.Add(reason);
        panel.Children.Add(note);

        if (await ShowDialogAsync(CreateDialog("设为等待", panel, "确认等待")) != ContentDialogResult.Primary)
        {
            return (null, null);
        }

        var value = reason.SelectedItem?.ToString();
        return string.IsNullOrWhiteSpace(value) ? (null, null) : (value, note.Text.Trim());
    }

    /// <summary>
    /// 首页统计卡片点击：跳到对应筛选页。状态/归档/收藏不再是侧边栏一级导航，
    /// 因此统一落到「所有项目」并应用对应筛选（筛选能力与之前完全一致）。
    /// </summary>
    private void StatCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StatCardViewModel stat })
        {
            NavigateToFilter(stat.Key);
        }
    }

    /// <summary>
    /// 在每次状态操作前重新探测项目所在根目录。卡片上的 IsLocationAvailable 是上次刷新结果，
    /// 这里只把实时结果作为最终门禁，防止 SMB 在用户操作前刚好断开。
    /// </summary>
    private async Task<bool> EnsureProjectStorageOnlineAsync(ProjectRecord project, string action)
    {
        if (await _storage.IsOnlineAsync(project.StorageLocation)) return true;

        ShowWarning("存储位置不可用", $"项目“{project.Name}”的存储位置离线或未配置，暂时不能{action}。请恢复连接后重试。");
        return false;
    }

    private void NavigateToFilter(string key)
    {
        var listFilter = PageFilterKeys.Contains(key, StringComparer.Ordinal);
        var navKey = listFilter ? "all" : key;
        var applied = listFilter ? key : navKey;

        var item = MainNavigation.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, navKey, StringComparison.Ordinal));
        if (item is null)
        {
            _pendingListFilter = null;
            ViewModel.SetFilter(applied);
            SyncFilterChips();
            return;
        }

        if (!ReferenceEquals(MainNavigation.SelectedItem, item))
        {
            // 选中导航项会触发 SelectionChanged，由它消费 _pendingListFilter 落到具体筛选；
            // 该事件同步或异步触发都成立（异步时 pending 仍然留在字段里）。
            _pendingListFilter = listFilter ? key : null;
            MainNavigation.SelectedItem = item;
        }

        // 已经在目标导航项上时不会触发 SelectionChanged，这里显式应用一次；重复应用是幂等的。
        ViewModel.SetFilter(applied);
        SyncFilterChips();
        ClearAllSelections();
    }

    private async void TagRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagSummaryViewModel tag }) return;
        var input = new TextBox { Header = "新名称", Text = tag.Name, PlaceholderText = "重命名会同步到所有使用它的项目" };
        if (await ShowDialogAsync(CreateDialog("重命名标签", input, "重命名")) != ContentDialogResult.Primary) return;
        var name = input.Text.Trim();
        if (name.Length == 0) return;
        try
        {
            await ViewModel.RenameTagAsync(tag.Name, name);
            ShowSuccess("标签已重命名", string.Empty);
        }
        catch (Exception ex)
        {
            ShowError("重命名标签失败", ex);
        }
    }

    private async void TagMerge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagSummaryViewModel source }) return;
        var targets = ViewModel.Tags
            .Where(candidate => !string.Equals(candidate.Name, source.Name, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Name)
            .ToList();
        if (targets.Count == 0)
        {
            ShowWarning("没有可合并的目标", "当前只有这一个标签。");
            return;
        }

        var choices = new ComboBox { Header = "合并到", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var name in targets) choices.Items.Add(new ComboBoxItem { Content = name });
        choices.SelectedIndex = 0;
        var panel = new StackPanel { Width = 420, Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = $"把「{source.Name}」下的项目并入目标标签，并删除「{source.Name}」。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(choices);
        if (await ShowDialogAsync(CreateDialog("合并标签", panel, "合并")) != ContentDialogResult.Primary) return;
        if (choices.SelectedItem is not ComboBoxItem selected || selected.Content is not string target) return;
        try
        {
            await ViewModel.MergeTagsAsync(source.Name, target);
            ShowSuccess("标签已合并", string.Empty);
        }
        catch (Exception ex)
        {
            ShowError("合并标签失败", ex);
        }
    }

    private async void TagDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagSummaryViewModel tag }) return;
        if (tag.Count > 0)
        {
            ShowWarning("无法删除", $"标签「{tag.Name}」仍被 {tag.Count} 个项目使用。可先重命名或合并。");
            return;
        }

        if (await ConfirmAsync("删除标签", $"确定删除标签「{tag.Name}」？", "删除") != ContentDialogResult.Primary) return;
        try
        {
            await ViewModel.DeleteUnusedTagAsync(tag.Name);
            ShowSuccess("标签已删除", string.Empty);
        }
        catch (Exception ex)
        {
            ShowError("删除标签失败", ex);
        }
    }

    private async void NewSoftware_Click(object sender, RoutedEventArgs e)
    {
        await EditSoftwareDialogAsync(null);
    }

    private async void SoftwareEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SoftwareConfiguration item })
        {
            await EditSoftwareDialogAsync(item);
        }
    }

    private async Task EditSoftwareDialogAsync(SoftwareConfiguration? item)
    {
        var name = new TextBox { Header = "显示名称 *", Text = item?.Name ?? string.Empty, PlaceholderText = "例如：ANSYS Mechanical" };
        var panel = new StackPanel { Width = 480, Spacing = 10 };
        panel.Children.Add(name);
        var dialog = CreateDialog(item is null ? "添加软件标签" : "编辑软件标签", panel, item is null ? "创建" : "保存");
        dialog.PrimaryButtonClick += (_, args2) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                args2.Cancel = true;
                name.Focus(FocusState.Programmatic);
            }
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            if (item is null)
            {
                await ViewModel.AddSoftwareAsync(name.Text);
                ShowSuccess("软件已添加", string.Empty);
            }
            else
            {
                await ViewModel.UpdateSoftwareAsync(item, name.Text);
                ShowSuccess("软件已保存", string.Empty);
            }
        }
        catch (Exception ex)
        {
            ShowError("保存软件失败", ex);
        }
    }

    private async void SoftwareDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SoftwareConfiguration item }) return;
        if (await ConfirmAsync("移除软件候选", $"从候选列表移除「{item.Name}」？已有项目的软件标签和统计记录会保留。", "移除") != ContentDialogResult.Primary) return;
        try
        {
            await ViewModel.RemoveSoftwareAsync(item);
            ShowSuccess("软件已删除", string.Empty);
        }
        catch (Exception ex)
        {
            ShowError("删除软件失败", ex);
        }
    }
}
