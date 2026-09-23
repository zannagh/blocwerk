namespace Blocwerk.Core.Abstractions;

/// <summary>Where a <see cref="HoldOverlapSeed"/> homography came from.</summary>
public enum HoldOverlapSeedSource
{
    /// <summary>Fitted directly from the corners of markers visible in BOTH photos (no wall model).</summary>
    SharedMarkers,

    /// <summary>
    /// Composed through one facet plane of the active wall model: photo A → facet plane (mm) → photo B,
    /// each side from that photo's own markers. Works even when the photos share no marker.
    /// </summary>
    GeometryFacet,
}

/// <summary>One exact correspondence between the two photos, NORMALIZED (0..1) in each photo's RAW frame.</summary>
/// <param name="LeftX">Left-photo x (0..1).</param>
/// <param name="LeftY">Left-photo y (0..1).</param>
/// <param name="RightX">Right-photo x (0..1).</param>
/// <param name="RightY">Right-photo y (0..1).</param>
public readonly record struct HoldOverlapPointPair(double LeftX, double LeftY, double RightX, double RightY);

/// <summary>
/// A wall-space anchor: a left and a right hold that sit at (nearly) the same place on the same facet
/// plane AND look alike. Ids are the matcher ids of <see cref="MatcherHold.Id"/> on each side.
/// </summary>
/// <param name="LeftHoldId">Matcher id of the left hold.</param>
/// <param name="RightHoldId">Matcher id of the right hold.</param>
/// <param name="PlaneDistanceMm">Distance between the two plane positions, in mm.</param>
/// <param name="Similarity">Fingerprint similarity (0..1).</param>
public sealed record HoldOverlapAnchor(int LeftHoldId, int RightHoldId, double PlaneDistanceMm, double Similarity);

/// <summary>
/// Optional prior knowledge for <see cref="IHoldOverlapMatcher.Match"/>, available only on walls that
/// declare ArUco markers. Everything here is expressed in NORMALIZED (0..1) coordinates of each photo's
/// RAW pixel frame (EXIF orientation ignored), the frame hold X/Y and marker corners live in. A matcher
/// that receives a seed must interpret its images in that same raw frame.
/// </summary>
public sealed record HoldOverlapSeed
{
    /// <summary>
    /// Gets the 3x3 row-major left→right homography in normalized raw coordinates, or null when only
    /// anchors are known (then the matcher keeps its own coarse estimate).
    /// </summary>
    public IReadOnlyList<double>? Homography { get; init; }

    /// <summary>Gets how <see cref="Homography"/> was obtained.</summary>
    public HoldOverlapSeedSource Source { get; init; }

    /// <summary>Gets the number of markers that constrain <see cref="Homography"/> (per side for a facet seed: the minimum).</summary>
    public int MarkerCount { get; init; }

    /// <summary>Gets the facet the homography was composed through (<see cref="HoldOverlapSeedSource.GeometryFacet"/> only).</summary>
    public string? FacetId { get; init; }

    /// <summary>Gets the RMS transfer error of the fit over the shared marker corners, in right-photo pixels (NaN when unknown).</summary>
    public double FitRmsPx { get; init; } = double.NaN;

    /// <summary>
    /// Gets the MEASURED correspondences (shared marker corners). Exact, so the matcher may use them as
    /// warp anchors.
    /// </summary>
    public IReadOnlyList<HoldOverlapPointPair> MeasuredPairs { get; init; } = [];

    /// <summary>
    /// Gets PREDICTED correspondences (a left hold's plane-induced position in the right photo). Off by the
    /// hold's relief parallax, so the matcher may only use them to fill regions no measured anchor covers.
    /// </summary>
    public IReadOnlyList<HoldOverlapPointPair> PriorPairs { get; init; } = [];

    /// <summary>Gets confident wall-space hold pairs (see <see cref="HoldOverlapAnchor"/>).</summary>
    public IReadOnlyList<HoldOverlapAnchor> Anchors { get; init; } = [];

    /// <summary>Gets a value indicating whether the homography is backed by two or more markers.</summary>
    public bool IsMultiMarker => Homography is not null && MarkerCount >= 2;
}
