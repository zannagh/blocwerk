using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A short "this is how it goes" clip somebody uploaded for a boulder.
/// </summary>
/// <remarks>
/// The clip itself lives in <see cref="Data"/> (Postgres <c>bytea</c>), matching how wall photos
/// are stored — one database, one backup, no second storage system to operate. That only holds
/// because uploads are capped (see <c>BetaVideoService.MaxVideoBytes</c>); read sites must
/// project the columns they need rather than materialising this entity, or every list query would
/// drag every clip into memory.
/// </remarks>
public class BetaVideo
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BoulderId { get; set; }

    [ForeignKey(nameof(BoulderId))]
    public Boulder Boulder { get; set; } = null!;

    public Guid UploadedByUserId { get; set; }

    [ForeignKey(nameof(UploadedByUserId))]
    public User UploadedBy { get; set; } = null!;

    [Required]
    [MaxLength(64)]
    public required string ContentType { get; set; }

    /// <summary>The original file name, kept only so a download has something sensible to be called.</summary>
    [MaxLength(256)]
    public string? FileName { get; set; }

    public long SizeBytes { get; set; }

    [Required]
    public required byte[] Data { get; set; }

    /// <summary>
    /// A JPEG poster frame grabbed in the browser before upload (see wwwroot/js/beta-video.js).
    /// Null when the browser could not decode the clip; the carousel then falls back to a
    /// placeholder tile rather than pulling the whole video down to draw a thumbnail.
    /// </summary>
    public byte[]? Thumbnail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
