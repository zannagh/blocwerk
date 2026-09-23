using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Detection.Enrichment;

/// <summary>
/// Everything one enrichment run decided, computed completely BEFORE anything is written, so a failure
/// anywhere leaves the holds and the context untouched. <see cref="Apply"/> only assigns fields.
/// </summary>
internal sealed class HoldEnrichmentPlan
{
    /// <summary>Gets this run's outline per hold (auto-detected holds without a shape only).</summary>
    public Dictionary<Hold, HoldOutlineResult> Outlines { get; } = [];

    /// <summary>Gets this run's metric measurement per hold (glyph walls only).</summary>
    public Dictionary<Hold, HoldMetric> Metrics { get; } = [];

    /// <summary>Gets the stale observation rows of the same panel photo, to delete.</summary>
    public List<WallMarkerObservation> StaleObservations { get; } = [];

    /// <summary>Gets the new observation rows, to add.</summary>
    public List<WallMarkerObservation> NewObservations { get; } = [];

    /// <summary>Gets the auto-detected holds that are really marker sheets, to drop (glyph walls only).</summary>
    public List<Hold> MarkerHolds { get; } = [];

    /// <summary>Gets or sets a value indicating whether the marker pass ran.</summary>
    public bool MarkerPassRan { get; set; }

    /// <summary>Gets or sets the number of validated markers in the photo.</summary>
    public int MarkerCount { get; set; }

    /// <summary>Writes the plan onto the holds and the context (no SaveChanges).</summary>
    /// <param name="db">The caller's context.</param>
    /// <returns>The run's summary.</returns>
    public HoldEnrichmentSummary Apply(BlocwerkDbContext db)
    {
        foreach (var (hold, outline) in Outlines)
        {
            var contour = IsContour(outline);
            if (contour)
            {
                hold.ShapePoints = outline.ShapePoints;
                hold.ShapeHoles = outline.ShapeHoles is { Count: > 0 } ? outline.ShapeHoles : null;
            }

            hold.OutlineSource = contour ? HoldOutlineSource.AutoContour : HoldOutlineSource.AutoCircle;
            hold.OutlineConfidence = contour ? outline.Confidence : null;
            hold.FingerprintJson = outline.Fingerprint.ToJson();
        }

        foreach (var (hold, metric) in Metrics)
        {
            HoldMetricPlanner.Apply(hold, metric);
        }

        DropMarkerHolds(db);
        db.WallMarkerObservations.RemoveRange(StaleObservations);
        db.WallMarkerObservations.AddRange(NewObservations);

        return new HoldEnrichmentSummary
        {
            Contours = Outlines.Values.Count(IsContour),
            CircleFallbacks = Outlines.Values.Count(o => !IsContour(o)),
            MarkerPassRan = MarkerPassRan,
            Markers = MarkerCount,
            Observations = NewObservations.Count,
            Measured = Metrics.Count,
            Placed = Metrics.Values.Count(m => m.FacetId is not null),
            DroppedMarkerHolds = MarkerHolds,
        };
    }

    /// <summary>
    /// Takes the marker-sheet holds back out of the context before the caller saves. They are fresh
    /// (Added, or not yet tracked), so detaching is enough; a hold that is somehow already stored is never
    /// deleted from here.
    /// </summary>
    private void DropMarkerHolds(BlocwerkDbContext db)
    {
        foreach (var hold in MarkerHolds)
        {
            var entry = db.Entry(hold);
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private static bool IsContour(HoldOutlineResult outline) =>
        outline.Method != HoldOutlineMethod.CircleFallback && outline.ShapePoints is { Count: >= 3 };
}
