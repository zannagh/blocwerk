// <copyright file="HoldOutlierPlanner.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// Finds placed holds whose 3D position the rest of the wall contradicts, and where they belong — 3D only, the panel
/// geometry is the truth and is never read for anything but the photo centre. Panel photos are checked best first
/// (<see cref="PhotoConsistency"/>). A hold with a "same hold" link to a photo checked before it belongs where that copy
/// is (one physical hold has one position). Otherwise its nearest trusted neighbours on its own photo decide
/// (<see cref="NeighbourPlaneFit"/>): the linked holds' positions when the photo has enough of them, else the photo's
/// own placements. A hold is re-placed when it sits on another facet or further off than the tolerance.
/// </summary>
public static class HoldOutlierPlanner
{
    /// <summary>
    /// A linked copy further off than this is the same hold placed twice: well above what a consistent photo's own
    /// placements disagree by (The Attic's best photo: 99 % within 33 mm).
    /// </summary>
    public const double TwinToleranceMm = 50;

    /// <summary>
    /// A hold further than this from where its neighbours put it is re-placed: above the neighbour estimate's own error
    /// on a consistent photo (The Attic's best photo, each hold left out in turn: 99 % within 66 mm, none beyond 100 mm).
    /// </summary>
    public const double NeighbourToleranceMm = 100;

    /// <summary>Linked holds a photo needs before they, not its own placements, are its neighbours' reference.</summary>
    public const int MinTwinAnchors = 8;

    /// <summary>Checks the wall's placed holds.</summary>
    /// <param name="holds">The wall's live holds (unplaced, virtual and panel-less ones are ignored).</param>
    /// <param name="links">The wall's "same hold" links.</param>
    /// <param name="extents">The active model's facet extents: a neighbour estimate off its facet is not used.</param>
    /// <returns>The holds to re-place and the counts per photo.</returns>
    public static HoldOutlierPlan Plan(
        IEnumerable<Hold> holds, IEnumerable<HoldLinkPair> links, IReadOnlyDictionary<string, PlaneRectMm> extents)
    {
        var placed = holds.Where(IsPlaced).OrderBy(h => h.Id).ToList();
        var twins = TwinsOf(placed, links);
        var photos = placed.GroupBy(h => h.WallPanelId!.Value)
            .Select(g => (Panel: g.Key, Holds: g.ToList(), Score: PhotoConsistency.P90ResidualMm(g)))
            .OrderBy(p => p.Score).ThenBy(p => p.Panel)
            .ToList();
        var position = placed.ToDictionary(h => h.Id, h => (Hold: h, At: new PlaneAnchor(h.X, h.Y, h.FacetId!, h.PlaneAMm!.Value, h.PlaneBMm!.Value)));
        var rank = new Dictionary<Guid, int>();
        var fixes = new List<HoldOutlierFix>();
        var panels = new List<HoldOutlierPanelCount>();
        foreach (var photo in photos)
        {
            var reference = photo.Holds
                .Select(h => (h, Twin: BestTwin(h, twins, rank, position)))
                .Where(p => p.Twin is not null)
                .ToDictionary(p => p.h.Id, p => p.Twin!.Value);
            var (found, undecided) = CheckPhoto(photo.Holds, reference, extents);
            foreach (var fix in found)
            {
                position[fix.Hold.Id] = (fix.Hold, new PlaneAnchor(fix.Hold.X, fix.Hold.Y, fix.FacetId, fix.PlaneAMm, fix.PlaneBMm));
            }

            fixes.AddRange(found);
            rank[photo.Panel] = rank.Count;
            panels.Add(new HoldOutlierPanelCount(
                photo.Panel, Math.Round(photo.Score, 1), photo.Holds.Count, found.Count(f => f.Twin is not null), found.Count(f => f.Twin is null), undecided));
        }

        return new HoldOutlierPlan(fixes, panels);
    }

    /// <summary>Whether a hold has a 3D position this check can judge.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>True for a non-virtual panel hold with a facet and a finite plane position.</returns>
    public static bool IsPlaced(Hold hold) =>
        !hold.IsVirtual && hold.WallPanelId is not null && hold.FacetId is not null
        && hold.PlaneAMm is { } a && double.IsFinite(a) && hold.PlaneBMm is { } b && double.IsFinite(b);

