// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The marker plan a re-solve uses: the wall's CURRENT revision when it is newer than the one the capture was pinned to
/// (a corrected plan: a marker on another segment, a mirrored triangle) and still defines every marker the capture's
/// photos show with the same dictionary; else the capture's own, and the note says why. The run carries the choice; the
/// capture's <c>PlanJson</c>/<c>PlanRevision</c> take it only when the new model is adopted.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private async Task<CaptureRun> WithCurrentPlanAsync(BlocwerkDbContext db, CaptureRun run, double? markerSize, CancellationToken ct)
    {
        var capture = run.Capture;
        if (capture.PlanJson is null || capture.PlanRevision is not { } pinned || run.Layout.Plan is not { } own)
        {
            return run;
        }

        var current = await db.WallMarkerPlans.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.WallId == capture.WallId && p.IsCurrent)
            .Select(p => new { p.Revision, p.Json })
            .FirstOrDefaultAsync(ct);
        if (current is null || current.Revision <= pinned)
        {
            return run;
        }

        var layout = WallMarkerLayoutResolver.Resolve(current.Json, markerSize);
        var missing = await DetectedIdsAsync(db, capture.Id, run.Layout, ct);
        missing.ExceptWith(layout.AllowedIds);
        var keep = layout.Plan is not { } plan ? "it could not be read"
            : plan.Dictionary != own.Dictionary ? $"it uses another marker dictionary ({plan.Dictionary})"
            : missing.Count > 0 ? $"it no longer defines marker(s) {string.Join(", ", missing.Order())} that the photos show"
            : null;
        if (keep is not null)
        {
            return run with { PlanNote = $"Solved with the capture's plan revision {pinned}, not the wall's current revision {current.Revision}: {keep}." };
        }

        capture.PlanJson = current.Json;
        capture.PlanRevision = current.Revision;
        return run with { Layout = layout, PlanNote = $"Solved with the wall's current plan revision {current.Revision} (the capture had revision {pinned})." };
    }

    private static async Task<HashSet<int>> DetectedIdsAsync(BlocwerkDbContext db, Guid captureId, WallMarkerLayout layout, CancellationToken ct)
    {
        var stored = await db.WallCapturePhotos.AsNoTracking().Where(p => p.CaptureId == captureId).Select(p => p.MarkersJson).ToListAsync(ct);
        return stored.SelectMany(json => CaptureComputeDocuments.UsableMarkers(layout, json)).Select(m => m.Id).ToHashSet();
    }
}
