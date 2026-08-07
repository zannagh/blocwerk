using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A climb logged on an external service (currently TopLogger) and imported for a user. Not tied to
/// any Blocwerk wall. Feeds the activities page and the boulder rating alongside local attempts;
/// clusters into an <see cref="Activity"/> like a native event. The grade is stored as a Font/V label
/// so scoring goes through the same <see cref="Helpers.GradeScoring"/> path as local boulders.
/// </summary>
public class ExternalAscent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public ExternalSource Source { get; set; } = ExternalSource.TopLogger;

    /// <summary>The source's own id for this ascent — the dedupe key across re-syncs.</summary>
    [MaxLength(128)]
    public required string ExternalId { get; set; }

    [MaxLength(256)]
    public string? ClimbName { get; set; }

    [MaxLength(256)]
    public string? GymName { get; set; }

    /// <summary>Mapped Font/V grade label, or null when it could not be mapped.</summary>
    [MaxLength(16)]
    public string? Grade { get; set; }

    /// <summary>Flash or Send (project ascents that aren't sends are not imported).</summary>
    public AttemptType Type { get; set; }

    public DateTimeOffset LoggedAt { get; set; }

    /// <summary>The activity this ascent was grouped into (see <see cref="Activity"/>).</summary>
    public Guid? ActivityId { get; set; }

    [ForeignKey(nameof(ActivityId))]
    public Activity? Activity { get; set; }
}
