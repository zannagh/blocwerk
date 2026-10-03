// <copyright file="ShapeDecisionApplier.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// Writes one reviewed <see cref="WallUpdateShapeProposal"/> onto its hold. Pure; the promote calls it for
/// every proposal of a completed run and nothing else ever does, which is what keeps the review free of
/// side effects until then. Pending and "keep previous" change nothing.
/// </summary>
public static class ShapeDecisionApplier
{
    /// <summary>Applies the proposal's verdict to <paramref name="hold"/>.</summary>
    /// <returns>Whether the hold changed.</returns>
    public static bool Apply(Hold hold, WallUpdateShapeProposal proposal)
    {
        return proposal.Decision switch
        {
            ShapeReviewDecision.Accepted => ApplyRecognised(hold, proposal),
            ShapeReviewDecision.Adjusted => ApplyAdjusted(hold, proposal),
            ShapeReviewDecision.Circle => ApplyCircle(hold),
            _ => false,
        };
    }

    private static bool ApplyRecognised(Hold hold, WallUpdateShapeProposal proposal)
    {
        // An accepted circle fallback found nothing trustworthy: accepting it means "fine as it is".
        if (ShapeJson.Read(proposal.ShapeJson) is not { Count: >= 3 } shape)
        {
            return false;
        }

        var rebased = ShapeJson.Rebase(shape, proposal.AnchorX, proposal.AnchorY, hold.X, hold.Y);
        var holes = ShapeJson.ReadRings(proposal.HolesJson)?
            .Select(ring => ShapeJson.Rebase(ring, proposal.AnchorX, proposal.AnchorY, hold.X, hold.Y))
            .ToList();
        WriteShape(hold, rebased);
        hold.ShapeHoles = holes is { Count: > 0 } ? holes : null;
        hold.OutlineSource = HoldOutlineSource.AutoContour;
        hold.OutlineConfidence = proposal.Confidence;
        return true;
    }

    private static bool ApplyAdjusted(Hold hold, WallUpdateShapeProposal proposal)
    {
        if (ShapeJson.Read(proposal.AdjustedShapeJson) is not { Count: >= 3 } shape)
        {
            return false;
        }

        WriteShape(hold, ShapeJson.Rebase(shape, proposal.AnchorX, proposal.AnchorY, hold.X, hold.Y));
        hold.ShapeHoles = null;
        hold.OutlineSource = HoldOutlineSource.Manual;
        hold.OutlineConfidence = null;
        return true;
    }

    private static bool ApplyCircle(Hold hold)
    {
        WriteShape(hold, null);
        hold.ShapeHoles = null;

        // A person decided this hold is a circle: Manual protects that from the next update's recognition.
        hold.OutlineSource = HoldOutlineSource.Manual;
        hold.OutlineConfidence = null;
        return true;
    }

    /// <summary>A different outline makes the metric size stale (see <c>Hold.Glyph.cs</c>).</summary>
    private static void WriteShape(Hold hold, List<ShapePoint>? shape)
    {
        if (hold.ShapeDiffers(shape))
        {
            hold.InvalidateGlyphSize();
        }

        hold.ShapePoints = shape;
    }
}
