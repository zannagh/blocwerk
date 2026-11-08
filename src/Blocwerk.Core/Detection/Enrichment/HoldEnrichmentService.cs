using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Recognition.Remote;
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
    private readonly IHoldOutlineBatch? outlineBatch;
    private readonly IMarkerDetectionService? markerService;
    private readonly IImageRecognitionGate? gate;

    /// <summary>Initializes a new instance of the <see cref="HoldEnrichmentService"/> class.</summary>
    /// <param name="settings">App settings (the outline / marker kill switches).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="outlineService">The outliner; null disables outlines.</param>
    /// <param name="markerService">The marker detector; null disables the marker pass.</param>
    /// <param name="gate">The process-wide recognition gate the whole run holds one slot of; null runs ungated.</param>
    public HoldEnrichmentService(
        BlocwerkSettings settings,
        ILogger<HoldEnrichmentService> logger,
        IHoldOutlineService? outlineService = null,
        IMarkerDetectionService? markerService = null,
        IImageRecognitionGate? gate = null)
    {
        this.gate = gate;
        this.settings = settings.HoldDetection;
        this.logger = logger;
        outlineBatch = outlineService is null ? null : new SessionHoldOutlineBatch(outlineService, gate);
        this.markerService = markerService;
    }

    /// <inheritdoc/>
    public Task<HoldEnrichmentSummary> EnrichAsync(
        BlocwerkDbContext db, HoldEnrichmentRequest request, CancellationToken ct = default)
    {
        if (!settings.Enabled)
        {
            logger.LogInformation(
                "Hold enrichment skipped on wall {WallId} panel {PanelId}: hold detection is switched off (HoldDetection:Enabled=false)",
                request.Wall.Id, request.PanelId);
            return Task.FromResult(HoldEnrichmentSummary.None);
        }

        if (gate is null)
        {
            return EnrichCoreAsync(db, request, null, ct);
        }

        return markerService is QueuedMarkerDetectionService
            ? EnrichWithRemoteMarkersAsync(db, request, gate, ct)
            : gate.RunAsync("enrich", () => EnrichCoreAsync(db, request, null, ct), ct);
    }

    /// <summary>
    /// A marker pass a recognition runner may serve runs first, outside the slot: the slot is for the app's own work, and a
    /// marker pass the app ends up doing itself takes one of its own.
    /// </summary>
    private async Task<HoldEnrichmentSummary> EnrichWithRemoteMarkersAsync(
        BlocwerkDbContext db, HoldEnrichmentRequest request, IImageRecognitionGate gate, CancellationToken ct)
    {
        MarkerPass? markers = null;
        if (!ImagePixelLimit.IsTooLarge(request.Image))
        {
            try
            {
                markers = await DetectMarkersAsync(db, request, ct);
            }
            catch (Exception ex)
            {
                return Failed(request, ex);
            }
        }

        return await gate.RunAsync("enrich", () => EnrichCoreAsync(db, request, markers, ct), ct);
    }

    private async Task<HoldEnrichmentSummary> EnrichCoreAsync(
        BlocwerkDbContext db, HoldEnrichmentRequest request, MarkerPass? detectedMarkers, CancellationToken ct)
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

            // Recognition first, geometry only (one outline batch, one marker pass); then the plan and its writes.
            var targets = OutlineTargets(request);
            var outlined = await OutlineAsync(request, targets, ct);
            var markers = detectedMarkers ?? await DetectMarkersAsync(db, request, ct);

            var plan = new HoldEnrichmentPlan();
            var size = PlanOutlines(request, targets, outlined, plan);
            await PlanMarkersAsync(db, request, plan, size, markers, ct);

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
            return Failed(request, ex);
        }
    }

    /// <summary>
    /// Never break ingest: nothing was applied (the plan is written only after it is complete), so the holds stay exactly as
    /// detection produced them.
    /// </summary>
    private HoldEnrichmentSummary Failed(HoldEnrichmentRequest request, Exception ex)
    {
        logger.LogWarning(ex, "Hold enrichment failed on wall {WallId} panel {PanelId}; holds left as detected", request.Wall.Id, request.PanelId);
        return HoldEnrichmentSummary.None with { Failed = true };
    }

    /// <summary>Every auto-detected hold that has no shape yet.</summary>
    private static List<Hold> OutlineTargets(HoldEnrichmentRequest request) =>
        request.Holds.Where(h => h.IsAutoDetected && h.ShapePoints is null).ToList();

    /// <summary>Outlines <paramref name="targets"/> in one batch on the photo; null when the pass does not run.</summary>
    private async Task<HoldOutlineBatchResult?> OutlineAsync(HoldEnrichmentRequest request, List<Hold> targets, CancellationToken ct)
    {
        if (!settings.OutlinesEnabled || outlineBatch is null || targets.Count == 0)
        {
            return null;
        }

        var seeds = targets.Select(h => HoldSeeds.Of(h, settings.BoxSeeds)).ToList();
        return await outlineBatch.OutlineAsync(request.Image, seeds, HoldOutlineBatchOptions.Jointly, ct);
    }

    /// <summary>
    /// Plans the outlines of <paramref name="targets"/>. Returns the image size (null when the pass did not run) so the
    /// marker pass can build circle fallbacks in pixels.
    /// </summary>
    private static (int Width, int Height)? PlanOutlines(
        HoldEnrichmentRequest request, List<Hold> targets, HoldOutlineBatchResult? outlined, HoldEnrichmentPlan plan)
    {
        if (outlined is null)
        {
            return null;
        }

        for (int i = 0; i < targets.Count; i++)
        {
            plan.Outlines[targets[i]] = outlined.Outlines[i];
        }

        // The panel's shapes must not overlap each other or a hand-placed hold: clip, else circle, else a smaller circle.
        var aspect = (double)outlined.ImageWidth / Math.Max(1, outlined.ImageHeight);
        foreach (var (hold, (outline, radius)) in HoldShapeCleanup.ResolveOutlines(request.Holds, plan.Outlines, allowRadiusShrink: true, aspect, keepTraced: true))
        {
            plan.Outlines[hold] = outline;
            if (Math.Abs(radius - hold.Radius) > 1e-9)
            {
                plan.Radii[hold] = radius;
            }
        }

        return (outlined.ImageWidth, outlined.ImageHeight);
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
