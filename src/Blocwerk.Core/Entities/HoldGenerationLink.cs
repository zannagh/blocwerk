using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// Records forward-in-time lineage between an old-generation hold and its successor row in the next
/// generation, materialized when a whole-wall big update carries the curated holds forward onto the
/// new capture. Under immutable generations the old row stays at generation N and a distinct row is
/// created at generation N+1; this link ties them so per-generation history and "changed at gen X"
/// stay queryable. Distinct from <see cref="HoldLink"/> (same physical hold across two panels within
/// one generation) — different semantics, deliberately not conflated.
/// </summary>
public class HoldGenerationLink
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>The predecessor hold, at <see cref="FromGeneration"/>.</summary>
    public Guid OldHoldId { get; set; }

    [ForeignKey(nameof(OldHoldId))]
    public Hold OldHold { get; set; } = null!;

    /// <summary>The successor hold, at <see cref="ToGeneration"/>.</summary>
    public Guid NewHoldId { get; set; }

    [ForeignKey(nameof(NewHoldId))]
    public Hold NewHold { get; set; } = null!;

    public HoldGenerationLinkKind Kind { get; set; } = HoldGenerationLinkKind.Same;

    /// <summary>The generation the predecessor hold belongs to.</summary>
    public int FromGeneration { get; set; }

    /// <summary>The generation the successor hold belongs to.</summary>
    public int ToGeneration { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? CreatedByUserId { get; set; }
}
