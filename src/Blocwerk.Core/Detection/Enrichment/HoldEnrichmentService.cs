using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Detection.Enrichment;

/// <summary>
/// <see cref="IHoldEnrichmentService"/>: outlines + fingerprints on every wall; marker observations and
/// metric sizes / plane positions only on glyph walls (<see cref="Wall.GlyphsEnabled"/>). The whole run
/// is planned first and applied only when nothing failed (see <see cref="HoldEnrichmentPlan"/>).
/// </summary>
/// <remarks>
/// Both CV services are optional: a host without the HoldDetection project (tests, tooling) simply has
/// no enrichment. Stateless, so a singleton; the caller's context is passed per call.
/// </remarks>
public sealed partial class HoldEnrichmentService : IHoldEnrichmentService
{
    private readonly HoldDetectionSettings settings;
    private readonly ILogger<HoldEnrichmentService> logger;
    private readonly IHoldOutlineService? outlineService;
    private readonly IMarkerDetectionService? markerService;

    /// <summary>Initializes a new instance of the <see cref="HoldEnrichmentService"/> class.</summary>
    /// <param name="settings">App settings (the outline / marker kill switches).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="outlineService">The outliner; null disables outlines.</param>
    /// <param name="markerService">The marker detector; null disables the marker pass.</param>
    public HoldEnrichmentService(
        BlocwerkSettings settings,
        ILogger<HoldEnrichmentService> logger,
        IHoldOutlineService? outlineService = null,
        IMarkerDetectionService? markerService = null)
    {
        this.settings = settings.HoldDetection;
        this.logger = logger;
        this.outlineService = outlineService;
        this.markerService = markerService;
    }

    /// <inheritdoc/>
    public async Task<HoldEnrichmentSummary> EnrichAsync(
        BlocwerkDbContext db, HoldEnrichmentRequest request, CancellationToken ct = default)
    {
        if (ImagePixelLimit.IsTooLarge(request.Image))
        {
            logger.LogWarning(
                "Hold enrichment skipped on wall {WallId} panel {PanelId}: the photo exceeds {MaxPixels} pixels",
                request.Wall.Id, request.PanelId, ImagePixelLimit.MaxPixels);
            return HoldEnrichmentSummary.None;
        }

        try
        {
            WarnOnRotatedPhoto(request);
            var plan = new HoldEnrichmentPlan();
            var outlines = await Task.Run(() => PlanOutlines(request, plan), ct);
            await PlanMarkersAsync(db, request, plan, outlines, ct);

            var summary = plan.Apply(db);
            logger.LogInformation(
                "Hold enrichment on wall {WallId} panel {PanelId}: {Contours} contours, {Circles} circle fallbacks, "
                + "marker pass {MarkerPass} ({Markers} markers, {Observations} observations, {MarkerHolds} marker-sheet holds dropped), "
                + "{Measured} measured, {Placed} placed",
                request.Wall.Id, request.PanelId, summary.Contours, summary.CircleFallbacks, summary.MarkerPassRan,
                summary.Markers, summary.Observations, summary.DroppedMarkerHolds.Count, summary.Measured, summary.Placed);
            return summary;
        }
        catch (Exception ex)
        {
            // Never break ingest: nothing was applied (the plan is written only after it is complete),
            // so the holds stay exactly as detection produced them.
            logger.LogWarning(
                ex,
                "Hold enrichment failed on wall {WallId} panel {PanelId}; holds left as detected",
                request.Wall.Id,
                request.PanelId);
            return HoldEnrichmentSummary.None with { Failed = true };
        }
    }

    /// <summary>
    /// Outlines every auto-detected hold that has no shape yet, on one decoded image. Returns the image
    /// size (null when the pass did not run) so the marker pass can build circle fallbacks in pixels.
    /// </summary>
    private (int Width, int Height)? PlanOutlines(HoldEnrichmentRequest request, HoldEnrichmentPlan plan)
    {
        var targets = request.Holds.Where(h => h.IsAutoDetected && h.ShapePoints is null).ToList();
        if (!settings.OutlinesEnabled || outlineService is null || targets.Count == 0)
        {
            return null;
        }

        using var session = outlineService.OpenSession(request.Image);
        foreach (var hold in targets)
        {
            plan.Outlines[hold] = session.Outline(new HoldSeed(hold.X, hold.Y, hold.Radius));
        }

        return (session.ImageWidth, session.ImageHeight);
    }

    private void WarnOnRotatedPhoto(HoldEnrichmentRequest request)
    {
        var orientation = ExifOrientation.Read(request.Image);
        if (orientation is { } o && o != ExifOrientation.Normal)
        {
            logger.LogWarning(
                "Photo ingested on wall {WallId} panel {PanelId} carries EXIF orientation {Orientation}: holds, "
                + "outlines and markers use the raw (unrotated) pixel grid, so it may display rotated",
                request.Wall.Id,
                request.PanelId,
                o);
        }
    }
}
