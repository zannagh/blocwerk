using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// One entry of a wall's image gallery, unified across the three places wall imagery lives:
/// uploaded images on disk, the wall's current photo, and the photos retired by past resets.
/// The legacy photos stay where they are — this is a read-only projection, never a copy.
/// </summary>
/// <param name="Id">
/// Identifies the row the bytes come from: the <c>WallImage</c> id, the wall id, or the
/// <c>WallReset</c> id, depending on <paramref name="Source"/>.
/// </param>
/// <param name="Source">Which store the caller has to fetch the bytes from.</param>
/// <param name="WallId">The wall the item belongs to.</param>
/// <param name="ContentType">MIME type of the image.</param>
/// <param name="SizeBytes">Size of the image in bytes.</param>
/// <param name="Caption">Caption for uploads, or a generated label for the legacy photos.</param>
/// <param name="CapturedAt">When the shot was taken; the sort key of the gallery.</param>
public sealed record WallGalleryItem(
    Guid Id,
    WallGallerySource Source,
    Guid WallId,
    string ContentType,
    long SizeBytes,
    string? Caption,
    DateTimeOffset CapturedAt);

/// <summary>Raw bytes of a legacy (database-stored) gallery item, ready to be served.</summary>
public sealed record WallImageContent(byte[] Data, string ContentType);
