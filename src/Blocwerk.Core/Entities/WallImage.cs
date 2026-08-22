using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A photo of a wall pushed in by a camera or client. The bytes live on disk (see
/// <c>IWallImageStorage</c>) and <see cref="StoragePath"/> holds the stored file name, so a
/// listing never drags image data into memory.
/// </summary>
public class WallImage
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>File name of the image in the wall-image store.</summary>
    [Required]
    [MaxLength(512)]
    public required string StoragePath { get; set; }

    [Required]
    [MaxLength(100)]
    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    [MaxLength(200)]
    public string? Caption { get; set; }

    /// <summary>When the shot was taken. Server-stamped unless the client supplies one.</summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
