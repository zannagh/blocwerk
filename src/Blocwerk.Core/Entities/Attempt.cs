using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

public class Attempt
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BoulderId { get; set; }

    [ForeignKey(nameof(BoulderId))]
    public Boulder Boulder { get; set; } = null!;

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public AttemptType Type { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(512)]
    public string? Notes { get; set; }

    /// <summary>
    /// Client-generated de-duplication key for offline replay. Unique across all attempts
    /// when set, so replaying a queued log after a reconnect cannot create a second row.
    /// </summary>
    public Guid? ClientRequestId { get; set; }

    /// <summary>The training activity this attempt was grouped into (see <see cref="Activity"/>).</summary>
    public Guid? ActivityId { get; set; }

    [ForeignKey(nameof(ActivityId))]
    public Activity? Activity { get; set; }
}
