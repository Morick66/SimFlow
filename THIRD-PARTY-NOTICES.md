# Third-Party Notices

SimFlow 使用通过 NuGet 分发的第三方组件。每个组件仍受其自身许可证约束；完整许可证文本以锁定版本的 NuGet 包内容和项目主页为准。

主要直接依赖：

| Package | Version |
| --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 |
| Microsoft.Data.Sqlite | 10.0.12 |
| Microsoft.Extensions.DependencyInjection | 10.0.12 |
| Microsoft.Extensions.Logging | 10.0.12 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 |
| Microsoft.WindowsAppSDK | 2.4.0 |
| Serilog.Extensions.Logging | 10.0.0 |
| Serilog.Sinks.File | 7.0.0 |
| Microsoft.NET.Test.Sdk | 18.10.1 |
| xunit | 2.9.3 |
| xunit.runner.visualstudio | 3.1.5 |

传递依赖及精确解析版本记录在各项目的 `packages.lock.json`。发布前应从 NuGet 全局包缓存导出这些固定版本的许可证文件，与二进制分发包一起归档。

Microsoft、Windows、WinUI、Visual Studio、Adams、ANSYS、Maxwell、nCode、SolidWorks、COMSOL 及其他名称和标志是其各自权利人的商标。SimFlow 与这些厂商不存在隶属或背书关系。
