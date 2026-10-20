// <copyright file="NeighbourPanelRule.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Services;

/// <summary>
/// The panel that owns the overlap with a staged neighbour (the staged centre), as the neighbour's triage
/// sees it. All RAW pixels.
/// </summary>
/// <param name="ToOwner">Overlap pairs, this panel's photo → the owner's photo (the overlaps step's proposals).</param>
/// <param name="OwnerSize">The owner's photo size.</param>
/// <param name="OwnerNewHolds">The owner's unpaired detections its own triage kept (genuinely new holds).</param>
/// <param name="PhotoSize">This panel's photo size.</param>
/// <param name="OwnerHolds">
/// What the owner settled itself: the detections it carries an old hold onto, its hand-placed holds and the detections
/// its own triage discarded. Null when unknown: every spot the owner shows then counts as settled.
/// </param>
/// <param name="Measured">
/// The pairs of <paramref name="ToOwner"/> backed by measured anchors (<see cref="NeighbourPanelRule.IsMeasured"/>), or null
/// when unknown: then every pair is trusted as before.
/// </param>
public sealed record OverlapOwner(
    IReadOnlyList<PointPair> ToOwner,
    (int Width, int Height) OwnerSize,
    IReadOnlyList<(double X, double Y)> OwnerNewHolds,
    (int Width, int Height) PhotoSize,
    IReadOnlyList<TriageSpot>? OwnerHolds = null,
    IReadOnlyList<PointPair>? Measured = null);

/// <summary>
/// A neighbour photo usually shows part of the centre panel (an oblique shot sees the main wall at its
/// edge). A detection there that the overlap maps inside the centre photo belongs to the centre: either a
/// hold the centre already has, or nothing the centre's detector saw. The one exception is a new hold the
/// centre kept as new — the same hold seen twice, which the overlaps step links. When the owner's holds are known, the
/// rule only applies where the owner has one: a detection on a spot the owner has nothing for stays a new hold.
/// Inside the region the measured pairs span (<see cref="IsMeasured"/>: backed by texture, marker or wall-space anchors),
/// a spot must also be near a measured pair: the matcher also proposes pairs far from any such anchor, resting on its own
/// extrapolated field, and between the measured parts those reach into wall the centre photo does not show (The Attic's
/// right side wall, where the rule threw two real new holds away). Beyond the measured region (an oblique strip where
/// texture matching fails) there is nothing better than the field, and the rule works as before.
/// </summary>
public static class NeighbourPanelRule
{
    /// <summary>The mapping is only trusted this close to an overlap pair (fraction of the photo's longer side).</summary>
    public const double Reach = 0.1;

    /// <summary>How close (fraction of the owner photo's longer side) a kept new owner hold counts as the same hold.</summary>
    public const double TwinDistance = 0.015;

    private const int MinimumPairs = 6;

    /// <summary>True when the owner photo shows this spot, settled it with a hold of its own and has no kept new hold there.</summary>
    public static bool ShownByOwner(OverlapOwner owner, double x, double y)
    {
        if (owner.ToOwner.Count < MinimumPairs)
        {
            return false;
        }

        var reach = Reach * Math.Max(owner.PhotoSize.Width, owner.PhotoSize.Height);
        if (!owner.ToOwner.Any(p => Distance(p.SrcX, p.SrcY, x, y) <= reach) || Unmeasured(owner.Measured, x, y, reach))
        {
            return false;
        }

        if (LocalAffine.Predict(owner.ToOwner, x, y) is not { } m)
        {
            return false;
        }

        var (w, h) = owner.OwnerSize;
        if (m.X < 0 || m.Y < 0 || m.X > w || m.Y > h)
        {
            return false;
        }

        var twin = TwinDistance * Math.Max(w, h);
        if (owner.OwnerNewHolds.Any(n => Distance(n.X, n.Y, m.X, m.Y) <= twin))
        {
            return false;
        }

        return owner.OwnerHolds is null || owner.OwnerHolds.Any(o => Distance(o.X, o.Y, m.X, m.Y) <= twin + o.RadiusPx);
    }

    /// <summary>Whether an overlap proposal of this confidence is backed by measured anchors (inside the shared overlap).</summary>
    /// <param name="confidence">The proposal's confidence.</param>
    /// <returns>True above <see cref="HoldOverlapConfidence.UnsupportedCap"/>.</returns>
    public static bool IsMeasured(double confidence) => confidence > HoldOverlapConfidence.UnsupportedCap;

    /// <summary>True when the spot lies within the measured pairs' hull but no measured pair is within reach of it.</summary>
    private static bool Unmeasured(IReadOnlyList<PointPair>? measured, double x, double y, double reach) =>
        measured is { Count: >= MinimumPairs }
        && !measured.Any(p => Distance(p.SrcX, p.SrcY, x, y) <= reach)
        && PhotoCoverage.Inside(PhotoCoverage.ConvexHull(measured.Select(p => (p.SrcX, p.SrcY)).ToList()), x, y);

    private static double Distance(double ax, double ay, double bx, double by) =>
        Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
}
