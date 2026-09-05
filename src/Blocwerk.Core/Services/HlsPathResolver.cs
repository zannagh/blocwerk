namespace Blocwerk.Core.Services;

/// <summary>
/// Resolves a client-supplied HLS sub-path (the <c>{*path}</c> of the serving route) to an absolute
/// path strictly inside one clip's HLS directory, or null when it escapes that directory or names a
/// file type the ladder never contains. Pure and filesystem-free so the traversal guard is unit-tested
/// without a store on disk; the serving layer still adds a symlink-containment check against the real
/// path before opening the file.
/// </summary>
public static class HlsPathResolver
{
    /// <summary>The only extensions an HLS ladder serves: playlists and MPEG-TS / fMP4 segments.</summary>
    private static readonly string[] AllowedExtensions = [".m3u8", ".ts", ".m4s"];

    /// <summary>
    /// Combines <paramref name="requestPath"/> onto <paramref name="hlsDirectory"/> and returns the
    /// canonical absolute path only when it stays within the directory and ends in an allowed extension.
    /// Rejects absolute/rooted paths, backslashes, <c>..</c> escapes and unexpected extensions.
    /// </summary>
    public static string? Resolve(string hlsDirectory, string requestPath)
    {
        if (string.IsNullOrWhiteSpace(hlsDirectory) || string.IsNullOrWhiteSpace(requestPath))
        {
            return null;
        }

        // A backslash is a path separator on Windows and has no business in an HLS segment name; an
        // absolute or rooted path must never be honoured against the store root.
        if (requestPath.Contains('\\') || requestPath.Contains('\0') || Path.IsPathRooted(requestPath))
        {
            return null;
        }

        var extension = Path.GetExtension(requestPath).ToLowerInvariant();
        if (Array.IndexOf(AllowedExtensions, extension) < 0)
        {
            return null;
        }

        var root = Path.GetFullPath(hlsDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        // GetFullPath collapses any "../" segments; the containment check then rejects anything that
        // climbed out of the directory (or that resolved to the directory itself).
        var combined = Path.GetFullPath(Path.Combine(root, requestPath));
        return combined.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? combined : null;
    }
}
