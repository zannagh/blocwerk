using System.Text.RegularExpressions;

namespace Blocwerk.Core.Geometry;

/// <summary>
/// Sanity checks for an uploaded <c>wall-geometry.json</c> before it becomes a wall's model. The parser
/// (<see cref="WallGeometryDocument.Parse"/>) is deliberately tolerant; this is where a file that parses
/// but cannot be used is refused, with messages written for the admin who uploaded it.
/// </summary>
public static partial class WallGeometryValidator
{
    /// <summary>The only schema version this build understands.</summary>
    public const int SupportedVersion = 1;

    /// <summary>Longest facet id: <c>Hold.FacetId</c> is a <c>varchar(16)</c>.</summary>
    public const int MaxFacetIdLength = 16;

    /// <summary>Upper bound for a printed marker side; anything larger is a unit mistake.</summary>
    public const double MaxMarkerSizeMm = 1000;

    // Nothing on a climbing wall is further than this from the origin; a larger value is a unit
    // mistake (metres vs millimetres the other way round) or garbage.
    private const double MaxCoordinateMm = 100_000;

    // Enough per-item errors to show the pattern without drowning the card.
    private const int MaxItemErrors = 8;

    /// <summary>Returns every problem found; an empty list means the document is usable.</summary>
    public static IReadOnlyList<string> Validate(WallGeometryDocument document)
    {
        if (document.Version != SupportedVersion)
        {
            return [$"Schema version {document.Version} is not supported — this app reads version {SupportedVersion}."];
        }

        var errors = new List<string>();
        if (!string.Equals(document.Units, "mm", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Units must be \"mm\", not \"{document.Units}\".");
        }

        if (!IsFinite(document.MarkerSizeMm) || document.MarkerSizeMm <= 0 || document.MarkerSizeMm > MaxMarkerSizeMm)
        {
            errors.Add($"Marker size must be between 0 and {MaxMarkerSizeMm:0} mm (got {document.MarkerSizeMm}).");
        }

        if (document.Quality?.ReprojRmsPx is { } rms && (!IsFinite(rms) || rms < 0))
        {
            errors.Add("The reprojection error in \"quality\" must be a non-negative number.");
        }

        var itemErrors = new List<string>();
        var facetIds = ValidateSegments(document, errors, itemErrors);
        ValidateMarkers(document, facetIds, errors, itemErrors);

        errors.AddRange(itemErrors.Take(MaxItemErrors));
        if (itemErrors.Count > MaxItemErrors)
        {
            errors.Add($"…and {itemErrors.Count - MaxItemErrors} more problems.");
        }

        return errors;
    }

    /// <summary>
    /// True for an id a hold row and a texture URL can carry: 1–16 of <c>[A-Za-z0-9_-]</c>. Everything
    /// that writes a facet id (import, enrichment, the texture stage) checks this.
    /// </summary>
    public static bool IsValidFacetId(string? id) => id is not null && FacetIdPattern().IsMatch(id);

    private static HashSet<string> ValidateSegments(WallGeometryDocument document, List<string> errors, List<string> itemErrors)
    {
        var facetIds = new HashSet<string>(StringComparer.Ordinal);
        var segmentIndexes = new HashSet<int>();
        foreach (var segment in document.Segments)
        {
            if (segment.Index < 0 || !segmentIndexes.Add(segment.Index))
            {
                itemErrors.Add($"Segment index {segment.Index} is negative or listed twice.");
            }

            CheckAngle(segment.DeclaredAngleDeg, $"Segment {segment.Index} declared angle", itemErrors);
            CheckAngle(segment.MeasuredAngleDeg, $"Segment {segment.Index} measured angle", itemErrors);
            foreach (var facet in segment.Facets)
            {
                ValidateFacet(facet, segment.Index, facetIds, itemErrors);
            }
        }

        if (facetIds.Count == 0)
        {
            errors.Add("The file describes no facets — at least one segment with one facet is needed.");
        }

        return facetIds;
    }

    private static void ValidateFacet(WallGeometryFacet facet, int segmentIndex, HashSet<string> facetIds, List<string> itemErrors)
    {
        if (string.IsNullOrWhiteSpace(facet.Id))
        {
            itemErrors.Add($"A facet of segment {segmentIndex} has no id.");
            return;
        }

        if (!IsValidFacetId(facet.Id))
        {
            itemErrors.Add($"Facet id \"{Shorten(facet.Id)}\" must be 1–{MaxFacetIdLength} letters, digits, '_' or '-'.");
            return;
        }

        if (!facetIds.Add(facet.Id))
        {
            itemErrors.Add($"Facet \"{facet.Id}\" is listed twice.");
        }

        CheckAngle(facet.MeasuredAngleDeg, $"Facet \"{facet.Id}\" measured angle", itemErrors);
        CheckAngle(facet.YawDeg, $"Facet \"{facet.Id}\" yaw", itemErrors);
        if (facet.ExtentMm is { } e
            && (!AllSane(e.AMin, e.AMax, e.BMin, e.BMax) || e.AMax <= e.AMin || e.BMax <= e.BMin))
        {
            itemErrors.Add($"Facet \"{facet.Id}\" has an empty or out-of-range extent.");
        }
    }

    private static void ValidateMarkers(
        WallGeometryDocument document, HashSet<string> facetIds, List<string> errors, List<string> itemErrors)
    {
        if (document.Markers.Count == 0)
        {
            // A model solved from photo features (world.frameSource = "features") has no markers by design.
            if (!document.IsFeatureFrame)
            {
                errors.Add("The file contains no markers — at least one placed marker is needed.");
            }

            return;
        }

        var ids = new HashSet<int>();
        foreach (var marker in document.Markers)
        {
            if (marker.Id < 0 || !ids.Add(marker.Id))
            {
                itemErrors.Add($"Marker id {marker.Id} is negative or listed twice.");
            }

            if (facetIds.Count > 0 && !facetIds.Contains(marker.Facet))
            {
                itemErrors.Add($"Marker {marker.Id} sits on facet \"{marker.Facet}\", which the file does not define.");
            }

            var cornersOk = marker.CornersPlaneMm.Count == 4
                && marker.CornersPlaneMm.All(c => c is { Length: >= 2 } && AllSane(c[0], c[1]));
            if (!cornersOk)
            {
                itemErrors.Add($"Marker {marker.Id} needs four corners of two finite numbers each.");
            }
        }
    }

    private static string Shorten(string text) => text.Length <= 24 ? text : text[..24] + "…";

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,16}\z")]
    private static partial Regex FacetIdPattern();

    private static void CheckAngle(double? degrees, string label, List<string> itemErrors)
    {
        if (degrees is { } d && (!IsFinite(d) || d < -180 || d > 180))
        {
            itemErrors.Add($"{label} ({d}) is not an angle between -180 and 180 degrees.");
        }
    }

    private static bool AllSane(params double[] values) =>
        values.All(v => IsFinite(v) && Math.Abs(v) <= MaxCoordinateMm);

    private static bool IsFinite(double value) => double.IsFinite(value);
}
