using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// Join row attributing a boulder to a setter (or co-setter). Distinct from
/// <see cref="Boulder.CreatedBy"/>: the creator is whoever added the boulder in the app, while a
/// setter is who actually set it on the wall — the two may differ, and a boulder can have several
/// co-setters. Mirrors <see cref="BoulderFavorite"/>: composite key, cascade on the boulder,
/// restrict on the user.
/// </summary>
public class BoulderSetter
{
    public Guid BoulderId { get; set; }

    [ForeignKey(nameof(BoulderId))]
    public Boulder Boulder { get; set; } = null!;

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
