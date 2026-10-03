// <copyright file="HoldTexturePlacementService.Overlap.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>One accepted facet registration of a photo, with where its inliers support it.</summary>
/// <param name="Registration">The registration.</param>
/// <param name="Support">Its inlier support, or null when its inliers are unknown.</param>
internal sealed record PhotoEvidence(FacetRegistration Registration, InlierSupport? Support);

/// <summary>
/// Overlapping facets: a hold can land inside two facet extents of one photo (a separate piece in front of a large
/// wall facet), and the photo's two registrations then each place it. <see cref="HoldTexturePlacer.Place"/> picks by
/// facet and inlier count alone; here the two positions are compared, and when they disagree the registration with
/// the stronger evidence AT THE HOLD (<see cref="TwinConsistency.Score"/>) places it, or neither when it is not clear.
/// </summary>
public sealed partial class HoldTexturePlacementService
{
    /// <summary>The placement the photo's evidence favours, with its side; marked unsupported when two registrations disagree without a winner.</summary>
    private CheckedPlacement Reconcile(int plan, PlannedPlacement p, Dictionary<string, PhotoEvidence> evidence, ActiveModel model, string label)
    {
        var size = p.Metric is { } m ? Math.Max(m.WidthMm, m.HeightMm) : 0;
        var current = Side(p.Fit, size, evidence[p.Fit.FacetId], model.Frames[p.Fit.FacetId]);
        var contested = false;
        foreach (var e in evidence.Values.Where(e => e.Registration.FacetId != p.Fit.FacetId))
        {
            if (HoldTexturePlacer.On(p.Hold, e.Registration) is not { } fit)
            {
                continue;
            }

            var other = Side(fit, size, e, model.Frames[fit.FacetId]);
            var verdict = TwinConsistency.Judge(current, other);
            if (verdict is TwinVerdict.Agree or TwinVerdict.SecondWrong)
            {
                continue;
            }

            logger.LogInformation(
                "Panel {Panel}: hold {HoldId} is placed {Distance:F0} mm apart by facet {Facet} (score {Score:F0}) and facet {Other} "
                + "(score {OtherScore:F0}): {Verdict}",
                label, p.Hold.Id, TwinConsistency.Distance(current, other), p.Fit.FacetId, TwinConsistency.Score(current),
                fit.FacetId, TwinConsistency.Score(other), verdict == TwinVerdict.FirstWrong ? $"facet {fit.FacetId} places it" : "neither");
            if (verdict == TwinVerdict.BothWrong)
            {
                contested = true;
                continue;
            }

            p = new PlannedPlacement(p.Hold, fit, HoldTexturePlacer.Measure(p.Hold, fit));
            current = other;
        }

        var beyond = evidence[p.Fit.FacetId].Support?.IsExtrapolated(p.Fit.PlaneAMm, p.Fit.PlaneBMm) ?? false;
        return new CheckedPlacement(plan, p, current, beyond || contested);
    }

    /// <summary>A placement as one side of a comparison: its world position and the evidence behind it.</summary>
    private static TwinSide Side(HoldPlaneFit fit, double size, PhotoEvidence e, FacetFrame frame)
    {
        var (a, b) = (fit.PlaneAMm, fit.PlaneBMm);
        return new TwinSide(frame.ToWorld(a, b), size, e.Registration, e.Support?.NearestInlierMm(a, b) ?? 0, e.Support?.BeyondHullMm(a, b) ?? 0);
    }
}
