using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A climb logged on an external service (currently TopLogger) and imported for a user. Not tied to
/// any Blocwerk wall — it references an <see cref="ExternalGym"/> instead. Feeds the activities page
/// and the boulder rating alongside local attempts, clustering into an <see cref="Activity"/> like a
/// native event. The mapped grade is stored as a Font label so scoring goes through the same
/// <see cref="Helpers.GradeScoring"/> path as local boulders.
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
    public required string ClimbName { get; set; }

    /// <summary>
    /// The source's stable id for the climb itself (recurs across ticks / sessions, unlike
    /// <see cref="ExternalId"/>). Used to dedupe the rating by climb identity. Null when unknown.
    /// </summary>
    [MaxLength(64)]
    public string? ClimbId { get; set; }

    /// <summary>The gym this ascent was logged at, if known. SetNull if the gym row is removed.</summary>
    public Guid? ExternalGymId { get; set; }

    [ForeignKey(nameof(ExternalGymId))]
    public ExternalGym? ExternalGym { get; set; }

    public DateTimeOffset LoggedAt { get; set; }

    /// <summary>Flash or Send (project ascents that aren't sends are not imported).</summary>
    public AttemptType Type { get; set; }

    /// <summary>Whether the climb was ticked (logged as done) on the source.</summary>
    public bool Ticked { get; set; }

    /// <summary>Whether the climb was topped, when the source distinguishes it. Null when unknown.</summary>
    public bool? Topped { get; set; }

    /// <summary>Source points for the ascent, when provided. Null when the source has no points.</summary>
    public double? Points { get; set; }

    /// <summary>The raw/scaled grade token as returned by the source, before mapping. Null when none.</summary>
    [MaxLength(32)]
    public string? RawGrade { get; set; }

    /// <summary>Mapped Font grade label, feeding <see cref="Helpers.GradeScoring"/>. Null when not yet mapped.</summary>
    [MaxLength(16)]
    public string? MappedGrade { get; set; }

    /// <summary>True when the raw grade could not be mapped and the user must resolve it manually.</summary>
    public bool NeedsGradeMapping { get; set; }

    /// <summary>The activity this ascent was grouped into (see <see cref="Activity"/>).</summary>
    public Guid? ActivityId { get; set; }

    [ForeignKey(nameof(ActivityId))]
    public Activity? Activity { get; set; }
}
