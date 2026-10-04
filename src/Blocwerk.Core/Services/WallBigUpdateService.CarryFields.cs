// <copyright file="WallBigUpdateService.CarryFields.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// What a re-found ("twin") successor inherits from the old hold it continues. A clone gets everything
/// (<see cref="Hold.Clone"/>); a twin is a fresh detection on the new photo, so it keeps its own position
/// and outline and takes the old hold's curation, and — because the camera moving does not move the hold —
/// its wall placement and measurements wherever the new photo did not measure them itself.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// The widest distance, in photo pixels, at which the overlap matcher accepts a pair: its residual gate
    /// (90 px) doubled for anchor-seeded pairs (<c>OpenCvHoldOverlapMatcher</c>). The matcher does not report
    /// the bound it used per pair, so the placement check uses the widest one.
    /// </summary>
    private const double MatcherGatePx = 180.0;

    /// <summary>
    /// Copies the curated (user-set) fields from the old hold onto its successor row, leaving the
    /// successor's own detected position and shape untouched. A real detection is never demoted to
    /// virtual: a virtual (or hand-placed) old hold that lands on a detection becomes a real hold.
    /// </summary>
    private static void CopyCuratedFields(Hold from, Hold to)
    {
        to.Name = from.Name;
        to.Color = from.Color;
        to.Material = from.Material;
        to.Category = from.Category;
        to.HandType = from.HandType;
        to.IsOnKickboard = from.IsOnKickboard;
        to.IsAutoDetected = from.IsAutoDetected;
    }

    /// <summary>
    /// Carries the old hold's 3D placement and metric fields onto a re-found twin, following the rules in
    /// <c>Hold.Glyph.cs</c>: the same physical hold keeps its facet position, sizes, footprint, protrusion
    /// and volume placement. What the staged enrichment measured on the new photo wins, field group by
    /// field group, so only the gaps are filled. Without this a re-found hold dropped out of the 3D view
    /// until the next placement run, which the wizard promote never starts.
    /// <para>
    /// The old wall position is only valid where the twin sits where the old hold was. A
    /// <paramref name="changed"/> verdict (a different hold, or an accepted "this hold moved" relocation)
    /// copies nothing, and neither does a twin that is not on the old hold's warp-predicted spot
    /// (<paramref name="warped"/>): a new 2D position with the old 3D placement is never corrected for
    /// marker or hold-fit placements, and it later makes the 3D triage discard a real new hold at the old
    /// spot. With no warp prediction the matcher's pairing is all there is, and it is trusted. A twin whose
    /// old position is withheld is flagged <see cref="Hold.NeedsReview"/> so it is not silently unplaced.
    /// </para>
    /// </summary>
    private static void CopyPlacementFields(
        Hold from, Hold to, bool changed, HoldPositionNorm? warped, (int Width, int Height)? photoSize)
    {
        if (changed)
        {
            return;
        }

        if (warped is null || IsOnWarpedSpot(to, warped, photoSize))
        {
            CopyWallPosition(from, to);
        }
        else if (from.FacetId is not null && to.FacetId is null)
        {
            to.NeedsReview = true;
        }

        if (to.WidthMm is null && to.HeightMm is null && to.AreaMm2 is null)
        {
            to.WidthMm = from.WidthMm;
            to.HeightMm = from.HeightMm;
            to.AreaMm2 = from.AreaMm2;
        }

        to.FootprintMm ??= from.FootprintMm;
        to.ProtrusionMm ??= from.ProtrusionMm;
    }

    /// <summary>
    /// The facet position and volume placement, as a group, when the twin has none of its own. A twin the
    /// marker pass sized in its own marker square (<see cref="HoldMetric.LocalMarker"/>) keeps that source:
    /// it describes the size the twin already has.
    /// </summary>
    private static void CopyWallPosition(Hold from, Hold to)
    {
        if (to.FacetId is null && from.FacetId is not null)
        {
            to.FacetId = from.FacetId;
            to.PlaneAMm = from.PlaneAMm;
            to.PlaneBMm = from.PlaneBMm;
            if (to.MetricSource != HoldMetric.LocalMarker)
            {
                to.MetricSource = from.MetricSource;
            }
        }

        to.VolumePlacementJson ??= from.VolumePlacementJson;
    }

    /// <summary>
    /// Whether the twin sits on the old hold's warp-predicted spot. Compared in pixels of the twin's photo:
    /// X and Y are normalized per side while the radius is normalized to the longest side, so a normalized
    /// distance shrinks the window on a landscape photo. The window is never tighter than the matcher's own
    /// widest acceptance (<see cref="MatcherGatePx"/>), so a pair the matcher accepted is not second-guessed
    /// here. Without a decodable photo size it falls back to the normalized radius window.
    /// </summary>
    private static bool IsOnWarpedSpot(Hold twin, HoldPositionNorm warped, (int Width, int Height)? photoSize)
    {
        var radiusWindow = Math.Max(twin.Radius, MinWarpedShapeRadius) * WarpedShapeRadiusFactor;
        if (photoSize is not { } size)
        {
            var nx = warped.X - twin.X;
            var ny = warped.Y - twin.Y;
            return (nx * nx) + (ny * ny) <= radiusWindow * radiusWindow;
        }

        var dx = (warped.X - twin.X) * size.Width;
        var dy = (warped.Y - twin.Y) * size.Height;
        var window = Math.Max(radiusWindow * Math.Max(size.Width, size.Height), MatcherGatePx);
        return (dx * dx) + (dy * dy) <= window * window;
    }

    /// <summary>
    /// A hand-placed or virtual hold merged onto a detection keeps what a person set that the detection cannot know: its wall
    /// position (with its volume placement) and, when the detection has no traced contour, its hand-drawn outline.
    /// </summary>
    private static void CopyHandSetFields(Hold from, Hold to)
    {
        CopyWallPosition(from, to);
        if (to.ShapePoints is not { Count: >= 3 } && from.ShapePoints is { Count: >= 3 })
        {
            to.ShapePoints = from.ShapePoints.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList();
            to.ShapeHoles = from.ShapeHoles;
            to.OutlineSource = from.OutlineSource;
            to.OutlineConfidence = from.OutlineConfidence;
        }
    }

    /// <summary>
    /// A second old hold claiming a twin the first one already claimed (a physical merge). The first
    /// writer's curation stays, but the merged hold must not lose what the second one stood for: it is
    /// hand-added if either was, it needs review if either did (or the claim says "changed"), and curated
    /// fields the first left empty are filled from the second.
    /// </summary>
    private static void MergeCuratedFields(Hold from, Hold to, bool changed)
    {
        to.IsAutoDetected &= from.IsAutoDetected;
        to.NeedsReview |= changed || from.NeedsReview;
        to.IsOnKickboard |= from.IsOnKickboard;
        to.Name ??= from.Name;
        to.Color ??= from.Color;
        to.Material ??= from.Material;
        to.HandType ??= from.HandType;
    }
}
