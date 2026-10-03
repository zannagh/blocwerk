using System.Text.Json;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Builds small <c>wall-geometry.json</c> payloads (schema v1) for the glyph service tests: segment 0
/// with one facet, segment 5 folded into facets "5a"/"5b", and one marker per given facet.
/// </summary>
internal static class GlyphGeometryJson
{
    public static string Build(
        int version = 1,
        double markerSizeMm = 125,
        double seg0MeasuredAngle = 44.6,
        double seg0Yaw = 0,
        double reprojRmsPx = 0.9,
        bool withMarkers = true,
        bool withFacets = true,
        string markerFacet = "0")
    {
        var segments = withFacets
            ? new object[]
            {
                new
                {
                    index = 0, name = "main wall", declaredAngleDeg = 45.0, measuredAngleDeg = seg0MeasuredAngle,
                    facets = new[]
                    {
                        Facet("0", seg0MeasuredAngle, seg0Yaw, width: 3100, height: 2600),
                    },
                },
                new
                {
                    index = 5, name = "right piece", declaredAngleDeg = (double?)null, measuredAngleDeg = (double?)null,
                    facets = new[]
                    {
                        Facet("5a", 12.5, 36.9, width: 900, height: 2000),
                        Facet("5b", 0.0, 90.0, width: 600, height: 2000),
                    },
                },
            }
            : [];

        var markers = withMarkers
            ? new object[]
            {
                Marker(0, 0, markerFacet),
                Marker(33, 5, "5a"),
            }
            : [];

        return JsonSerializer.Serialize(new
        {
            version,
            units = "mm",
            dictionary = "DICT_4X4_50",
            markerSizeMm,
            segments,
            markers,
            quality = new { reprojRmsPx },
        });
    }

    private static object Facet(string id, double angle, double yaw, double width, double height) => new
    {
        id,
        origin = new[] { 0.0, 0.0, 0.0 },
        measuredAngleDeg = angle,
        yawDeg = yaw,
        extentMm = new { aMin = -50.0, aMax = width - 50, bMin = -50.0, bMax = height - 50 },
    };

    private static object Marker(int id, int segment, string facet) => new
    {
        id,
        segment,
        facet,
        cornersPlaneMm = new[]
        {
            new[] { 0.0, 125.0 }, new[] { 125.0, 125.0 }, new[] { 125.0, 0.0 }, new[] { 0.0, 0.0 },
        },
        observations = 2,
    };
}
