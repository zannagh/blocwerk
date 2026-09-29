namespace Blocwerk.Core.Abstractions;

/// <summary>A point in image pixel coordinates (or normalized 0..1 when stated).</summary>
public readonly record struct MarkerPoint(double X, double Y);

/// <summary>Validation settings for <see cref="IMarkerDetectionService"/>.</summary>
public sealed record MarkerDetectionOptions
{
    /// <summary>The wall's legal ids: 6 segments x 6 roles.</summary>
    public static readonly IReadOnlySet<int> DefaultAllowedIds = Enumerable.Range(0, 36).ToHashSet();

    public static MarkerDetectionOptions Default { get; } = new();

    /// <summary>
    /// Ids that may appear on the wall. DICT_4X4_50 has a low Hamming distance, so anything else
    /// is a false positive by definition.
    /// </summary>
    public IReadOnlySet<int> AllowedIds { get; init; } = DefaultAllowedIds;

    /// <summary>Candidates whose mean side is below this are rejected (real false positives were ~12 px).</summary>
    public double MinSidePx { get; init; } = 20.0;

    /// <summary>Candidates whose longest/shortest edge ratio exceeds this are rejected (grazing angle).</summary>
    public double MaxEdgeRatio { get; init; } = 4.0;

    /// <summary>
    /// Re-fit accepted markers' corners as intersections of the black square's edge lines. ArUco's
    /// corners on the wall photos sit 3–29 px off (screw heads, paper edge); millimetre hold sizes
    /// come from these corners, so leave this on except for diagnostics.
    /// </summary>
    public bool RefineCorners { get; init; } = true;

    /// <summary>
    /// Lowest quiet-zone contrast (ring just outside the quad vs the marker's own black and white) a marker may
    /// have. The default suits hand-cut legacy prints whose white margin is often trimmed away (real ones down to
    /// −0.09); markers printed from a plan carry a full one-module margin and use
    /// <see cref="PlanMinQuietZoneContrast"/>.
    /// </summary>
    public double MinQuietZoneContrast { get; init; } = -0.5;

    /// <summary>
    /// The quiet-zone floor for plan-printed markers: on The Attic's plan rev 1 (1110 real detections in 142 photos)
    /// the lowest real contrast was 0.15; black holds that decoded as ids (a ring hold as id 17, a purple one as
    /// id 37, bolt holes) scored −0.39…0.08.
    /// </summary>
    public const double PlanMinQuietZoneContrast = 0.1;

    /// <summary>
    /// Also decode on a brightened (gamma 0.5) copy of the photo and merge: markers on a dark kickboard or
    /// volume that the plain pass cannot read. Off by default; the quiet-zone check still runs on the photo itself.
    /// </summary>
    public bool BrightenedPass { get; init; }
}

/// <summary>A validated marker. Corners are in ArUco order TL, TR, BR, BL of the printed marker.</summary>
public sealed record DetectedMarker
{
    public required int Id { get; init; }

    /// <summary>Corners in pixels, TL, TR, BR, BL.</summary>
    public required IReadOnlyList<MarkerPoint> CornersPx { get; init; }

    /// <summary>Corners normalized by image width/height (0..1), TL, TR, BR, BL.</summary>
    public required IReadOnlyList<MarkerPoint> CornersNormalized { get; init; }

    /// <summary>Mean edge length in pixels.</summary>
    public required double SidePx { get; init; }

    /// <summary>Longest edge / shortest edge (1 = seen head-on).</summary>
    public required double EdgeRatio { get; init; }

    /// <summary>True when some corners were reconstructed rather than observed (e.g. cut off by the frame).</summary>
    public bool Synthetic { get; init; }

    /// <summary>Centroid of the four pixel corners.</summary>
    public MarkerPoint CenterPx => new(CornersPx.Average(c => c.X), CornersPx.Average(c => c.Y));
}

/// <summary>Why a decoded candidate was not accepted.</summary>
public enum MarkerRejectionReason
{
    /// <summary>The id is not in <see cref="MarkerDetectionOptions.AllowedIds"/>.</summary>
    IdNotAllowed,

    /// <summary>Mean side below <see cref="MarkerDetectionOptions.MinSidePx"/>.</summary>
    TooSmall,

    /// <summary>Edge ratio above <see cref="MarkerDetectionOptions.MaxEdgeRatio"/>.</summary>
    TooOblique,

    /// <summary>Another candidate with the same id in this image scored better.</summary>
    DuplicateId,

    /// <summary>
    /// After corner refinement: the quad is not a dark square on light paper (its surroundings are as dark
    /// as its black border) — a dark hold or shadow whose texture happened to decode as an id.
    /// </summary>
    NoQuietZone,
}

/// <summary>A decoded candidate that failed validation, kept for diagnostics.</summary>
public sealed record RejectedMarkerCandidate(
    int Id,
    IReadOnlyList<MarkerPoint> CornersPx,
    double SidePx,
    double EdgeRatio,
    MarkerRejectionReason Reason,
    string Detail);

/// <summary>Outcome of detecting markers in one image.</summary>
public sealed record MarkerDetectionResult
{
    public required int ImageWidth { get; init; }

    public required int ImageHeight { get; init; }

    /// <summary>Validated markers, ordered by id; ids are unique.</summary>
    public required IReadOnlyList<DetectedMarker> Markers { get; init; }

    /// <summary>Decoded candidates that failed validation, with reasons.</summary>
    public required IReadOnlyList<RejectedMarkerCandidate> Rejected { get; init; }

    /// <summary>Quads the detector found but could not decode to any id (not listed individually).</summary>
    public int UndecodedCandidateCount { get; init; }

    /// <summary>
    /// True when the image produced evidence of false positives (the same id twice). The accepted
    /// markers are still the best candidates, but callers should treat this image with care.
    /// </summary>
    public bool Suspicious { get; init; }

    /// <summary>Human-readable reasons behind <see cref="Suspicious"/>.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
