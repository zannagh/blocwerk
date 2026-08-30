using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One calibrated (grade → base points) entry for a gym's scoring, shared across all users. A
/// TopLogger ascent's <c>points</c> equals the matching grade's base points plus the gym's
/// <see cref="ExternalGym.FlashBonusPoints"/> when it was flashed, so the pair lets an ascent's points
/// resolve deterministically to a grade and to flash/send. One row per (gym, grade).
/// </summary>
public class GymGradePoint
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The gym this calibration entry belongs to. Cascades with the gym.</summary>
    public Guid ExternalGymId { get; set; }

    [ForeignKey(nameof(ExternalGymId))]
    public ExternalGym? ExternalGym { get; set; }

    /// <summary>The Font grade label (feeds <see cref="Helpers.GradeScoring"/>).</summary>
    [MaxLength(16)]
    public required string Grade { get; set; }

    /// <summary>The base points this gym awards for a send at <see cref="Grade"/>.</summary>
    public int Points { get; set; }
}
