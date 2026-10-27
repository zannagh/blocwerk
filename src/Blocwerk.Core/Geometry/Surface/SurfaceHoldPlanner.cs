// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Geometry.Surface;

/// <summary>
/// The owner's placement rule (D49): every hold of a registered panel photo is cast onto the photo-real surface
/// (<see cref="SurfaceCaster"/>). Linked copies of one physical hold get ONE position, from the view that saw the facet most
/// head-on. A hold no view could cast falls back to the outlier check (<see cref="HoldOutlierPlanner"/>) run over the wall
/// as the surface placed it; failing that it stays as it is. Only 3D fields are planned.
/// </summary>
public static class SurfaceHoldPlanner
{
    /// <summary>
    /// Linked copies whose own casts lie further apart than this are not given one position (a wrong link). The Attic's
    /// linked pairs cast from both photos: median 23-29 mm, p90 about 100 mm (gate 0b).
    /// </summary>
    public const double MaxShareGapMm = 150;

    /// <summary>Plans the wall.</summary>
    public static SurfacePlan Plan(
        IReadOnlyList<Hold> live,
        IEnumerable<HoldLinkPair> links,
        IReadOnlyDictionary<Guid, SolvedCamera> poses,
        IReadOnlyDictionary<string, (FacetFrame Frame, PlaneRectMm Extent)> facets,
        SplatSurface surface)
    {
        var holds = live.Where(h => !h.IsVirtual && h.WallPanelId is { } p && poses.ContainsKey(p)).OrderBy(h => h.Id).ToList();
        var casts = holds.Select(h => (h.Id, Cast: SurfaceCaster.Cast(h, poses[h.WallPanelId!.Value], facets, surface)))
            .Where(c => c.Cast is not null).ToDictionary(c => c.Id, c => c.Cast!);
        var fixes = new Dictionary<Guid, (SurfaceFix Fix, bool Shared)>();
        var gaps = new List<double>();
        foreach (var group in Groups(holds, links))
        {
            var cast = group.Where(h => casts.ContainsKey(h.Id)).Select(h => (Hold: h, Cast: casts[h.Id]))
                .OrderBy(c => c.Cast.ThetaDeg).ThenBy(c => c.Hold.Id).ToList();
            if (cast.Count == 0)
            {
                continue;
            }

            if (cast.Count >= 2)
            {
                gaps.Add(Distance(facets, cast[0].Cast, cast[1].Cast));
            }

            var best = cast[0].Cast;
            foreach (var hold in group)
            {
                // A copy whose own cast is far from the shared one is not the same hold (a wrong link): it keeps its own.
                var own = casts.GetValueOrDefault(hold.Id);
                var at = own is not null && Distance(facets, own, best) > MaxShareGapMm ? own : best;
                fixes[hold.Id] = (Fix(hold, poses[hold.WallPanelId!.Value], at, facets[at.FacetId].Frame), at != own);
            }
        }

        var fallback = Fallback(live, holds.Where(h => !fixes.ContainsKey(h.Id)).ToList(), fixes.Values.Select(f => f.Fix), links, facets);
        var all = fixes.Values.Select(f => f.Fix).Concat(fallback).ToList();
        var panels = holds.GroupBy(h => h.WallPanelId!.Value).OrderBy(g => g.Key).Select(g => new SurfacePanelCount(
            g.Key,
            g.Count(),
            g.Count(h => fixes.TryGetValue(h.Id, out var f) && !f.Shared),
            g.Count(h => fixes.TryGetValue(h.Id, out var f) && f.Shared),
            g.Count(h => fallback.Any(f => f.Hold.Id == h.Id)),
            g.Count(h => !fixes.ContainsKey(h.Id) && fallback.All(f => f.Hold.Id != h.Id)))).ToList();
        return new SurfacePlan(all, panels, gaps);
    }

