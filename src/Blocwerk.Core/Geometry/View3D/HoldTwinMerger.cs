// <copyright file="HoldTwinMerger.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Finds the physical holds among a wall's placed hold rows. Overlapping panels each photograph the
/// holds in their overlap, so one physical hold is stored once per panel; mapped onto the measured
/// facets those copies land on top of each other. Stored <see cref="Entities.HoldLink"/>s are used
/// first; a geometric fallback then pairs same-coloured, similar-sized holds of different panels that
/// sit on one facet within a fraction of their size. View-layer only: nothing is written back.
/// </summary>
public static class HoldTwinMerger
{
    /// <summary>Geometric twins: centres at most this far apart, whatever their size.</summary>
    public const double MinTwinDistanceMm = 15;

    /// <summary>Geometric twins: centres at most this fraction of the smaller hold's size apart.</summary>
    public const double TwinDistanceFraction = 0.35;

    /// <summary>Geometric twins: larger size over smaller size must stay below this.</summary>
    public const double MaxTwinSizeRatio = 1.6;

    /// <summary>A stored link is trusted up to this distance, whatever the holds' size.</summary>
    public const double MinLinkDistanceMm = 200;

    /// <summary>A stored link is trusted up to this multiple of the larger hold's size.</summary>
    public const double LinkDistanceFactor = 2.5;

    /// <summary>Two tilts count as different views when they differ by more than this fraction.</summary>
    public const double TiltDifference = 0.15;

    /// <summary>Groups <paramref name="candidates"/> into physical holds, representative first.</summary>
    /// <param name="candidates">The placed live hold rows.</param>
    /// <param name="links">Stored "same physical hold" links of the wall.</param>
    /// <returns>The groups and how they were found.</returns>
    public static HoldTwinGroups Group(IReadOnlyList<HoldTwinCandidate> candidates, IEnumerable<HoldLinkPair>? links)
    {
        var sets = new TwinSets(candidates);
        var index = candidates.Select((c, i) => (c.Hold.Id, i)).ToDictionary(p => p.Id, p => p.i);

        int explicitMerges = 0, rejected = 0;
        foreach (var link in links ?? [])
        {
            if (!index.TryGetValue(link.HoldAId, out var a) || !index.TryGetValue(link.HoldBId, out var b))
            {
                continue;
            }

            if (!IsPlausibleLink(candidates[a], candidates[b]))
            {
                rejected++;
                continue;
            }

            if (sets.Union(a, b))
            {
                explicitMerges++;
            }
        }

        var geometricMerges = 0;
        foreach (var (a, b) in GeometricPairs(candidates))
        {
            if (!sets.SharePanel(a, b) && sets.Union(a, b))
            {
                geometricMerges++;
            }
        }

        var groups = sets.Components()
            .Select(g => (IReadOnlyList<HoldTwinCandidate>)Order(g.Select(i => candidates[i]).ToList()))
            .ToList();
        return new HoldTwinGroups(groups, explicitMerges, geometricMerges, rejected);
    }

    /// <summary>
    /// True when two rows of DIFFERENT panels on one facet are the same physical hold by geometry alone:
    /// same colour, sizes within <see cref="MaxTwinSizeRatio"/>, centres within
    /// max(<see cref="MinTwinDistanceMm"/>, <see cref="TwinDistanceFraction"/> × the smaller size).
    /// </summary>
    /// <param name="a">One row.</param>
    /// <param name="b">The other.</param>
    /// <returns>Whether they are geometric twins.</returns>
    public static bool AreGeometricTwins(HoldTwinCandidate a, HoldTwinCandidate b)
    {
        if (a.Hold.WallPanelId == b.Hold.WallPanelId || a.Placed.FacetId != b.Placed.FacetId
            || !string.Equals(a.Hold.Color, b.Hold.Color, StringComparison.Ordinal))
        {
            return false;
        }

        var sizeA = SizeOf(a.Placed);
        var sizeB = SizeOf(b.Placed);
        var small = Math.Min(sizeA, sizeB);
        if (!(small > 0) || Math.Max(sizeA, sizeB) / small >= MaxTwinSizeRatio)
        {
            return false;
        }

        return PlaneDistance(a.Placed, b.Placed) <= Math.Max(MinTwinDistanceMm, TwinDistanceFraction * small);
    }

