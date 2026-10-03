// <copyright file="NeighbourPanelRule.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// The panel that owns the overlap with a staged neighbour (the staged centre), as the neighbour's triage
/// sees it. All RAW pixels.
/// </summary>
/// <param name="ToOwner">Overlap pairs, this panel's photo → the owner's photo (the overlaps step's proposals).</param>
/// <param name="OwnerSize">The owner's photo size.</param>
/// <param name="OwnerNewHolds">The owner's unpaired detections its own triage kept (genuinely new holds).</param>
/// <param name="PhotoSize">This panel's photo size.</param>
public sealed record OverlapOwner(
    IReadOnlyList<PointPair> ToOwner,
    (int Width, int Height) OwnerSize,
    IReadOnlyList<(double X, double Y)> OwnerNewHolds,
    (int Width, int Height) PhotoSize);

/// <summary>
/// A neighbour photo usually shows part of the centre panel (an oblique shot sees the main wall at its
/// edge). A detection there that the overlap maps inside the centre photo belongs to the centre: either a
/// hold the centre already has, or nothing the centre's detector saw. The one exception is a new hold the
/// centre kept as new — the same hold seen twice, which the overlaps step links.
/// </summary>
public static class NeighbourPanelRule
{
    /// <summary>The mapping is only trusted this close to an overlap pair (fraction of the photo's longer side).</summary>
    public const double Reach = 0.1;

    /// <summary>How close (fraction of the owner photo's longer side) a kept new owner hold counts as the same hold.</summary>
    public const double TwinDistance = 0.015;

    private const int MinimumPairs = 6;

    /// <summary>True when the owner photo shows this spot and has no kept new hold there.</summary>
    public static bool ShownByOwner(OverlapOwner owner, double x, double y)
    {
        if (owner.ToOwner.Count < MinimumPairs)
        {
            return false;
        }

        var reach = Reach * Math.Max(owner.PhotoSize.Width, owner.PhotoSize.Height);
        if (!owner.ToOwner.Any(p => Distance(p.SrcX, p.SrcY, x, y) <= reach))
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
        return !owner.OwnerNewHolds.Any(n => Distance(n.X, n.Y, m.X, m.Y) <= twin);
    }

    private static double Distance(double ax, double ay, double bx, double by) =>
        Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
}
