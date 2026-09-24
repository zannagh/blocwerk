using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Detection.Enrichment;

/// <summary>
/// The metric (millimetre) half of hold enrichment, shared by ingest (<see cref="HoldEnrichmentService"/>)
/// and the outline upgrade of existing holds: which facet a hold sits on and how big it is, from one
/// photo's markers and the wall's active geometry model. Pure apart from <see cref="LoadActiveGeometryAsync"/>.
/// </summary>
public static class HoldMetricPlanner
{
    /// <summary>The only wall-geometry.json schema version this reader understands.</summary>
    public const int SupportedGeometryVersion = 1;

    /// <summary>
    /// Measures <paramref name="holds"/> on one photo. With a model every hold is placed on a facet; without
    /// one only the printed marker size gives a scale (sizes, no position). Holds with no usable facet or
    /// marker are left out.
    /// </summary>
    /// <param name="holds">The holds to measure.</param>
    /// <param name="outlines">Fresh outlines per hold (a real contour wins over the stored shape / circle).</param>
    /// <param name="markers">The photo's markers, in the photo's pixel grid.</param>
    /// <param name="document">The wall's active model, or null.</param>
    /// <param name="markerSizeMm">The wall's printed marker size, used only without a model.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="layout">The wall's marker plan layout; with one, each marker's own printed size is used.</param>
    /// <returns>The measurement per hold that could be measured.</returns>
    public static Dictionary<Hold, HoldMetric> Measure(
        IEnumerable<Hold> holds,
        IReadOnlyDictionary<Hold, HoldOutlineResult> outlines,
        IReadOnlyList<DetectedMarker> markers,
        WallGeometryDocument? document,
        double? markerSizeMm,
        int width,
        int height,
        WallMarkerLayout? layout = null)
    {
        var metrics = new Dictionary<Hold, HoldMetric>();
        if (markers.Count == 0)
        {
            return metrics;
        }

        // Without a wall model only the printed marker size gives a scale — and it can never be
        // inferred from an id, so an unknown size means no metric at all.
        var mapping = document is null ? null : MarkerPlaneMapper.Map(markers, document);
        var localMaps = document is not null ? null
            : layout is { IsFromPlan: true } ? MarkerPlaneMapper.MapLocal(markers, layout)
            : markerSizeMm is > 0 and var size ? MarkerPlaneMapper.MapLocal(markers, size)
            : null;
        if (mapping is null && localMaps is null)
        {
            return metrics;
        }

        foreach (var hold in holds)
        {
            outlines.TryGetValue(hold, out var outline);
            var outlinePx = HoldMetricMeasurer.OutlinePx(hold, outline, width, height);
            var holesPx = HoldMetricMeasurer.HolesPx(hold, outline, width, height);
            var centre = new MarkerPoint(hold.X * width, hold.Y * height);
            var metric = mapping is not null
                ? HoldMetricMeasurer.MeasureOnFacets(outlinePx, centre, mapping, document!, markers, holesPx)
                : HoldMetricMeasurer.MeasureLocal(outlinePx, centre, localMaps!, markers, holesPx);
            if (metric is not null)
            {
                metrics[hold] = metric;
            }
        }

        return metrics;
    }

    /// <summary>Writes a measurement onto a hold, and its rotation-free sizes into the fingerprint.</summary>
    /// <param name="hold">The hold.</param>
    /// <param name="metric">The measurement.</param>
    public static void Apply(Hold hold, HoldMetric metric)
    {
        ApplySize(hold, metric);

        // Facet id and plane position belong together; an id the row cannot hold (a model imported
        // before ids were validated) drops the position rather than failing the whole save.
        var facetOk = metric.FacetId is null || WallGeometryValidator.IsValidFacetId(metric.FacetId);
        hold.FacetId = facetOk ? metric.FacetId : null;
        hold.PlaneAMm = facetOk ? metric.PlaneAMm : null;
        hold.PlaneBMm = facetOk ? metric.PlaneBMm : null;
        hold.MetricSource = metric.MetricSource;
    }

