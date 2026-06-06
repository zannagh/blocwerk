using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

public class WallReset
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public int Generation { get; set; }

    public byte[]? PreviousPhoto { get; set; }

    public DateTimeOffset ResetAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid ResetByUserId { get; set; }

    [ForeignKey(nameof(ResetByUserId))]
    public User ResetBy { get; set; } = null!;
}
