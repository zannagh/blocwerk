using System.ComponentModel.DataAnnotations;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A real gym on an external service (currently TopLogger), shared across all users — one global row
/// per (source, external id). Created lazily the first time any user logs a tick there. Deliberately
/// not user-scoped, so it is never subject to a per-user query filter.
/// </summary>
public class ExternalGym
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public ExternalSource Source { get; set; } = ExternalSource.TopLogger;

    /// <summary>The source's own id for this gym (e.g. the TopLogger gym id).</summary>
    [MaxLength(128)]
    public required string ExternalId { get; set; }

    [MaxLength(256)]
    public required string Name { get; set; }

    [MaxLength(256)]
    public string? Slug { get; set; }

    /// <summary>
    /// Points this gym's scoring adds ON TOP of a climb's base grade points for a flash (0 when the
    /// gym has no flash bonus, or is not calibrated). Combined with the per-grade base points in
    /// <see cref="GymGradePoint"/>, it lets an ascent's <c>points</c> resolve to a grade AND flash/send.
    /// </summary>
    public int FlashBonusPoints { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