    /// <summary>
    /// Writes only the sizes of a measurement (and the fingerprint's rotation-free sizes), leaving the
    /// facet and plane position alone — for a reshaped hold whose centre did not move.
    /// </summary>
    /// <param name="hold">The hold.</param>
    /// <param name="metric">The measurement.</param>
    public static void ApplySize(Hold hold, HoldMetric metric)
    {
        hold.WidthMm = metric.WidthMm;
        hold.HeightMm = metric.HeightMm;
        hold.AreaMm2 = metric.AreaMm2;

        // The fingerprint's sizes are rotation-free (long / short side) so a relocated, rotated hold
        // still compares; the hold's own WidthMm/HeightMm stay along the facet's u/v.
        var fingerprint = HoldFingerprint.FromJson(hold.FingerprintJson);
        if (fingerprint is not null)
        {
            hold.FingerprintJson = (fingerprint with
            {
                WidthMm = Math.Max(metric.WidthMm, metric.HeightMm),
                HeightMm = Math.Min(metric.WidthMm, metric.HeightMm),
                AreaMm2 = metric.AreaMm2,
            }).ToJson();
        }
    }

    /// <summary>
    /// Rebuilds a photo's markers from its stored observation rows (normalized corners), so a photo can be
    /// measured again without re-running marker detection. Rows with unreadable corners are skipped.
    /// </summary>
    /// <param name="rows">The photo's observation rows.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>The markers in the photo's pixel grid.</returns>
    public static List<DetectedMarker> MarkersFromObservations(IEnumerable<WallMarkerObservation> rows, int width, int height)
    {
        var markers = new List<DetectedMarker>();
        foreach (var row in rows)
        {
            var corners = ParseCorners(row.CornersJson);
            if (corners is null)
            {
                continue;
            }

            var px = corners.Select(c => new MarkerPoint(c.X * width, c.Y * height)).ToArray();
            var edges = Enumerable.Range(0, 4)
                .Select(i => Math.Sqrt(Math.Pow(px[(i + 1) % 4].X - px[i].X, 2) + Math.Pow(px[(i + 1) % 4].Y - px[i].Y, 2)))
                .ToArray();
            markers.Add(new DetectedMarker
            {
                Id = row.MarkerId,
                CornersPx = px,
                CornersNormalized = corners,
                SidePx = row.SidePx > 0 ? row.SidePx : edges.Average(),
                EdgeRatio = edges.Min() > 0 ? edges.Max() / edges.Min() : 1,
                Synthetic = row.Synthetic,
            });
        }

        return markers;
    }

    /// <summary>The wall's active geometry model, or null (none, unparseable, or a newer schema).</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="logger">Where to warn about an unusable model.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The parsed model, or null.</returns>
    public static async Task<WallGeometryDocument?> LoadActiveGeometryAsync(
        BlocwerkDbContext db, Guid wallId, ILogger logger, CancellationToken ct)
    {
        var json = await db.WallGeometryModels
            .AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive)
            .Select(m => m.Json)
            .FirstOrDefaultAsync(ct);
        if (json is null)
        {
            return null;
        }

        try
        {
            var document = WallGeometryDocument.Parse(json);
            if (document.Version is > 0 and <= SupportedGeometryVersion)
            {
                return document;
            }

            logger.LogWarning(
                "Active geometry model of wall {WallId} has unsupported version {Version}; measuring without it",
                wallId,
                document.Version);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Active geometry model of wall {WallId} is not valid JSON; measuring without it", wallId);
        }

        return null;
    }

    private static MarkerPoint[]? ParseCorners(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<double[][]>(json);
            if (raw is not { Length: 4 } || raw.Any(c => c is not { Length: 2 }))
            {
                return null;
            }

            return raw.Select(c => new MarkerPoint(c[0], c[1])).ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
