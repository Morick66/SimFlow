using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SimFlow.Services;
using SimFlow.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SimFlow
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        public static IServiceProvider Services { get; private set; } = null!;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            var paths = AppPaths.Create(HasPackageIdentity());
            paths.EnsureDirectories();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(Path.Combine(paths.LogsDirectory, "simflow-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
                .CreateLogger();
            Log.Information("SimFlow 运行数据目录：{AppDataDirectory}", paths.AppDataDirectory);
            var services = new ServiceCollection();
            services.AddSingleton(paths);
            services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: true));
            services.AddSingleton<DatabaseService>();
            services.AddSingleton<IConfigurationService, ConfigurationService>();
            services.AddSingleton<IProjectRepository, ProjectRepository>();
            services.AddSingleton<StorageSettingsService>();
            services.AddSingleton<IProjectMetadataStore, ProjectMetadataStore>();
            services.AddSingleton<IStorageLocationService, StorageLocationService>();
            services.AddSingleton<IProjectService, ProjectService>();
            services.AddSingleton<IProjectMigrationService, ProjectMigrationService>();
            services.AddSingleton<ISimulationReportService, SimulationReportService>();
            services.AddSingleton<IProjectScannerService, ProjectScannerService>();
            services.AddSingleton<IStatisticsService, StatisticsService>();
            services.AddSingleton<MainViewModel>();
            // 统计页的视图模型保持单例：来回切换导航时保留用户选中的时间范围。
            services.AddSingleton<StatisticsViewModel>();
            services.AddTransient<MainWindow>();
            Services = services.BuildServiceProvider();
        }

        /// <summary>
        /// 是否以 MSIX 打包方式运行。打包运行时运行数据要放到包外（见 <see cref="AppPaths.Create"/>）：
        /// 包容器内的数据会在卸载时被删除。
        /// </summary>
        private static bool HasPackageIdentity()
        {
            try
            {
                return !string.IsNullOrEmpty(Windows.ApplicationModel.Package.Current.Id.Name);
            }
            catch (Exception)
            {
                // 未打包（从 Visual Studio / 命令行直接运行）时没有包标识。
                return false;
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                await Services.GetRequiredService<IConfigurationService>().InitializeAsync();
                await Services.GetRequiredService<DatabaseService>().InitializeAsync();
                _window = Services.GetRequiredService<MainWindow>();
                _window.Activate();
            }
            catch (Exception ex)
            {
                Services.GetService<ILogger<App>>()?.LogCritical(ex, "SimFlow startup failed.");
                _window = new Window { Title = "SimFlow 启动失败" };
                _window.Content = new Grid
                {
                    Padding = new Thickness(36),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"SimFlow 无法启动\n\n{ex.Message}\n\n请查看日志后重试。",
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 16
                        }
                    }
                };
                _window.Activate();
            }
        }
    }
}
