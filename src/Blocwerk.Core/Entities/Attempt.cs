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
}
