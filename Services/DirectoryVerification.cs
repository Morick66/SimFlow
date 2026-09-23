using System.Security.Cryptography;

namespace SimFlow.Services;

/// <summary>复制和删除源副本前的保守检查；权限错误和链接均拒绝，不能把未枚举到的文件视为不存在。</summary>
public static class DirectoryVerification
{
    public static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    public static void EnsureSeparate(string source, string destination)
    {
        var left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (PathsEqual(left, right)
            || (right + Path.DirectorySeparatorChar).StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || (left + Path.DirectorySeparatorChar).StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("源目录与目标目录不能相同或相互包含。");
        EnsureNoLinkedParents(left);
        EnsureNoLinkedParents(right);
    }

    private static void EnsureNoLinkedParents(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"不支持连接或符号链接：{directory.FullName}");
        }
    }

    public static Dictionary<string, bool> Inventory(string root)
    {
        EnsureNoLinkedParents(root);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"不支持连接或符号链接：{entry.FullName}");
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                result.Add(Path.GetRelativePath(root, entry.FullName), isDirectory);
                if (isDirectory) pending.Push(new DirectoryInfo(entry.FullName));
            }
        }
        return result;
    }

    public static async Task VerifyAsync(string source, string destination, bool exact = true,
        string? excludedFile = null, CancellationToken cancellationToken = default)
    {
        EnsureSeparate(source, destination);
        var entries = Inventory(source);
        var targetEntries = Inventory(destination);
        if (exact && entries.Count != targetEntries.Count)
            throw new IOException("目标目录内容与源目录不一致，已保留原文件。");
        var verifiedSourceFiles = new Dictionary<string, (long Length, byte[] Hash)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targetEntries.TryGetValue(entry.Key, out var directory) || directory != entry.Value)
                throw new IOException($"目标缺少文件或目录：{entry.Key}");
            if (directory || string.Equals(entry.Key, excludedFile, StringComparison.OrdinalIgnoreCase)) continue;
            await using var input = new FileStream(Path.Combine(source, entry.Key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await using var output = new FileStream(Path.Combine(destination, entry.Key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (input.Length != output.Length)
                throw new IOException($"源与目标文件长度不同：{entry.Key}，已保留源目录。");
            var sourceHash = await SHA256.HashDataAsync(input, cancellationToken);
            var targetHash = await SHA256.HashDataAsync(output, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
                throw new IOException($"源与目标文件内容不同：{entry.Key}，已保留源目录。");
            verifiedSourceFiles[entry.Key] = (input.Length, sourceHash);
        }
        // 捕获校验期间新建/删除的条目，并重新哈希已校验文件。
        // 仅重查文件名和类型会漏掉“原位覆盖”：文件可在首次哈希后保持同名同长度却改变内容。
        var latest = Inventory(source);
        if (latest.Count != entries.Count || entries.Any(item => !latest.TryGetValue(item.Key, out var kind) || kind != item.Value))
            throw new IOException("校验过程中源目录发生变化，请停止写入后重试。");
        foreach (var entry in verifiedSourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(source, entry.Key);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (input.Length != entry.Value.Length)
                throw new IOException($"校验过程中源文件发生变化：{entry.Key}，已保留源目录。");
            var latestHash = await SHA256.HashDataAsync(input, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(latestHash, entry.Value.Hash))
                throw new IOException($"校验过程中源文件发生变化：{entry.Key}，已保留源目录。");
        }
    }
}
