namespace Blocwerk.Core.Services;

/// <summary>
/// The outcome of looking at (or cleaning) every photo a wall stores: the wall photo and its staged
/// copy, every panel photo of every generation (live and staged), the photos archived by resets, the
/// gallery uploads on disk, and the copies of those photos kept in the change journal.
/// </summary>
/// <param name="Photos">Photos looked at (JPEG and PNG; other formats are counted but never changed).</param>
/// <param name="WithLocation">Photos whose EXIF carried a GPS block when they were looked at.</param>
/// <param name="WithMetadata">Photos that carried removable metadata (GPS included) when they were looked at.</param>
/// <param name="Cleaned">Photos rewritten without it — always 0 for a dry run.</param>
/// <param name="Skipped">Photos left alone because they changed while the run was going (re-run to pick them up).</param>
public sealed record PhotoPrivacyReport(int Photos, int WithLocation, int WithMetadata, int Cleaned, int Skipped);

/// <summary>
/// Removes location and other metadata from the photos a wall ALREADY stores — new uploads are cleaned
/// at ingest (<see cref="Capture.StoredPhotoSanitizer"/>). Pixels and the EXIF orientation are never
/// changed, so holds stay exactly where they are drawn. Wall admins only, never from a kiosk tablet.
/// </summary>
public interface IWallPhotoPrivacyService
{
    /// <summary>Dry run: counts what <see cref="RemoveMetadataAsync"/> would clean, changing nothing.</summary>
    Task<PhotoPrivacyReport> ScanAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>
    /// Rewrites every photo that carries metadata, one photo at a time. Idempotent: a second run finds
    /// nothing to clean. A photo replaced while the run was going is skipped rather than overwritten.
    /// </summary>
    Task<PhotoPrivacyReport> RemoveMetadataAsync(Guid wallId, CancellationToken ct = default);
}
