// <copyright file="PlanMarkerEdits.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>The planner's marker edits: add, move (also onto another surface), resize, re-role, delete.</summary>
public static class PlanMarkerEdits
{
    /// <summary>Size of a hand-added marker on a surface that has none to copy from.</summary>
    public const double DefaultSizeMm = 100;

    /// <summary>The lowest id not yet used (the dictionary has <see cref="ArucoDict4X4.Count"/>).</summary>
    public static int NextFreeId(MarkerPlan plan)
    {
        var used = plan.Markers.Select(m => m.Id).ToHashSet();
        var id = 0;
        while (used.Contains(id))
        {
            id++;
        }

        return id;
    }

    /// <summary>
    /// Adds a filler at a (snapped) point of <paramref name="segment"/>, sized like the fillers already
    /// on that surface so a hand-placed marker matches its neighbours.
    /// </summary>
    public static MarkerPlan Add(MarkerPlan plan, int segment, PlanVector local, out int id)
    {
        id = NextFreeId(plan);
        var onSegment = plan.Markers.Where(m => m.Segment == segment).ToList();
        var size = onSegment.Where(m => m.Role == MarkerRole.Filler).Select(m => m.SizeMm).DefaultIfEmpty(0).Min();
        if (size <= 0)
        {
            size = onSegment.Select(m => m.SizeMm).DefaultIfEmpty(DefaultSizeMm).Min();
        }

        var snapped = PlannerGeometry.Snap(local);
        var marker = new PlanMarker(id, segment, snapped.X, snapped.Y, size, MarkerRole.Filler);
        return plan with { Markers = [.. plan.Markers, marker] };
    }

    /// <summary>
    /// Moves a marker to a net-space point: onto whichever surface is under it (its own when the drop
    /// missed every surface), converted into that surface's frame and snapped.
    /// </summary>
    public static MarkerPlan MoveToNet(MarkerPlan plan, NetGeometry net, int id, double netX, double netY)
    {
        var marker = plan.Markers.FirstOrDefault(m => m.Id == id);
        if (marker is null)
        {
            return plan;
        }

        var target = PlannerGeometry.SegmentAt(net, netX, netY)
                     ?? net.Segments.FirstOrDefault(s => s.Index == marker.Segment);
        if (target is null)
        {
            return plan;
        }

        var local = PlannerGeometry.Snap(PlannerGeometry.NetToSegment(target, netX, netY));
        return Replace(plan, marker with { Segment = target.Index, XMm = local.X, YMm = local.Y });
    }

    /// <summary>Replaces the marker with the same id.</summary>
    public static MarkerPlan Replace(MarkerPlan plan, PlanMarker updated) => Replace(plan, updated.Id, updated);

    /// <summary>Replaces the marker that had id <paramref name="oldId"/> (its id may change; duplicates are the validator's to report).</summary>
    public static MarkerPlan Replace(MarkerPlan plan, int oldId, PlanMarker updated) =>
        plan with { Markers = plan.Markers.Select(m => m.Id == oldId ? updated : m).ToList() };

    /// <summary>Removes a marker.</summary>
    public static MarkerPlan Delete(MarkerPlan plan, int id) =>
        plan with { Markers = plan.Markers.Where(m => m.Id != id).ToList() };

    /// <summary>The on-photo target for the marker's role (corners need more pixels than fillers).</summary>
    public static double TargetPx(MarkerRole role, MarkerGenerationOptions options) =>
        role == MarkerRole.Corner ? options.CornerTargetPx : options.FillerTargetPx;
}
