namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Stores wall images as files on disk (or any mounted volume), mirroring
/// <see cref="IBetaVideoStorage"/>: an upload is streamed to a temp file on the same volume and
/// then committed with an atomic move, so the bytes never sit whole in memory.
/// </summary>
public interface IWallImageStorage
{
    /// <summary>A fresh temp path on the same volume as the store, so committing it is an atomic move.</summary>
    string CreateTempPath(string extension);

    /// <summary>Moves a finished temp file into the store and returns the stored (relative) name.</summary>
    string Commit(string tempPath, string extension);

    /// <summary>Absolute path of a stored image, or null when the name escapes the store.</summary>
    string? ResolvePhysicalPath(string storedName);

    /// <summary>Removes a stored image; missing files are ignored.</summary>
    void Delete(string? storedName);
}
