using SimFlow.Models;

namespace SimFlow.Services;

/// <summary>从用户配置的模板创建项目级正式报告，目标固定为 Delivery 目录。</summary>
public sealed class SimulationReportService(
    IConfigurationService configuration,
    IStorageLocationService storage) : ISimulationReportService
{
    private static readonly HashSet<string> ReportExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".odt"
    };

    public async Task<PreparedSimulationReport> PrepareAsync(
        ProjectRecord project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var projectDirectory = storage.ResolveProjectPath(project);
        if (!Directory.Exists(projectDirectory))
        {
            throw new DirectoryNotFoundException($"项目目录不可用：{projectDirectory}");
        }

        var deliveryDirectory = Path.Combine(projectDirectory, "Delivery");
        Directory.CreateDirectory(deliveryDirectory);
        var existingReports = Directory.EnumerateFiles(deliveryDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ReportExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (existingReports.Count == 1)
        {
            return new PreparedSimulationReport(existingReports[0], false);
        }

        if (existingReports.Count > 1)
        {
            throw new InvalidOperationException(
                $"Delivery 中发现多个 Word 报告，无法判断要打开哪一个：{string.Join("、", existingReports.Select(Path.GetFileName))}。请只保留一个报告后重试。");
        }

        var templatePath = configuration.Current.SimulationReportTemplatePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(templatePath))
        {
            throw new InvalidOperationException("Delivery 中尚无仿真报告，且未配置报告模板。请先到“设置 → 常规”选择模板文件。");
        }

        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("Delivery 中尚无仿真报告，且配置的模板不存在。请在设置中重新选择。", templatePath);
        }

        var fileName = Path.GetFileName(templatePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("仿真报告模板文件名无效，请在设置中重新选择。");
        }

        var destinationPath = Path.Combine(deliveryDirectory, fileName);
        if (File.Exists(destinationPath))
        {
            return new PreparedSimulationReport(destinationPath, false);
        }

        var temporaryPath = Path.Combine(deliveryDirectory, $".{fileName}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var source = new FileStream(templatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            try
            {
                File.Move(temporaryPath, destinationPath, false);
                return new PreparedSimulationReport(destinationPath, true);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                return new PreparedSimulationReport(destinationPath, false);
            }
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