    /// <summary>
    /// Orders a group so its best view comes first: a real outline beats a stand-in circle; then the
    /// more perpendicular camera (when both tilts are known and clearly differ); then the hold nearer its
    /// panel photo's centre; the id breaks remaining ties so the choice is stable.
    /// </summary>
    /// <param name="group">The rows of one physical hold.</param>
    /// <returns>The rows, representative first.</returns>
    public static List<HoldTwinCandidate> Order(List<HoldTwinCandidate> group)
    {
        var ordered = group.OrderBy(c => c.Hold.Id).ToList();
        ordered.Sort(Compare);
        return ordered;
    }

    private static int Compare(HoldTwinCandidate x, HoldTwinCandidate y)
    {
        var outline = HasOutline(y).CompareTo(HasOutline(x));
        if (outline != 0)
        {
            return outline;
        }

        if (x.Tilt is { } tx && y.Tilt is { } ty && Math.Abs(tx - ty) > TiltDifference * Math.Max(tx, ty))
        {
            return tx.CompareTo(ty);
        }

        var centre = CentreOffset(x).CompareTo(CentreOffset(y));
        return centre != 0 ? centre : x.Hold.Id.CompareTo(y.Hold.Id);
    }

    private static bool HasOutline(HoldTwinCandidate c) =>
        c.Placed.Shape is { Source: not Wall3DShapeSource.Circle };

    private static double CentreOffset(HoldTwinCandidate c) =>
        Math.Sqrt(((c.Hold.X - 0.5) * (c.Hold.X - 0.5)) + ((c.Hold.Y - 0.5) * (c.Hold.Y - 0.5)));

    /// <summary>A stored link is trusted unless it ties rows of one panel or holds implausibly far apart.</summary>
    private static bool IsPlausibleLink(HoldTwinCandidate a, HoldTwinCandidate b)
    {
        if (a.Hold.WallPanelId == b.Hold.WallPanelId)
        {
            return false;
        }

        var limit = Math.Max(MinLinkDistanceMm, LinkDistanceFactor * Math.Max(SizeOf(a.Placed), SizeOf(b.Placed)));
        return WorldDistance(a.Placed, b.Placed) <= limit;
    }

    /// <summary>Every geometric twin pair, per facet, closest first (so a hold pairs with its nearest twin).</summary>
    private static IEnumerable<(int A, int B)> GeometricPairs(IReadOnlyList<HoldTwinCandidate> candidates)
    {
        var pairs = new List<(int A, int B, double D)>();
        foreach (var facet in candidates.Select((c, i) => (c, i)).GroupBy(p => p.c.Placed.FacetId))
        {
            var members = facet.ToList();
            for (var i = 0; i < members.Count; i++)
            {
                for (var j = i + 1; j < members.Count; j++)
                {
                    if (AreGeometricTwins(members[i].c, members[j].c))
                    {
                        pairs.Add((members[i].i, members[j].i, PlaneDistance(members[i].c.Placed, members[j].c.Placed)));
                    }
                }
            }
        }

        return pairs.OrderBy(p => p.D).ThenBy(p => p.A).ThenBy(p => p.B).Select(p => (p.A, p.B));
    }

    private static double SizeOf(Wall3DHold h) => Math.Max(h.WidthMm, h.HeightMm);

    private static double PlaneDistance(Wall3DHold a, Wall3DHold b) =>
        Math.Sqrt(((a.PlaneA - b.PlaneA) * (a.PlaneA - b.PlaneA)) + ((a.PlaneB - b.PlaneB) * (a.PlaneB - b.PlaneB)));

    private static double WorldDistance(Wall3DHold a, Wall3DHold b) =>
        Math.Sqrt(Enumerable.Range(0, 3).Sum(k => (a.Position[k] - b.Position[k]) * (a.Position[k] - b.Position[k])));
}
