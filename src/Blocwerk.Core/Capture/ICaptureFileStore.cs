namespace Blocwerk.Core.Capture;

/// <summary>
/// Disk store for capture photos and geometry textures, under <c>captures/</c> of the wall-image
/// storage root (<c>WALLIMAGE__STORAGEPATH</c>), following <see cref="Abstractions.IWallImageStorage"/>:
/// write to a temp file on the same volume, commit with an atomic move, address by a bare file name.
/// </summary>
public interface ICaptureFileStore
{
    /// <summary>Writes <paramref name="bytes"/> and returns the stored (bare) name.</summary>
    Task<string> SaveAsync(byte[] bytes, string extension, CancellationToken ct);

    /// <summary>
    /// Streams <paramref name="content"/> to a temp file and commits it; returns the stored (bare) name.
    /// Never buffers the whole stream. Throws <see cref="CaptureFileTooLargeException"/> (and keeps
    /// nothing) once more than <paramref name="maxBytes"/> arrive.
    /// </summary>
    Task<string> SaveStreamAsync(Stream content, string extension, long maxBytes, CancellationToken ct);

    /// <summary>Reads a stored file, or null when it is missing or the name escapes the store.</summary>
    Task<byte[]?> ReadAsync(string storedName, CancellationToken ct);

    /// <summary>Absolute path of a stored file, or null when the name escapes the store.</summary>
    string? ResolvePhysicalPath(string storedName);

    /// <summary>Removes a stored file; missing files are ignored.</summary>
    void Delete(string? storedName);

    /// <summary>Every stored file (bare name, last write) plus leftover temp files, for the orphan sweep.</summary>
    IReadOnlyList<CaptureStoredFile> ListFiles() => [];

    /// <summary>Deletes temp files (interrupted writes) older than <paramref name="age"/>; returns how many.</summary>
    int PurgeTemp(TimeSpan age) => 0;
}

/// <summary>A streamed upload went past its size limit.</summary>
public sealed class CaptureFileTooLargeException(long maxBytes)
    : InvalidOperationException($"The file is larger than {maxBytes / (1024 * 1024)} MB.")
{
    public long MaxBytes { get; } = maxBytes;
}

/// <summary>A file in the capture store, as <see cref="ICaptureFileStore.ListFiles"/> reports it.</summary>
public sealed record CaptureStoredFile(string Name, DateTimeOffset WrittenAt);
