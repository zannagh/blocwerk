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
    /// Warns when the holes leave too thin a white border in the owner's photos (see <see cref="MountingHoleSafety"/>):
    /// <c>mounting-holes-tight</c> under <see cref="MountingHoleSafety.MinBorderPx"/> (markers drop out on any
    /// wall), else <c>mounting-holes-thin</c> under <see cref="MountingHoleSafety.RecommendedBorderPx"/> (fine on
    /// light plywood, not on dark holds or volumes). Names the thinnest marker and the gaps that fix all of them.
    /// </summary>
    private static void CheckTightHoles(MarkerPlan plan, MountingHoles holes, List<PlanIssue> issues)
    {
        var thin = plan.Markers
            .Where(m => double.IsFinite(m.SizeMm) && m.SizeMm > 0)
            .Select(m => (Marker: m, PxPerMm: PhotoPxPerMm(plan, m)))
            .Select(t => (t.Marker, t.PxPerMm, Level: MountingHoleSafety.Assess(t.Marker.SizeMm, holes, t.PxPerMm)))
            .Where(t => t.Level != MountingHoleBorder.Clear)
            .ToList();
        if (thin.Count == 0)
        {
            return;
        }

        var (worst, pxPerMm, level) = thin.MinBy(t => MountingHoleSafety.BorderPx(t.Marker.SizeMm, holes, t.PxPerMm));
        var size = worst.SizeMm;
        var border = MountingHoleLayout.BorderMm(holes);
        var borderPx = MountingHoleSafety.BorderPx(size, holes, pxPerMm);
        var tooThin = level == MountingHoleBorder.TooThin;
        var message = tooThin
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"The mounting holes are too tight for the {size:0} mm markers: the white border is {border:0.#} mm, only ≈{borderPx:0.#} px in your photos. On real photos, markers with under {MountingHoleSafety.MinBorderPx:0} px of white border were found in only 40–95 % of the photos, on the light wall too (far fewer under 1 px), and their corners were twice as noisy.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"The white border around the {size:0} mm markers is {border:0.#} mm, ≈{borderPx:0.#} px in your photos. That is fine on the light wall (every real marker there was found from {MountingHoleSafety.MinBorderPx:0} px on), but on a dark hold or volume, where the paper is the only contrast, markers with under {MountingHoleSafety.RecommendedBorderPx:0} px were found only 75–88 % of the time (92–100 % from {MountingHoleSafety.RecommendedBorderPx:0} px).");
        message += Remedy(thin.Select(t => (t.Marker.SizeMm, t.PxPerMm)).ToList(), holes, size, pxPerMm, tooThin);
        issues.Add(Warning(tooThin ? "mounting-holes-tight" : "mounting-holes-thin", message, worst.Segment, worst.Id));
    }

    /// <summary>The gaps that reach the recommended border for every listed marker (else, when too thin, the minimum; else tape).</summary>
    private static string Remedy(List<(double SizeMm, double PxPerMm)> markers, MountingHoles holes, double size, double pxPerMm, bool tooThin)
    {
        double[] targets = tooThin ? [MountingHoleSafety.RecommendedBorderPx, MountingHoleSafety.MinBorderPx] : [MountingHoleSafety.RecommendedBorderPx];
        foreach (var target in targets)
        {
            MountingHoles? safe = holes;
            foreach (var (sizeMm, px) in markers)
            {
                safe = safe is null ? null : MountingHoleSafety.SafeGaps(sizeMm, safe, px, target);
            }

            if (safe is not null)
            {
                var cutOut = size + (2 * MountingHoleLayout.BorderMm(safe));
                return tooThin
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $" Gap to marker {safe.GapToMarkerMm:0.#} mm and gap to cut edge {safe.GapToEdgeMm:0.#} mm give at least {target:0} px (cut-out {cutOut:0} mm for the {size:0} mm markers).")
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $" If any of these markers sit on dark holds or volumes, gap to marker {safe.GapToMarkerMm:0.#} mm and gap to cut edge {safe.GapToEdgeMm:0.#} mm give {target:0} px (cut-out {cutOut:0} mm for the {size:0} mm markers); otherwise keep the tighter gaps.");
            }
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $" No allowed gap gets there: keep at least {MountingHoleSafety.RecommendedBorderMm(size, pxPerMm):0} mm of white paper around the square (cut outside the printed line), tape these markers instead, or photograph from closer.");
    }

    /// <summary>Photo px per mm on the marker's surface at the plan's distance (face-on when the surface is unknown).</summary>
    private static double PhotoPxPerMm(MarkerPlan plan, PlanMarker marker)
    {
        var segment = plan.Segments.FirstOrDefault(s => s.Index == marker.Segment);
        var px = segment is null ? MarkerSizing.PxPerMm(plan.Photo) : MarkerSizing.EstimatedPx(1, segment, plan.Photo);
        return double.IsFinite(px) && px > 0 ? px : 0;
    }
}
