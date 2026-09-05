namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Stores beta clips as files on disk (or any mounted volume). Clips are streamed straight to a
/// temp file, optionally transcoded, then committed into the store — the bytes never sit whole in
/// memory the way the old <c>bytea</c> path required.
/// </summary>
public interface IBetaVideoStorage
{
    /// <summary>A fresh temp path on the same volume as the store, so committing it is an atomic move.</summary>
    string CreateTempPath(string extension);

    /// <summary>Moves a finished temp file into the store and returns the stored (relative) name.</summary>
    string Commit(string tempPath, string extension);

    /// <summary>Absolute path of a stored clip, or null when the name escapes the store.</summary>
    string? ResolvePhysicalPath(string storedName);

    /// <summary>Removes a stored clip; missing files are ignored.</summary>
    void Delete(string? storedName);

    /// <summary>
    /// A fresh, empty build directory for a clip's HLS ladder, on the same volume as the store so
    /// committing it is an atomic rename. Any previous build for the same clip is cleared first.
    /// </summary>
    string CreateHlsBuildDirectory(Guid videoId);

    /// <summary>
    /// Atomically swaps the build directory into place as the clip's live HLS output: the previous
    /// output (if any) is removed and the build directory renamed over it. Throws when no build exists.
    /// </summary>
    void CommitHlsDirectory(Guid videoId);

    /// <summary>The absolute path of a clip's live HLS directory (whether or not it exists yet).</summary>
    string GetHlsDirectory(Guid videoId);

    /// <summary>
    /// Resolves a request sub-path to a file inside the clip's live HLS directory, or null when it
    /// escapes the directory, names an unexpected type, or (via a symlink) points outside the store.
    /// </summary>
    string? ResolveHlsFile(Guid videoId, string requestPath);

    /// <summary>Removes a clip's HLS output and any half-written build directory; missing ones are ignored.</summary>
    void DeleteHlsDirectory(Guid videoId);
}
