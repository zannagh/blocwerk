// <copyright file="WallBigUpdateService.CarryFields.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

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
    /// Copies the curated (user-set) fields from the old hold onto its successor row, leaving the
    /// successor's own detected position and shape untouched. Virtual only carries forward from a
    /// virtual predecessor; a real detection is never demoted to virtual.
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
        if (from.IsVirtual)
        {
            to.IsVirtual = true;
        }
    }

    /// <summary>
    /// Carries the old hold's 3D placement and metric fields onto a re-found twin, following the rules in
    /// <c>Hold.Glyph.cs</c>: the same physical hold keeps its facet position, sizes, footprint, protrusion
    /// and volume placement. What the staged enrichment measured on the new photo wins, field group by
    /// field group, so only the gaps are filled. A <paramref name="changed"/> hold is a physically
    /// different hold at the same spot: it keeps the position (facet, plane, volume placement) but not the
    /// old hold's sizes, footprint or protrusion. Without this a re-found hold dropped out of the 3D view
    /// until the next placement run, which the wizard promote never starts.
    /// </summary>
    private static void CopyPlacementFields(Hold from, Hold to, bool changed)
    {
        if (to.FacetId is null && from.FacetId is not null)
        {
            to.FacetId = from.FacetId;
            to.PlaneAMm = from.PlaneAMm;
            to.PlaneBMm = from.PlaneBMm;
            to.MetricSource = from.MetricSource;
        }

        to.VolumePlacementJson ??= from.VolumePlacementJson;
        if (changed)
        {
            return;
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
