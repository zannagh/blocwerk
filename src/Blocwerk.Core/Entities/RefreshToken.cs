using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

public class RefreshToken
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(16384)]
    public required string Token { get; set; }

    [Required]
    public required string UserId { get; set; }

    [Required]
    public required string UserName { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    [NotMapped]
    public bool IsExpired => ExpiresAt < DateTimeOffset.UtcNow;

    public bool IsConsumed { get; set; }
}
