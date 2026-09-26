using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>Declarations (pre-fill + validation) and handing a draft to the pipeline.</summary>
public sealed partial class WallCaptureService
{
    public async Task<CaptureDeclarations> SuggestDeclarationsAsync(Guid captureId)
    {
        var (db, _, capture) = await OpenCaptureAsync(captureId);
        await using (db)
        {
            var layout = await DraftLayoutAsync(db, capture);
            var photos = await db.WallCapturePhotos.AsNoTracking().Where(p => p.CaptureId == captureId).ToListAsync();
            var seen = SeenSegments(photos, layout);
            var previous = CaptureDeclarationRules.Deserialize(await db.WallCaptures.AsNoTracking()
                .Where(c => c.WallId == capture.WallId && c.Id != captureId && c.DeclarationsJson != null)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => c.DeclarationsJson)
                .FirstOrDefaultAsync());
            if (layout.IsFromPlan)
            {
                return PlanDeclarations(layout, photos.All(p => p.MarkersJson is not null) ? seen : null, previous.LevelPairs);
            }

            var bound = await db.WallSegments.AsNoTracking()
                .Where(s => s.WallId == capture.WallId && s.MarkerSegmentIndex != null)
                .Select(s => new { Index = s.MarkerSegmentIndex!.Value, s.Name, s.Angle })
                .ToListAsync();

            var segments = seen.Order().Select(index =>
                previous.Segments.FirstOrDefault(p => p.Index == index)
                ?? (bound.FirstOrDefault(b => b.Index == index) is { } b
                    ? new CaptureSegmentDeclaration(index, b.Name, b.Angle, false)
                    : new CaptureSegmentDeclaration(index, CaptureDeclarationRules.DefaultName(index), null, false)))
                .ToList();
            return new CaptureDeclarations(segments, previous.LevelPairs);
        }
    }

    public async Task<IReadOnlyList<string>> StartAsync(
        Guid captureId, CaptureDeclarations declarations, string? notes, SplatQuality splatQuality = SplatQuality.High)
    {
        if (!IsComputeConfigured)
        {
            return ["The 3D computation service is not configured on this server. Import a wall-geometry.json instead."];
        }

        var (db, userId, capture) = await OpenDraftAsync(captureId);
        await using (db)
        {
            var layout = await DraftLayoutAsync(db, capture);
            var errors = await StartProblemsAsync(db, capture, declarations, layout, await IsMarkerlessAvailableAsync());
            if (errors.Count > 0)
            {
                return errors;
            }

            // The capture keeps the plan (and revision) it started with: later edits of the wall's plan don't change it.
            if (capture.PlanJson is null)
            {
                capture.PlanRevision = layout.IsFromPlan ? await MarkerBaselines.CurrentRevisionAsync(db, capture.WallId) : null;
            }

            capture.PlanJson = layout.Plan is { } plan ? MarkerPlanJson.ToJson(plan) : null;
            capture.DeclarationsJson = CaptureDeclarationRules.Serialize(declarations);
            capture.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()[..Math.Min(notes.Trim().Length, 2048)];
            capture.SplatQuality = splatQuality;
            capture.Status = WallCaptureStatus.Queued;
            capture.Stage = "Waiting to start";
            capture.Progress = 0;
            capture.CreatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            queue.Enqueue(capture.Id);
            logger.LogInformation("Capture {CaptureId} of wall {WallId} started by {UserId}", capture.Id, capture.WallId, userId);
            return [];
        }
    }

    private static async Task<List<string>> StartProblemsAsync(
        BlocwerkDbContext db, WallCapture capture, CaptureDeclarations declarations, WallMarkerLayout layout, bool markerless)
    {
        var photos = await db.WallCapturePhotos.AsNoTracking().Where(p => p.CaptureId == capture.Id).ToListAsync();
        var withMarkers = photos.Where(p => CaptureComputeDocuments.UsableMarkers(layout, p.MarkersJson).Count > 0).ToList();
        var errors = new List<string>();
        if (photos.Count < 2)
        {
            errors.Add("Upload at least two photos of the wall.");
        }
        else if (withMarkers.Count < 2 && photos.All(p => p.MarkersJson is not null) && !markerless)
        {
            errors.Add("At least two photos must show markers.");
        }

        errors.AddRange(withMarkers.Where(p => p.Focal35mm is null)
            .Select(p => $"{p.OriginalFileName ?? $"Photo {p.Index}"} has no focal length in its EXIF; upload the camera original."));

        // Without (enough) markers the capture is measured from photo features: the declarations table and level
        // pairs belong to the marker solve, so they are neither needed nor checked then.
        var features = markerless && withMarkers.Count < 2 && photos.All(p => p.MarkersJson is not null);
        if (!features)
        {
            // With a plan whose markers are still to be found (the pipeline detects them), "seen" is unknown yet.
            var pending = layout.IsFromPlan && photos.Any(p => p.MarkersJson is null);
            errors.AddRange(CaptureDeclarationRules.Validate(declarations, pending ? null : SeenSegments(photos, layout)));
            errors.AddRange(UnplannedLevelPairs(declarations, layout));
        }

        var running = await db.WallCaptures.AnyAsync(c => c.WallId == capture.WallId && c.Id != capture.Id
            && (c.Status == WallCaptureStatus.Queued || c.Status == WallCaptureStatus.Detecting
                || c.Status == WallCaptureStatus.Solving || c.Status == WallCaptureStatus.Texturing));
        if (running)
        {
            errors.Add("Another capture of this wall is still being processed. Wait for it to finish.");
        }

        return errors;
    }

    private static HashSet<int> SeenSegments(IEnumerable<WallCapturePhoto> photos, WallMarkerLayout layout) => photos
        .SelectMany(p => CaptureComputeDocuments.UsableMarkers(layout, p.MarkersJson))
        .Select(m => layout.SegmentOf(m.Id))
        .OfType<int>()
        .ToHashSet();

    /// <summary>
    /// A plan's declarations: every planned surface seen in the photos (all of them while detection is
    /// pending), with the plan's name and angle; plumb surfaces are the gravity references.
    /// </summary>
    private static CaptureDeclarations PlanDeclarations(WallMarkerLayout layout, HashSet<int>? seen, IReadOnlyList<int[]> previousPairs)
    {
        var segments = layout.Segments
            .Where(s => seen is null || seen.Contains(s.Index))
            .OrderBy(s => s.Index)
            .Select(s => new CaptureSegmentDeclaration(s.Index, s.Name.Length <= 100 ? s.Name : s.Name[..100], s.OverhangDeg, s.VerticalReference))
            .ToList();
        var pairs = previousPairs.Where(p => p.Length == 2 && p.All(layout.AllowedIds.Contains)).ToList();
        return new CaptureDeclarations(segments, pairs);
    }

    private static IEnumerable<string> UnplannedLevelPairs(CaptureDeclarations declarations, WallMarkerLayout layout) =>
        layout.IsFromPlan
            ? declarations.LevelPairs
                .Where(p => p.Length != 2 || !p.All(layout.AllowedIds.Contains))
                .Select(p => $"Level pair {string.Join("-", p)} names a marker that is not in the marker plan.")
            : [];
}
