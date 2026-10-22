using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

public class ActivityLogEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public Guid? BoulderId { get; set; }

    [ForeignKey(nameof(BoulderId))]
    public Boulder? Boulder { get; set; }

    /// <summary>
    /// The attempt an <see cref="ActivityType.AttemptLogged"/> entry records; the entry goes with it when the attempt is
    /// deleted. Null for every other type, and for an older entry that could not be matched to its attempt.
    /// </summary>
    public Guid? AttemptId { get; set; }

    [ForeignKey(nameof(AttemptId))]
    public Attempt? Attempt { get; set; }

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public ActivityType Type { get; set; }

    [MaxLength(1024)]
    public string? Details { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
