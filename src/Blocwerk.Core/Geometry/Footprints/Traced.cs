// <copyright file="Traced.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>
/// One hold being refined (<see cref="HoldFootprintRefiner"/>): the plane its silhouettes are traced on (its facet,
/// or the tangent plane of the volume it is placed on), its panel silhouette there, its centre in that plane and
/// the volumes that may hide it from a capture view.
/// </summary>
/// <param name="Hold">The hold.</param>
/// <param name="Frame">The tracing plane.</param>
/// <param name="View">The panel photo's silhouette on it.</param>
/// <param name="Centre">The hold's centre on it.</param>
/// <param name="Volumes">Its facet's volumes, or null.</param>
/// <param name="OnVolume">Whether <see cref="Frame"/> is a volume's tangent plane.</param>
internal sealed record Traced(Hold Hold, FacetFrame Frame, FootprintView View, (double A, double B) Centre, FacetVolumes? Volumes, bool OnVolume)
{
    /// <summary>The hold on its facet, or on its volume when it has a current placement there.</summary>
    /// <param name="hold">The hold.</param>
    /// <param name="facet">Its facet.</param>
    /// <param name="panel">Its panel silhouette on the facet (flat, as mapped).</param>
    /// <param name="volumes">Its facet's volumes, or null.</param>
    /// <returns>The traced hold.</returns>
    public static Traced Of(Hold hold, FacetFrame facet, FootprintView panel, FacetVolumes? volumes)
    {
        var flat = new Traced(hold, facet, panel, (hold.PlaneAMm!.Value, hold.PlaneBMm!.Value), volumes, false);
        if (volumes is null || HoldVolumePlacement.FromJson(hold.VolumePlacementJson) is not { } p || !p.Matches(hold.PlaneAMm, hold.PlaneBMm)
            || VolumeFootprints.TangentFrame(facet, p) is not { } tangent)
        {
            return flat;
        }

        // Walk each flat silhouette point back along the panel camera's ray onto the tangent plane.
        var ring = panel.Silhouette.Select(q => VolumeFootprints.ToPlane(tangent, panel.CameraMm, facet.ToWorld(q.A, q.B))).ToList();
        return ring.All(q => q is not null)
            ? new Traced(hold, tangent, new FootprintView(ring.Select(q => q!.Value).ToList(), panel.CameraMm, panel.Label), (0, 0), volumes, true)
            : flat;
    }
}
