// <copyright file="CapturePlanLayoutCheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Per photo: do the detected markers sit where the wall's marker plan says, relative to each other? DICT_4X4_50
/// decodes ids on holds, and the same hold decodes the same id from neighbouring photos, so such a false marker is
/// multi-view consistent and the solver cannot reject it (The Attic, 2026-09-29: a ring hold as id 17, a purple
/// hold as id 37 wrecked the model). Checked per plan segment (one plane), only between markers of that segment:
/// <list type="bullet">
/// <item>≥ 3 markers: a flat surface photographed from the front is never mirrored, so every well-shaped triangle
/// of markers must keep its plan orientation; a marker that flips two or more triangles is ignored.</item>
/// <item>≥ 5 markers: one homography per segment (all 4-marker subsets, plan centre → image centre); a marker
/// its consensus places far from the plan, or at a size the homography's local scale contradicts, is ignored.</item>
/// <item>≥ 3 markers with a known focal length: the depth each marker's apparent size implies cannot differ from
/// another's by more than the plan distance between them allows.</item>
/// </list>
/// Tolerances are wide: owners place markers only roughly where planned (The Attic: up to ~500 mm off). Ties are
/// left alone (the solver's own rejection judges those). Only a whole marker is ever ignored, never the photo.
/// </summary>
public static partial class CapturePlanLayoutCheck
{
    /// <summary>The stored reason (<see cref="CaptureMarker.Ignored"/>) and the solver-notes reason.</summary>
    public const string Reason = Geometry.WallGeometryRejectedObservation.PlanLayout;

    /// <summary>Checks one photo's detections against a plan layout; everything passes without a plan.</summary>
    /// <param name="layout">The wall's marker layout.</param>
    /// <param name="markers">The photo's detections (already validated by the detector).</param>
    /// <param name="focalPx">The photo's focal length in px, when known (enables the size/depth check).</param>
    public static PlanLayoutVerdict Apply(WallMarkerLayout layout, IReadOnlyList<CaptureMarker> markers, double? focalPx)
    {
        if (!layout.IsFromPlan || markers.Count < 3)
        {
            return new PlanLayoutVerdict(markers, []);
        }

        var ignored = new List<IgnoredCaptureMarker>();
        var views = markers.Select(m => PlanLayoutView.Of(layout, m)).OfType<PlanLayoutView>();
        foreach (var segment in views.GroupBy(v => v.Segment).OrderBy(g => g.Key))
        {
            var members = segment.OrderBy(v => v.Marker.Id).ToList();
            ignored.AddRange(CheckOrientation(members));
            ignored.AddRange(CheckHomography(members));
            if (focalPx is > 0)
            {
                ignored.AddRange(CheckDepth(members, focalPx.Value));
            }
        }

        var drop = ignored.Select(i => i.Marker).ToHashSet(ReferenceEqualityComparer.Instance);
        return new PlanLayoutVerdict(markers.Where(m => !drop.Contains(m)).ToList(), ignored);
    }

    /// <summary>The focal length in px of a photo from its 35 mm equivalent, or null.</summary>
    /// <param name="focal35mm">The EXIF 35 mm equivalent focal length.</param>
    /// <param name="width">Image width, px.</param>
    /// <param name="height">Image height, px.</param>
    public static double? FocalPx(double? focal35mm, int width, int height) =>
        focal35mm is > 0 ? focal35mm.Value / 36.0 * Math.Max(width, height) : null;

    internal static string Ids(IEnumerable<PlanLayoutView> views)
    {
        var ids = views.Select(v => v.Marker.Id).Distinct().Order().ToList();
        return ids.Count == 1 ? $"marker {ids[0]}" : $"markers {string.Join(", ", ids.Take(ids.Count - 1))} and {ids[^1]}";
    }

    /// <summary>The single member with the highest count, when that count reaches <paramref name="min"/>; else null.</summary>
    private static PlanLayoutView? UniqueWorst(Dictionary<PlanLayoutView, int> counts, int min)
    {
        var ranked = counts.Where(p => p.Value >= min).OrderByDescending(p => p.Value).ToList();
        return ranked.Count == 0 || (ranked.Count > 1 && ranked[1].Value == ranked[0].Value) ? null : ranked[0].Key;
    }
}
