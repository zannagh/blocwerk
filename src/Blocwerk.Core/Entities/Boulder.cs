using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

public class Boulder
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    [Required]
    [MaxLength(256)]
    public required string Name { get; set; }

    [MaxLength(16)]
    public string? Grade { get; set; }

    public Guid CreatedByUserId { get; set; }

    [ForeignKey(nameof(CreatedByUserId))]
    public User CreatedBy { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsArchived { get; set; }

    public int Generation { get; set; }

    public ICollection<BoulderHold> BoulderHolds { get; set; } = [];

    public ICollection<Attempt> Attempts { get; set; } = [];
}
