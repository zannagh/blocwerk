using Blocwerk.Core.Configuration;

namespace Blocwerk.Core.Capture;

/// <summary>Filesystem <see cref="ICaptureFileStore"/> rooted at <c>{WallImage.StoragePath}/captures</c>.</summary>
public sealed class FileSystemCaptureFileStore : ICaptureFileStore
{
    private readonly string root;
    private readonly string tempRoot;

    public FileSystemCaptureFileStore(BlocwerkSettings settings)
    {
        root = Path.GetFullPath(Path.Combine(settings.WallImage.StoragePath, "captures"));
        tempRoot = Path.Combine(root, "tmp");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(tempRoot);
    }

    public async Task<string> SaveAsync(byte[] bytes, string extension, CancellationToken ct)
    {
        var ext = Normalize(extension);
        var temp = Path.Combine(tempRoot, $"{Guid.NewGuid():N}{ext}");
        await File.WriteAllBytesAsync(temp, bytes, ct);
        var name = $"{Guid.NewGuid():N}{ext}";
        File.Move(temp, Path.Combine(root, name), overwrite: true);
        return name;
    }

    public async Task<string> SaveStreamAsync(Stream content, string extension, long maxBytes, CancellationToken ct)
    {
        var ext = Normalize(extension);
        var temp = Path.Combine(tempRoot, $"{Guid.NewGuid():N}{ext}");
        try
        {
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await CopyCappedAsync(content, target, maxBytes, ct);
            }

            var name = $"{Guid.NewGuid():N}{ext}";
            File.Move(temp, Path.Combine(root, name), overwrite: true);
            return name;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public async Task<byte[]?> ReadAsync(string storedName, CancellationToken ct)
    {
        var path = ResolvePhysicalPath(storedName);
        return path is not null && File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public string? ResolvePhysicalPath(string storedName)
    {
        // Only a bare file name directly under the root is valid (no traversal).
        if (string.IsNullOrWhiteSpace(storedName)
            || Path.IsPathRooted(storedName)
            || Path.GetFileName(storedName) != storedName)
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(root, storedName));
    }

    public void Delete(string? storedName)
    {
        var path = storedName is null ? null : ResolvePhysicalPath(storedName);
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public IReadOnlyList<CaptureStoredFile> ListFiles() =>
        new DirectoryInfo(root).EnumerateFiles()
            .Select(f => new CaptureStoredFile(f.Name, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToList();

    public int PurgeTemp(TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        var removed = 0;
        foreach (var file in new DirectoryInfo(tempRoot).EnumerateFiles().Where(f => f.LastWriteTimeUtc < cutoff).ToList())
        {
            try
            {
                file.Delete();
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retried by the next sweep.
            }
        }

        return removed;
    }

    private static async Task CopyCappedAsync(Stream source, Stream target, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new CaptureFileTooLargeException(maxBytes);
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static string Normalize(string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return ext.Length <= 6 && ext.Skip(1).All(char.IsLetterOrDigit) ? ext.ToLowerInvariant() : ".bin";
    }
}
