using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A live climbing session: the user has told the app they are on a wall right now. It is
/// deliberately lightweight — a start time and the wall — and auto-closes once its day passes,
/// so a forgotten session never runs forever.
/// </summary>
public class ClimbingSession
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the session was closed, or null while it is still live.</summary>
    public DateTimeOffset? EndedAt { get; set; }
}
