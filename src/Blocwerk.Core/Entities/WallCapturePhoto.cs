using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One uploaded photo of a <see cref="WallCapture"/>. The bytes are on disk (metadata already
/// stripped), not in the database. Width/height are the RAW pixel grid — EXIF orientation is
/// ignored, exactly like the marker decoder — so they are consistent with the marker corners.
/// </summary>
public class WallCapturePhoto
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CaptureId { get; set; }

    [ForeignKey(nameof(CaptureId))]
    public WallCapture Capture { get; set; } = null!;

    /// <summary>Upload order (1-based); also names the photo towards the compute service (<c>p01</c>…).</summary>
    public int Index { get; set; }

    /// <summary>The file name the admin uploaded, for display only.</summary>
    [MaxLength(256)]
    public string? OriginalFileName { get; set; }

    /// <summary>Stored name under the capture file store (a bare file name).</summary>
    [Required]
    [MaxLength(128)]
    public required string StoredPath { get; set; }

    [MaxLength(32)]
    public string ContentType { get; set; } = "image/jpeg";

    /// <summary>SHA-256 (hex) of the stored bytes; a re-uploaded duplicate is refused.</summary>
    [Required]
    [MaxLength(64)]
    public required string ContentHash { get; set; }

    public long SizeBytes { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>EXIF FocalLengthIn35mmFormat, when the camera wrote one.</summary>
    public double? Focal35mm { get; set; }

    /// <summary>Photos sharing intrinsics (camera model + lens + resolution), from EXIF.</summary>
    [MaxLength(256)]
    public string? CameraGroup { get; set; }

    /// <summary>Validated, refined markers (<c>CaptureMarker[]</c> as JSON, pixel corners), null until detected.</summary>
    public string? MarkersJson { get; set; }

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}
