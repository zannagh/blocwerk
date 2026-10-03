using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Makes one staged panel's recognised outlines non-overlapping BEFORE the reviewer sees them: a proposal that
/// would overlap another hold (a neighbour's proposal, a staged hand-drawn or circle hold) is clipped, or falls
/// back to a circle. Radii are not part of a proposal, so they never change here. Mutates the proposals only.
/// </summary>
internal static class ShapeProposalOverlaps
{
    /// <summary>Resolves the overlaps; returns the number of proposals that were changed.</summary>
    /// <param name="holds">Every staged hold on the panel.</param>
    /// <param name="proposals">The panel's proposals.</param>
    /// <param name="aspect">Photo width / height.</param>
    /// <returns>How many proposals changed.</returns>
    public static int Resolve(IReadOnlyList<Hold> holds, IReadOnlyList<WallUpdateShapeProposal> proposals, double aspect)
    {
        var byHold = proposals.Where(IsContour).ToDictionary(p => p.HoldId);
        var inputs = holds.Select(h => byHold.TryGetValue(h.Id, out var p) ? Candidate(h, p) : Locked(h)).ToList();
        var changed = 0;
        foreach (var r in HoldShapeOverlapResolver.Resolve(inputs, allowRadiusShrink: false, aspect))
        {
            var proposal = byHold[r.Id];
            var hold = holds.First(h => h.Id == r.Id);
            if (r.Shape is null)
            {
                proposal.Method = HoldOutlineMethod.CircleFallback;
                proposal.ShapeJson = null;
                proposal.HolesJson = null;
                proposal.Confidence = Math.Min(proposal.Confidence, HoldOutlineRefiner.CircleConfidenceCeiling);
                changed++;
            }
            else if (r.Fit != HoldShapeFit.Unchanged)
            {
                var back = ShapeJson.Rebase(r.Shape, hold.X, hold.Y, proposal.AnchorX, proposal.AnchorY);
                proposal.ShapeJson = ShapeJson.Write(back);
                proposal.HolesJson = null;
                changed++;
            }
        }

        return changed;
    }

    private static bool IsContour(WallUpdateShapeProposal p) =>
        p.Method != HoldOutlineMethod.CircleFallback && ShapeJson.Read(p.ShapeJson) is { Count: >= 3 };

    private static HoldShapeInput Candidate(Hold hold, WallUpdateShapeProposal p)
    {
        var shape = ShapeJson.Rebase(ShapeJson.Read(p.ShapeJson)!, p.AnchorX, p.AnchorY, hold.X, hold.Y);
        return new HoldShapeInput(hold.Id, hold.X, hold.Y, hold.Radius, shape, Locked: false);
    }

    private static HoldShapeInput Locked(Hold hold) =>
        new(hold.Id, hold.X, hold.Y, hold.Radius, hold.ShapePoints, Locked: true);
}
