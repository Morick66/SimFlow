using SimFlow.Models;

namespace SimFlow.Services;

/// <summary>先准备和校验副本，再提交配置。失败时旧配置及源目录保持可用。</summary>
public sealed class StorageSettingsService(IConfigurationService configuration, IProjectRepository repository)
{
    public async Task SaveAsync(string localRoot, string workstationRoot, string archiveRoot, bool copyProjects,
        CancellationToken cancellationToken = default)
    {
        var previous = configuration.Current;
        var changed = string.IsNullOrWhiteSpace(previous.LocalWorkRoot) || string.IsNullOrWhiteSpace(localRoot)
            ? !string.Equals(previous.LocalWorkRoot, localRoot, StringComparison.OrdinalIgnoreCase)
            : !DirectoryVerification.PathsEqual(previous.LocalWorkRoot, localRoot);
        if (changed && !string.IsNullOrWhiteSpace(previous.LocalWorkRoot) && !string.IsNullOrWhiteSpace(localRoot))
            DirectoryVerification.EnsureSeparate(previous.LocalWorkRoot, localRoot);
        if (changed && copyProjects)
        {
            if (string.IsNullOrWhiteSpace(previous.LocalWorkRoot) || string.IsNullOrWhiteSpace(localRoot))
                throw new InvalidOperationException("复制项目需要有效的源路径和目标路径。");
            var candidates = (await repository.GetAllAsync(cancellationToken))
                .Where(project => project.StorageLocation == StorageLocationCode.LocalWork)
                .Select(project => (project.ProjectCode,
                    Source: ResolveChild(previous.LocalWorkRoot, project.RelativePath),
                    Target: ResolveChild(localRoot, project.RelativePath)))
                .ToList();
            // 先检查整批路径和已有副本；冲突不覆盖，缺源目录也不能静默跳过。
            foreach (var item in candidates)
            {
                DirectoryVerification.EnsureSeparate(item.Source, item.Target);
                DirectoryVerification.Inventory(item.Source);
                if (File.Exists(item.Target)) throw new IOException($"目标路径被文件占用：{item.Target}");
                if (Directory.Exists(item.Target))
                    await DirectoryVerification.VerifyAsync(item.Source, item.Target, cancellationToken: cancellationToken);
            }

            var completed = new List<string>();
            var failed = new List<string>();
            foreach (var item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!Directory.Exists(item.Target))
                        await Task.Run(() => DirectoryCopy.Copy(item.Source, item.Target), cancellationToken);
                    await DirectoryVerification.VerifyAsync(item.Source, item.Target, cancellationToken: cancellationToken);
                    completed.Add(item.ProjectCode);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{item.ProjectCode}: {ex.Message}");
                }
            }
            if (failed.Count > 0)
                throw new IOException($"未切换存储路径，旧配置继续生效。已复制并校验：{string.Join("、", completed)}。已生成的副本保留，可重试。\n失败项目：\n{string.Join("\n", failed)}");
            // 在提交前再次校验整批副本，捕获复制后其他程序写入源目录的情况。
            foreach (var item in candidates)
                await DirectoryVerification.VerifyAsync(item.Source, item.Target, cancellationToken: cancellationToken);
        }

        // 不修改 Current；只有配置文件成功写入后，ConfigurationService 才切换 Current。
        var next = new AppConfiguration
        {
            SchemaVersion = previous.SchemaVersion,
            LocalWorkRoot = localRoot,
            WorkstationWorkRoot = workstationRoot,
            WorkstationArchiveRoot = archiveRoot,
            ProjectPlaceholderImage = previous.ProjectPlaceholderImage,
            SimulationReportTemplatePath = previous.SimulationReportTemplatePath,
            CheckWorkstationOnStartup = previous.CheckWorkstationOnStartup,
            Software = previous.Software.Select(item => new SoftwareConfiguration { Name = item.Name }).ToList()
        };
        await configuration.SaveAsync(next, cancellationToken);
    }

    private static string ResolveChild(string root, string relative)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var child = Path.GetFullPath(Path.Combine(root, relative));
        if (!child.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || DirectoryVerification.PathsEqual(child, root))
            throw new IOException($"项目相对路径超出了 Work 目录：{relative}");
        return child;
    }
}