    /// <summary>The fixes of one photo, and how many of its holds could not be judged.</summary>
    private static (List<HoldOutlierFix> Fixes, int Undecided) CheckPhoto(
        List<Hold> holds,
        Dictionary<Guid, (Hold Twin, PlaneAnchor At)> reference,
        IReadOnlyDictionary<string, PlaneRectMm> extents)
    {
        var anchors = reference.Count >= MinTwinAnchors
            ? holds.Where(h => reference.ContainsKey(h.Id)).Select(h => (h.Id, At: reference[h.Id].At with { X = h.X, Y = h.Y })).ToList()
            : holds.Select(h => (h.Id, At: new PlaneAnchor(h.X, h.Y, h.FacetId!, h.PlaneAMm!.Value, h.PlaneBMm!.Value))).ToList();
        var fixes = new List<HoldOutlierFix>();
        var undecided = 0;
        foreach (var hold in holds)
        {
            var expected = reference.TryGetValue(hold.Id, out var twin)
                ? FromTwin(hold, twin.Twin, twin.At)
                : FromNeighbours(hold, anchors.Where(p => p.Id != hold.Id).Select(p => p.At).ToList(), extents);
            if (expected is null)
            {
                undecided++;
            }
            else if (Deviates(hold, expected))
            {
                fixes.Add(expected);
            }
        }

        return (fixes, undecided);
    }

    private static HoldOutlierFix FromTwin(Hold hold, Hold twin, PlaneAnchor at)
    {
        var size = twin.WidthMm is { } w && twin.HeightMm is { } h
            ? new HoldMetric(w, h, twin.AreaMm2 ?? 0, at.FacetId, at.A, at.B, HoldMetric.Consensus)
            : null;
        return new HoldOutlierFix(hold, at.FacetId, at.A, at.B, size, twin);
    }

    private static HoldOutlierFix? FromNeighbours(Hold hold, List<PlaneAnchor> anchors, IReadOnlyDictionary<string, PlaneRectMm> extents)
    {
        if (NeighbourPlaneFit.Predict(hold.X, hold.Y, anchors, NeighbourToleranceMm) is not { } fit)
        {
            return null;
        }

        var (facet, map) = fit;
        var (a, b) = map(hold.X, hold.Y);
        if (!double.IsFinite(a) || !double.IsFinite(b) || !extents.TryGetValue(facet, out var e)
            || a < e.AMin - HoldTexturePlacer.ExtentMarginMm || a > e.AMax + HoldTexturePlacer.ExtentMarginMm
            || b < e.BMin - HoldTexturePlacer.ExtentMarginMm || b > e.BMax + HoldTexturePlacer.ExtentMarginMm)
        {
            return null;
        }

        var size = HoldFitMeasurer.Measure(hold, new HoldPlaneFit(facet, a, b, map, Wall3DShapeSource.HoldFit)) is { WidthMm: > 0, HeightMm: > 0 } m
            ? m with { MetricSource = HoldMetric.Consensus }
            : null;
        return new HoldOutlierFix(hold, facet, a, b, size, null);
    }

    private static bool Deviates(Hold hold, HoldOutlierFix expected)
    {
        if (!string.Equals(hold.FacetId, expected.FacetId, StringComparison.Ordinal))
        {
            return true;
        }

        var (da, db) = (hold.PlaneAMm!.Value - expected.PlaneAMm, hold.PlaneBMm!.Value - expected.PlaneBMm);
        var tolerance = expected.Twin is null ? NeighbourToleranceMm : TwinToleranceMm;
        return Math.Sqrt((da * da) + (db * db)) > tolerance;
    }

    /// <summary>The hold's linked copy on the best-ranked photo checked before it, with that copy's (possibly fixed) position.</summary>
    private static (Hold Twin, PlaneAnchor At)? BestTwin(
        Hold hold,
        Dictionary<Guid, List<Guid>> twins,
        Dictionary<Guid, int> rank,
        Dictionary<Guid, (Hold Hold, PlaneAnchor At)> position)
    {
        if (!twins.TryGetValue(hold.Id, out var ids))
        {
            return null;
        }

        var best = ids.Select(id => position[id])
            .Where(p => rank.ContainsKey(p.Hold.WallPanelId!.Value))
            .OrderBy(p => rank[p.Hold.WallPanelId!.Value]).ThenBy(p => p.Hold.Id)
            .FirstOrDefault();
        return best.Hold is null ? null : best;
    }

    /// <summary>Each placed hold's linked copies on OTHER photos (a link within one photo is a mistake and ignored).</summary>
    private static Dictionary<Guid, List<Guid>> TwinsOf(List<Hold> placed, IEnumerable<HoldLinkPair> links)
    {
        var byId = placed.ToDictionary(h => h.Id);
        var twins = new Dictionary<Guid, List<Guid>>();
        foreach (var link in links)
        {
            if (!byId.TryGetValue(link.HoldAId, out var a) || !byId.TryGetValue(link.HoldBId, out var b) || a.WallPanelId == b.WallPanelId)
            {
                continue;
            }

            Add(twins, a.Id, b.Id);
            Add(twins, b.Id, a.Id);
        }

        return twins;
    }

    private static void Add(Dictionary<Guid, List<Guid>> twins, Guid from, Guid to)
    {
        if (!twins.TryGetValue(from, out var list))
        {
            twins[from] = list = [];
        }

        list.Add(to);
    }
}
