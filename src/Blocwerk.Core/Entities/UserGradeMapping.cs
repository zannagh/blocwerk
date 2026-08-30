using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A user's manual resolution of a raw external grade token to a Font grade. Applied to imported
/// <see cref="ExternalAscent"/> rows whose raw grade could not be mapped automatically. One row per
/// (user, raw grade key).
/// </summary>
public class UserGradeMapping
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    /// <summary>The raw grade/points token being mapped (the key matched against ascents).</summary>
    [MaxLength(64)]
    public required string RawGradeKey { get; set; }

    /// <summary>The Font grade the user resolved the raw key to.</summary>
    [MaxLength(16)]
    public required string FontGrade { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
