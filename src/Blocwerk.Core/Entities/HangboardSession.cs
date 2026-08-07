using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

public class HangboardSession
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public int EdgeSizeMm { get; set; }

    public double AdditionalWeightKg { get; set; }

    public TimeSpan Duration { get; set; }

    public int Sets { get; set; } = 1;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(512)]
    public string? Notes { get; set; }

    /// <summary>The training activity this session was grouped into (see <see cref="Activity"/>).</summary>
    public Guid? ActivityId { get; set; }

    [ForeignKey(nameof(ActivityId))]
    public Activity? Activity { get; set; }
}
