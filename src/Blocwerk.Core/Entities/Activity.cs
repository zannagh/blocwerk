using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A retrospective training activity: one cluster of the user's logged events (boulder attempts,
/// hangboard and pull-up sessions) that happened close together in time. Events are grouped into
/// an activity by <see cref="Helpers.ActivityGrouping"/> (a ~4h inactivity gap, or the day boundary,
/// starts a new one). Distinct from the live "on the wall now" <see cref="ClimbingSession"/>.
/// </summary>
public class Activity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    /// <summary>The main bouldering wall of the activity, if any. Null for training-only activities.</summary>
    public Guid? WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall? Wall { get; set; }

    /// <summary>Timestamp of the earliest event in the activity.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Timestamp of the latest event in the activity. Grows as events are attached.</summary>
    public DateTimeOffset LastEventAt { get; set; }

    /// <summary>
    /// User-edited duration in minutes. When null the duration is derived from
    /// <see cref="LastEventAt"/> − <see cref="StartedAt"/>.
    /// </summary>
    public int? DurationMinutes { get; set; }
}