    /// <summary>The fix of <paramref name="hold"/> at <paramref name="at"/>, sized and outlined from its own view.</summary>
    private static SurfaceFix Fix(Hold hold, SolvedCamera camera, SurfaceCast at, FacetFrame frame)
    {
        var footprint = SurfaceCaster.Silhouette(hold, camera, frame) is { } ring
            ? HoldFootprintEstimator.Corrected(frame, (at.A, at.B), new FootprintView(ring, camera.Centre, camera.Image), HoldFootprint.KeyOf(hold))
            : null;
        var size = footprint is null ? null : SizeOf(footprint, at);
        return new SurfaceFix(hold, at.FacetId, at.A, at.B, size, footprint, false);
    }

    /// <summary>The footprint's extent along u and v and its area, tagged <see cref="HoldMetric.Surface"/>.</summary>
    private static HoldMetric? SizeOf(HoldFootprint footprint, SurfaceCast at)
    {
        var o = footprint.Outline;
        var width = o.Max(p => p[0]) - o.Min(p => p[0]);
        var height = o.Max(p => p[1]) - o.Min(p => p[1]);
        var area = Math.Abs(o.Select((p, i) => (p[0] * o[(i + 1) % o.Count][1]) - (o[(i + 1) % o.Count][0] * p[1])).Sum()) / 2;
        return width > 0 && height > 0 ? new HoldMetric(width, height, area, at.FacetId, at.A, at.B, HoldMetric.Surface) : null;
    }

    /// <summary>The outlier check's fixes for the holds the surface missed, over the wall as the surface placed it.</summary>
    private static List<SurfaceFix> Fallback(
        IReadOnlyList<Hold> live, List<Hold> missed, IEnumerable<SurfaceFix> placed, IEnumerable<HoldLinkPair> links,
        IReadOnlyDictionary<string, (FacetFrame Frame, PlaneRectMm Extent)> facets)
    {
        if (missed.Count == 0)
        {
            return [];
        }

        var moved = placed.ToDictionary(f => f.Hold.Id);
        var wall = live.Select(h =>
        {
            var copy = h.Clone();
            if (moved.TryGetValue(h.Id, out var f))
            {
                (copy.FacetId, copy.PlaneAMm, copy.PlaneBMm) = (f.FacetId, f.A, f.B);
            }

            return copy;
        }).ToList();
        var wanted = missed.ToDictionary(h => h.Id);
        var extents = facets.ToDictionary(f => f.Key, f => f.Value.Extent);
        return HoldOutlierPlanner.Plan(wall, links, extents).Fixes
            .Where(f => wanted.ContainsKey(f.Hold.Id))
            .Select(f => new SurfaceFix(wanted[f.Hold.Id], f.FacetId, f.PlaneAMm, f.PlaneBMm, f.Size, null, true))
            .ToList();
    }

    /// <summary>The holds grouped by "same hold" links across photos; an unlinked hold is its own group.</summary>
    private static IEnumerable<List<Hold>> Groups(List<Hold> holds, IEnumerable<HoldLinkPair> links)
    {
        var index = holds.Select((h, i) => (h.Id, i)).ToDictionary(p => p.Id, p => p.i);
        var parent = Enumerable.Range(0, holds.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        foreach (var link in links)
        {
            if (index.TryGetValue(link.HoldAId, out var a) && index.TryGetValue(link.HoldBId, out var b)
                && holds[a].WallPanelId != holds[b].WallPanelId)
            {
                parent[Find(a)] = Find(b);
            }
        }

        return Enumerable.Range(0, holds.Count).GroupBy(Find).Select(g => g.Select(i => holds[i]).ToList());
    }

    private static double Distance(IReadOnlyDictionary<string, (FacetFrame Frame, PlaneRectMm Extent)> facets, SurfaceCast x, SurfaceCast y)
    {
        var p = facets[x.FacetId].Frame.ToWorld(x.A, x.B);
        var q = facets[y.FacetId].Frame.ToWorld(y.A, y.B);
        return Math.Sqrt(Enumerable.Range(0, 3).Sum(k => (p[k] - q[k]) * (p[k] - q[k])));
    }
}
