using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;

namespace Blocwerk.Core.Services;

/// <summary>
/// Filesystem-backed <see cref="IBetaVideoStorage"/> rooted at <c>BetaVideoSettings.StoragePath</c>.
/// Temp files live under a <c>tmp</c> subfolder of the same root so a commit is a plain rename.
/// </summary>
public class FileSystemBetaVideoStorage : IBetaVideoStorage
{
    private readonly string root;
    private readonly string tempRoot;

    public FileSystemBetaVideoStorage(BlocwerkSettings settings)
    {
        root = Path.GetFullPath(settings.BetaVideo.StoragePath);
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
        if (Path.IsPathRooted(storedName) || Path.GetFileName(storedName) != storedName)
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(root, storedName));
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

    public string CreateHlsBuildDirectory(Guid videoId)
    {
        var build = BuildDirectory(videoId);
        if (Directory.Exists(build))
        {
            Directory.Delete(build, recursive: true);
        }

        Directory.CreateDirectory(build);
        return build;
    }

    public void CommitHlsDirectory(Guid videoId)
    {
        var build = BuildDirectory(videoId);
        if (!Directory.Exists(build))
        {
            throw new InvalidOperationException("No HLS build directory to commit.");
        }

        var final = GetHlsDirectory(videoId);
        if (Directory.Exists(final))
        {
            Directory.Delete(final, recursive: true);
        }

        // Same-volume rename: the swap from a fully-written build directory to the live one is atomic,
        // so a reader never sees a half-written ladder under the live path.
        Directory.Move(build, final);
    }

    public string GetHlsDirectory(Guid videoId) => Path.Combine(root, videoId.ToString("N"));

    public string? ResolveHlsFile(Guid videoId, string requestPath)
    {
        var directory = GetHlsDirectory(videoId);
        var resolved = HlsPathResolver.Resolve(directory, requestPath);
        if (resolved is null || !File.Exists(resolved))
        {
            return null;
        }

        // Defence against a symlink inside the directory that points back out of the store: compare the
        // real (link-resolved) path against the store root. GetFullPath alone does not follow links.
        var realRoot = Path.GetFullPath(root);
        var rootWithSeparator = realRoot.EndsWith(Path.DirectorySeparatorChar)
            ? realRoot
            : realRoot + Path.DirectorySeparatorChar;
        var real = ResolveRealPath(resolved);
        return real.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? resolved : null;
    }

    public void DeleteHlsDirectory(Guid videoId)
    {
        foreach (var path in new[] { GetHlsDirectory(videoId), BuildDirectory(videoId) })
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private string BuildDirectory(Guid videoId) => GetHlsDirectory(videoId) + ".tmp";

    private static string ResolveRealPath(string path)
    {
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return target is null ? Path.GetFullPath(path) : Path.GetFullPath(target.FullName);
        }
        catch (Exception)
        {
            // A path we cannot inspect for links is treated as itself; the caller's containment check
            // still runs against the canonical (non-link-resolved) path.
            return Path.GetFullPath(path);
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
