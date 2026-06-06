using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

public class Wall
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(256)]
    public required string Name { get; set; }

    [MaxLength(1024)]
    public string? Description { get; set; }

    public byte[]? Photo { get; set; }

    [MaxLength(64)]
    public string? PhotoContentType { get; set; }

    public Guid OwnerId { get; set; }

    [ForeignKey(nameof(OwnerId))]
    public User Owner { get; set; } = null!;

    [MaxLength(64)]
    public string? ShareToken { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastResetAt { get; set; }

    public int CurrentGeneration { get; set; }

    public ICollection<WallMember> Members { get; set; } = [];

    public ICollection<Hold> Holds { get; set; } = [];

    public ICollection<Boulder> Boulders { get; set; } = [];

    public ICollection<WallReset> Resets { get; set; } = [];
}
