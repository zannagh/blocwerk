// <copyright file="CarryEvidence.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Registration;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// Checks a carried placement (<see cref="PlacementCarrier"/>) against what the current run knows about the hold
/// without it: the photo's own registration of the carried facet (the hold's centre mapped through it), and the
/// holds linked to it that another photo placed (world distance). A carried placement is only as good as the
/// earlier placement it came from; one the current evidence contradicts is not carried. Pure.
/// </summary>
public static class CarryEvidence
{
    /// <summary>Largest disagreement with any evidence a carried placement may have, mm (hold parallax and fit error stay well below).</summary>
    public const double MaxDisagreementMm = 100;

    /// <summary>
    /// The largest disagreement between the carried placement and the evidence, mm, or null when there is none.
    /// A registration of the carried facet that sees the hold's centre beyond its horizon disagrees infinitely.
    /// </summary>
    /// <param name="x">The hold's normalised photo x.</param>
    /// <param name="y">The hold's normalised photo y.</param>
    /// <param name="carried">The carried placement: facet and plane (a, b), mm, on the active model.</param>
    /// <param name="frames">The active model's facet frames.</param>
    /// <param name="registrations">The photo's registrations in this run (rejected ones are ignored).</param>
    /// <param name="twins">Placements of the linked holds made by other photos in this run.</param>
    /// <returns>The disagreement, mm.</returns>
    public static double? Disagreement(
        double x,
        double y,
        (string FacetId, double A, double B) carried,
        IReadOnlyDictionary<string, FacetFrame> frames,
        IEnumerable<FacetRegistration> registrations,
        IEnumerable<(string FacetId, double A, double B)> twins)
    {
        double? worst = null;
        foreach (var r in registrations.Where(r => r.Accepted && r.PhotoToPlane is not null && r.FacetId == carried.FacetId))
        {
            var (a, b) = r.Map(x, y);
            var d = double.IsFinite(a) && double.IsFinite(b) ? Math.Sqrt(Sq(a - carried.A) + Sq(b - carried.B)) : double.PositiveInfinity;
            worst = Math.Max(worst ?? 0, d);
        }

        if (frames.TryGetValue(carried.FacetId, out var frame))
        {
            var world = frame.ToWorld(carried.A, carried.B);
            foreach (var twin in twins)
            {
                if (frames.TryGetValue(twin.FacetId, out var twinFrame))
                {
                    worst = Math.Max(worst ?? 0, Vec3.Distance(world, twinFrame.ToWorld(twin.A, twin.B)));
                }
            }
        }

        return worst;
    }

    /// <summary>Whether a disagreement allows carrying (no evidence allows it).</summary>
    /// <param name="disagreement">From <see cref="Disagreement"/>.</param>
    /// <returns>True when it is at most <see cref="MaxDisagreementMm"/> or unknown.</returns>
    public static bool Allows(double? disagreement) => disagreement is not > MaxDisagreementMm;

    private static double Sq(double v) => v * v;
}
