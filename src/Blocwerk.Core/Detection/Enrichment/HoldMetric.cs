namespace Blocwerk.Core.Detection.Enrichment;

/// <summary>What the marker pass measured for one hold.</summary>
/// <param name="WidthMm">Extent along the plane's a (u) axis, in mm.</param>
/// <param name="HeightMm">Extent along the plane's b (v) axis, in mm.</param>
/// <param name="AreaMm2">Outline area on the plane, in mm².</param>
/// <param name="FacetId">The facet the hold sits on; null for a local marker frame.</param>
/// <param name="PlaneAMm">Hold centre along the facet's u axis; null for a local marker frame.</param>
/// <param name="PlaneBMm">Hold centre along the facet's v axis; null for a local marker frame.</param>
/// <param name="MetricSource">"multi-marker", "single-marker" or "local-marker".</param>
public sealed record HoldMetric(
    double WidthMm,
    double HeightMm,
    double AreaMm2,
    string? FacetId,
    double? PlaneAMm,
    double? PlaneBMm,
    string MetricSource)
{
    /// <summary>Metric source of a facet fit over two or more markers.</summary>
    public const string MultiMarker = "multi-marker";

    /// <summary>Metric source of an exact fit to one marker of a facet.</summary>
    public const string SingleMarker = "single-marker";

    /// <summary>Metric source of a size taken in the nearest marker's own square (no wall model).</summary>
    public const string LocalMarker = "local-marker";
}
