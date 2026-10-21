using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

public class WallReset
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>
    /// The generation being retired by this reset. <see cref="PreviousPhoto"/> is the
    /// wall photo that was live while holds carried this generation.
    /// </summary>
    public int Generation { get; set; }

    public byte[]? PreviousPhoto { get; set; }

    [MaxLength(64)]
    public string? PreviousPhotoContentType { get; set; }

    /// <summary>SHA-256 of <see cref="PreviousPhoto"/>, kept by <c>ContentHashStamp</c> on every save; null without bytes or before the backfill.</summary>
    public byte[]? PreviousPhotoSha256 { get; set; }

    public DateTimeOffset ResetAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid ResetByUserId { get; set; }

    [ForeignKey(nameof(ResetByUserId))]
    public User ResetBy { get; set; } = null!;
}
