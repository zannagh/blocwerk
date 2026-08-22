using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// Records and reads the image gallery of a wall. Uploads live on disk (see
/// <c>IWallImageStorage</c>); the wall's own photo and the photos retired by past resets stay as
/// byte arrays on their existing rows and are surfaced here as a read-only projection.
/// </summary>
public interface IWallImageService
{
    /// <summary>
    /// Records an image whose bytes have already been committed to the wall-image store.
    /// <paramref name="capturedAt"/> is stamped with the current UTC time when the caller omits it.
    /// </summary>
    Task<WallImage> RecordImageAsync(
        Guid wallId,
        string storagePath,
        string contentType,
        long sizeBytes,
        string? caption = null,
        DateTimeOffset? capturedAt = null,
        CancellationToken ct = default);

    /// <summary>
    /// The wall's gallery, newest capture first, merged across uploads, the wall's current photo
    /// and every reset's previous photo. Paged with <paramref name="skip"/>/<paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<WallGalleryItem>> GetGalleryAsync(
        Guid wallId,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default);

    /// <summary>A single uploaded image row, or null when it does not exist.</summary>
    Task<WallImage?> GetImageAsync(Guid imageId, CancellationToken ct = default);

    /// <summary>
    /// Bytes of a gallery item that lives in the database — the wall's current photo
    /// (<see cref="WallGallerySource.WallPhoto"/>) or a reset's previous photo
    /// (<see cref="WallGallerySource.ResetPhoto"/>). Uploaded items are served from the file store
    /// instead, so they return null here.
    /// </summary>
    Task<WallImageContent?> GetLegacyImageContentAsync(
        Guid wallId,
        WallGallerySource source,
        Guid sourceId,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes an uploaded image and its stored file. The acting user must administer the wall.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The acting user does not administer the wall.</exception>
    Task DeleteImageAsync(Guid imageId, Guid actingUserId, CancellationToken ct = default);
}
