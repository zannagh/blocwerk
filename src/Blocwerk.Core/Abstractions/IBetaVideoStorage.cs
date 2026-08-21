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
}
