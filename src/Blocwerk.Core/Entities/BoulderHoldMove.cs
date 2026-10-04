// <copyright file="BoulderHoldMove.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A hold of a boulder that physically moved when the panels were updated: what the boulder was told about it. Kept
/// (within the cutoff) the boulder still uses the successor hold; removed (beyond it) the membership is gone and this row
/// is the only record of which hold it was and what mark it carried, so the "Then" view can still draw it at the old
/// position and the setter can pick another hold. Shown on the boulder while it needs review at <see cref="ToGeneration"/>.
/// </summary>
public class BoulderHoldMove
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    public Guid BoulderId { get; set; }

    [ForeignKey(nameof(BoulderId))]
    public Boulder Boulder { get; set; } = null!;

    /// <summary>The hold at <see cref="FromGeneration"/>. Null once it was deleted.</summary>
    public Guid? OldHoldId { get; set; }

    [ForeignKey(nameof(OldHoldId))]
    public Hold? OldHold { get; set; }

    /// <summary>The successor at <see cref="ToGeneration"/>. Null once it was deleted.</summary>
    public Guid? NewHoldId { get; set; }

    [ForeignKey(nameof(NewHoldId))]
    public Hold? NewHold { get; set; }

    /// <summary>The mark the boulder had on the hold.</summary>
    public HoldType Type { get; set; } = HoldType.Normal;

    /// <summary>The usage rule the boulder had for the hold.</summary>
    public HoldUsage Usage { get; set; } = HoldUsage.HandAndFoot;

    public double DistanceMm { get; set; }

    public HoldMoveSource Source { get; set; }

    public double? RotationDeg { get; set; }

    /// <summary>Kept or Removed (a hold that stayed has no row).</summary>
    public HoldMoveOutcome Outcome { get; set; }

    /// <summary>
    /// The distance, mm, measured again after the new holds were placed on the 3D model, when that differs from what the
    /// boulder was told. The boulder is not changed by it: it is marked for review and shows this number.
    /// </summary>
    public double? RemeasuredDistanceMm { get; set; }

    /// <summary>What the later 3D measurement would have done to the boulder.</summary>
    public HoldMoveOutcome? RemeasuredOutcome { get; set; }

    public int FromGeneration { get; set; }

    public int ToGeneration { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
