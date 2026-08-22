using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;

namespace Blocwerk.Core.Services;

/// <summary>
/// Filesystem-backed <see cref="IWallImageStorage"/> rooted at <c>WallImageSettings.StoragePath</c>.
/// Temp files live under a <c>tmp</c> subfolder of the same root so a commit is a plain rename.
/// </summary>
public class FileSystemWallImageStorage : IWallImageStorage
{
    private readonly string root;
    private readonly string tempRoot;

    public FileSystemWallImageStorage(BlocwerkSettings settings)
    {
        root = Path.GetFullPath(settings.WallImage.StoragePath);
        tempRoot = Path.Combine(root, "tmp");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(tempRoot);
    }

    public string CreateTempPath(string extension) =>
        Path.Combine(tempRoot, $"{Guid.NewGuid():N}{Normalize(extension)}");

    public string Commit(string tempPath, string extension)
    {
        var name = $"{Guid.NewGuid():N}{Normalize(extension)}";
        var dest = Path.Combine(root, name);
        File.Move(tempPath, dest, overwrite: true);
        return name;
    }

    public string? ResolvePhysicalPath(string storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName))
        {
            return null;
        }

        // Guard against traversal: only a bare file name directly under the root is valid.
        var candidate = Path.GetFullPath(Path.Combine(root, storedName));
        var expected = Path.Combine(root, Path.GetFileName(storedName));
        return candidate == Path.GetFullPath(expected) ? candidate : null;
    }

    public void Delete(string? storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName))
        {
            return;
        }

        var path = ResolvePhysicalPath(storedName);
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string Normalize(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return ext.Length <= 8 && ext.All(c => char.IsLetterOrDigit(c) || c == '.') ? ext.ToLowerInvariant() : string.Empty;
    }
}
