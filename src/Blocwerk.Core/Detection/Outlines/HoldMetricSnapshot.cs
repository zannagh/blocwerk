using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>The metric fields of a hold, as they were before a run replaced them.</summary>
/// <param name="WidthMm">Width in mm.</param>
/// <param name="HeightMm">Height in mm.</param>
/// <param name="AreaMm2">Area in mm².</param>
/// <param name="FacetId">Facet.</param>
/// <param name="PlaneAMm">Plane position a.</param>
/// <param name="PlaneBMm">Plane position b.</param>
/// <param name="MetricSource">Metric source.</param>
public sealed record HoldMetricSnapshot(
    double? WidthMm, double? HeightMm, double? AreaMm2, string? FacetId, double? PlaneAMm, double? PlaneBMm, string? MetricSource)
{
    /// <summary>Captures a hold's metric fields.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>The snapshot.</returns>
    public static HoldMetricSnapshot Of(Hold hold) =>
        new(hold.WidthMm, hold.HeightMm, hold.AreaMm2, hold.FacetId, hold.PlaneAMm, hold.PlaneBMm, hold.MetricSource);

    /// <summary>Writes the snapshot back onto a hold.</summary>
    /// <param name="hold">The hold.</param>
    public void RestoreTo(Hold hold)
    {
        hold.WidthMm = WidthMm;
        hold.HeightMm = HeightMm;
        hold.AreaMm2 = AreaMm2;
        hold.FacetId = FacetId;
        hold.PlaneAMm = PlaneAMm;
        hold.PlaneBMm = PlaneBMm;
        hold.MetricSource = MetricSource;
    }
}
