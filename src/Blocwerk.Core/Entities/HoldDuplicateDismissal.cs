using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// "Not a duplicate": an admin looked at two holds and said they are different, so the review list never suggests the
/// pair again. The hold ids are plain ids (no foreign keys, lower id first) so a deleted hold never blocks anything.
/// </summary>
public class HoldDuplicateDismissal
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>The lower of the two hold ids.</summary>
    public Guid HoldAId { get; set; }

    /// <summary>The higher of the two hold ids.</summary>
    public Guid HoldBId { get; set; }

    public Guid DismissedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
