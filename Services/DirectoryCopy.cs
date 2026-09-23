namespace SimFlow.Services;

/// <summary>
/// 简单的目录复制工具（用于“更换本机 Work 路径时复制旧目录项目”）。
/// 拒绝目录连接点/符号链接；调用方在配置切换前校验哈希。
/// </summary>
public static class DirectoryCopy
{
    public static void Copy(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        var sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var destinationPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase)
            || destinationPath.StartsWith(sourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("目标目录不能与源目录相同，也不能位于源目录内部。");
        }
        if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
        {
            throw new IOException($"目标目录已存在，为避免覆盖现有项目已停止复制：{destinationPath}");
        }

        var stagingPath = destinationPath + $".copy.{Guid.NewGuid():N}.partial";
        DirectoryVerification.EnsureSeparate(sourcePath, destinationPath);
        var entries = DirectoryVerification.Inventory(sourcePath);
        try
        {
            Directory.CreateDirectory(stagingPath);
            // 先按源结构重建目录：文件复制只为父目录建目录，空目录不会被带过去。
            foreach (var directory in entries.Where(item => item.Value))
            {
                var relative = directory.Key;
                Directory.CreateDirectory(Path.Combine(stagingPath, relative));
            }

            foreach (var file in entries.Where(item => !item.Value))
            {
                var relative = file.Key;
                var target = Path.Combine(stagingPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(sourcePath, relative), target, false);
            }

            Directory.Move(stagingPath, destinationPath);
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, true);
            }
        }
    }
}
