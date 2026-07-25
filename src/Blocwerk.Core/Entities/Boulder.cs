using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

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

    /// <summary>
    /// Derived from the boulder's hold usage marks; never set directly by the user.
    /// </summary>
    public FootholdMode FootholdMode { get; set; } = FootholdMode.AllKickboard;

    /// <summary>
    /// Whether every kickboard foothold counts as on for this boulder.
    /// </summary>
    public bool KickboardFootholdsOn { get; set; } = true;

    /// <summary>
    /// A draft is only visible to its creator until it is published.
    /// </summary>
    public bool IsDraft { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public bool IsArchived { get; set; }

    public bool IsHistoric { get; set; }

    public bool NeedsReview { get; set; }

    public int Generation { get; set; }

    public ICollection<BoulderHold> BoulderHolds { get; set; } = [];

    public ICollection<Attempt> Attempts { get; set; } = [];

    public ICollection<BoulderComment> Comments { get; set; } = [];

    public ICollection<GradeProposal> GradeProposals { get; set; } = [];
}
