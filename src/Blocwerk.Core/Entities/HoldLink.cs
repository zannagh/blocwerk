using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// Records that two holds on adjacent big-wall panels are the same physical hold. When the same
/// hold appears in two overlapping images each image gets its own <see cref="Hold"/>; this link
/// ties them together so boulders and edits can treat them as one.
/// </summary>
public class HoldLink
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public Guid HoldAId { get; set; }

    [ForeignKey(nameof(HoldAId))]
    public Hold HoldA { get; set; } = null!;

    public Guid HoldBId { get; set; }

    [ForeignKey(nameof(HoldBId))]
    public Hold HoldB { get; set; } = null!;

    public HoldLinkKind Kind { get; set; } = HoldLinkKind.Same;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? CreatedByUserId { get; set; }
}
