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

    /// <summary>
    /// Metric source of a hold re-placed after an edit through a homography fitted to the photo's other
    /// placed holds (<see cref="Geometry.View3D.HoldPlaneProjector"/>), when the photo has no markers.
    /// </summary>
    public const string HoldFit = "hold-fit";

    /// <summary>
    /// Metric source of a hold placed by registering its panel photo onto the 3D model's facet textures
    /// ("place existing holds on the 3D model"), for photos taken before any marker was on the wall.
    /// </summary>
    public const string TextureRegistration = "texture-registration";

    /// <summary>
    /// Metric source of a <see cref="TextureRegistration"/> placement carried over from an earlier model when a
    /// re-captured model's textures could not be registered to the hold's panel photo: the previous plane position,
    /// moved through the shared wall frame onto the same facet of the new model (the size is kept). Re-placed like
    /// a texture-registration placement whenever a later run can register the photo.
    /// </summary>
    public const string TextureRegistrationCarried = "texture-registration-carried";
}
