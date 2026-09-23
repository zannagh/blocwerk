// <copyright file="MarkerPlanValidator.Print.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Print options: mounting-hole sizes, holes too tight to detect well, and the screw-bias hint when there are none.</summary>
public static partial class MarkerPlanValidator
{
    private static void CheckPrint(MarkerPlan plan, List<PlanIssue> issues)
    {
        if (plan.Print?.MountingHoles is { } holes)
        {
            // Checked even while switched off: the sizes ride in the JSON and come back when ticked.
            foreach (var problem in holes.Problems())
            {
                issues.Add(Error("mounting-holes", problem));
            }
        }

        // The layout grows the cut-out (and, if needed, the paper) to fit any allowed head, so there
        // is no "doesn't fit" case to report — only detection advice.
        if (MarkerPlanPdfLayout.Holes(plan) is { } printed && plan.Markers.Count > 0)
        {
            CheckTightHoles(plan, printed, issues);
        }

        if (plan.Markers.Count > 0 && plan.Print?.HolesEnabled != true)
        {
            issues.Add(Warning(
                "tip-screw-bias",
                "Tip: screws near the black square can shift detected corners — use mounting holes (under Printing) or tape."));
        }
    }

    /// <summary>
    /// Warns when the holes sit too tight for any marker, naming the worst one (sizes in the owner's photos)
    /// and the gaps that measured clean for all of them (see <see cref="MountingHoleSafety"/>).
    /// </summary>
    private static void CheckTightHoles(MarkerPlan plan, MountingHoles holes, List<PlanIssue> issues)
    {
        var tight = plan.Markers
            .Where(m => double.IsFinite(m.SizeMm) && m.SizeMm > 0)
            .Select(m => (Marker: m, PxPerMm: PhotoPxPerMm(plan, m)))
            .Where(t => !MountingHoleSafety.IsSafe(t.Marker.SizeMm, holes, t.PxPerMm))
            .ToList();
        if (tight.Count == 0)
        {
            return;
        }

        var (worst, pxPerMm) = tight.MaxBy(t => t.Marker.SizeMm);
        var size = worst.SizeMm;
        var border = MountingHoleLayout.BorderMm(holes);
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"The mounting holes are tight for the {size:0} mm markers: the white border is {border:0.#} mm (≈{border * pxPerMm:0.#} px in your photos). In tests, a border under ~{MountingHoleSafety.MinBorderPx:0} photo px ({MountingHoleSafety.MinBorderMm(size, pxPerMm):0.#} mm here) let the detected corners drift toward the wall by up to 1.5 px.");
        MountingHoles? safe = holes;
        foreach (var (marker, px) in tight)
        {
            safe = safe is null ? null : MountingHoleSafety.SafeGaps(marker.SizeMm, safe, px);
        }

        message += safe is not null
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" Gap to marker {safe.GapToMarkerMm:0.#} mm and gap to cut edge {safe.GapToEdgeMm:0.#} mm measured clean (cut-out {size + (2 * MountingHoleLayout.BorderMm(safe)):0} mm for the {size:0} mm markers).")
            : string.Create(CultureInfo.InvariantCulture, $" Keep at least {MountingHoleSafety.MinBorderMm(size, pxPerMm):0} mm of white paper around the square (cut outside the printed line), or tape these markers instead.");
        issues.Add(Warning("mounting-holes-tight", message, worst.Segment, worst.Id));
    }

    /// <summary>Photo px per mm on the marker's surface at the plan's distance (face-on when the surface is unknown).</summary>
    private static double PhotoPxPerMm(MarkerPlan plan, PlanMarker marker)
    {
        var segment = plan.Segments.FirstOrDefault(s => s.Index == marker.Segment);
        var px = segment is null ? MarkerSizing.PxPerMm(plan.Photo) : MarkerSizing.EstimatedPx(1, segment, plan.Photo);
        return double.IsFinite(px) && px > 0 ? px : 0;
    }
}
