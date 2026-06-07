using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

public class GradeProposal
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BoulderId { get; set; }

    [ForeignKey(nameof(BoulderId))]
    public Boulder Boulder { get; set; } = null!;

    public Guid ProposedByUserId { get; set; }

    [ForeignKey(nameof(ProposedByUserId))]
    public User ProposedBy { get; set; } = null!;

    [Required]
    [MaxLength(16)]
    public required string ProposedGrade { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsResolved { get; set; }
}
