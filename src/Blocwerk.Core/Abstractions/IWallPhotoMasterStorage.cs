namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Stores full-resolution stitched wall masters (ortho and angled) as files on disk, mirroring
/// <see cref="IWallImageStorage"/>: a download is streamed to a temp file on the same volume and
/// then committed with an atomic move, so a ~41 MB master never sits whole in memory.
/// </summary>
public interface IWallPhotoMasterStorage
{
    /// <summary>A fresh temp path on the same volume as the store, so committing it is an atomic move.</summary>
    string CreateTempPath(string extension);

    /// <summary>Moves a finished temp file into the store and returns the stored (relative) name.</summary>
    string Commit(string tempPath, string extension);

    /// <summary>Absolute path of a stored master, or null when the name escapes the store.</summary>
    string? ResolvePhysicalPath(string storedName);

    /// <summary>Removes a stored master; missing files are ignored.</summary>
    void Delete(string? storedName);
}
