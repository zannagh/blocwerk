using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// <see cref="IWallCaptureService"/>. Writes use the gate of every other wall-admin action:
/// <see cref="WallAdminGuard"/> (owner or admin, pinned to a kiosk's own wall) plus
/// <see cref="KioskGuard"/>, because a capture is an owner-desk task, never a tablet task.
/// </summary>
/// <remarks>
/// <paramref name="kioskContext"/>, <paramref name="markerDetection"/> and <paramref name="markerPlans"/> are optional,
/// as on <c>WallGlyphService</c>.
/// </remarks>
public sealed partial class WallCaptureService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    ICaptureFileStore files,
    WallCaptureQueue queue,
    IComputeJobClientFactory computeClients,
    ILogger<WallCaptureService> logger,
    IKioskContext? kioskContext = null,
    IMarkerDetectionService? markerDetection = null,
    IMarkerPlanService? markerPlans = null,
    ICaptureVideoFrameExtractor? videoFrames = null,
    WallCapturePipelineOptions? pipelineOptions = null,
    IDeployBusyGate? busyGate = null) : IWallCaptureService
{
    private const string AdminAction = "Capturing wall photos";

    public bool IsComputeConfigured => computeClients.Get(ComputeServiceKind.Geometry).IsConfigured;

    public bool IsSplatConfigured => computeClients.Get(ComputeServiceKind.Splat).IsConfigured && videoFrames is not null;

    public async Task<WallCaptureDraft?> GetDraftAsync(Guid wallId)
    {
        var (db, userId) = await OpenForAdminAsync(wallId);
        await using (db)
        {
            var draft = await db.WallCaptures.AsNoTracking()
                .Where(c => c.WallId == wallId && c.CreatedByUserId == userId && c.Status == WallCaptureStatus.Draft)
                .FirstOrDefaultAsync();
            return draft is null ? null : await ToDraftAsync(db, draft);
        }
    }

    public async Task<WallCaptureDraft> CreateDraftAsync(Guid wallId)
    {
        var (db, userId) = await OpenForAdminAsync(wallId);
        await using (db)
        {
            var glyphs = await db.Walls.Where(w => w.Id == wallId).Select(w => w.GlyphsEnabled).FirstOrDefaultAsync();
            if (!glyphs)
            {
                throw new InvalidOperationException("Switch on printed markers for this wall first.");
            }

            var draft = await db.WallCaptures
                .FirstOrDefaultAsync(c => c.WallId == wallId && c.CreatedByUserId == userId && c.Status == WallCaptureStatus.Draft);
            if (draft is null)
            {
                draft = new WallCapture { WallId = wallId, CreatedByUserId = userId, Stage = "Uploading photos" };
                db.WallCaptures.Add(draft);
                await db.SaveChangesAsync();
                logger.LogInformation("Capture draft {CaptureId} opened on wall {WallId} by {UserId}", draft.Id, wallId, userId);
            }

            return await ToDraftAsync(db, draft);
        }
    }

    public async Task DiscardDraftAsync(Guid captureId)
    {
        var (db, _, capture) = await OpenDraftAsync(captureId);
        await using (db)
        {
            var paths = await db.WallCapturePhotos.Where(p => p.CaptureId == captureId).Select(p => p.StoredPath).ToListAsync();
            paths.AddRange(CaptureVideoFiles.Of(capture));
            db.WallCaptures.Remove(capture);
            await db.SaveChangesAsync();
            foreach (var path in paths)
            {
                files.Delete(path);
            }
        }
    }

    private static async Task<WallCaptureDraft> ToDraftAsync(BlocwerkDbContext db, WallCapture draft)
    {
        var photos = await db.WallCapturePhotos.AsNoTracking()
            .Where(p => p.CaptureId == draft.Id)
            .OrderBy(p => p.Index)
            .ToListAsync();
        var revision = draft.PlanJson is not null ? draft.PlanRevision : await MarkerBaselines.CurrentRevisionAsync(db, draft.WallId);
        var plan = PlanInfo(draft, await DraftLayoutAsync(db, draft), revision);
        var video = draft.VideoStoredPath is null
            ? null
            : new CaptureVideoInfo(draft.VideoFileName, draft.VideoSizeBytes ?? 0, draft.VideoDurationSeconds);
        return new WallCaptureDraft(draft.Id, draft.Notes, photos.Select(p => ToResult(p, [])).ToList(), plan, video);
    }

    private static CapturePhotoResult ToResult(WallCapturePhoto p, IReadOnlyList<string> warnings) => new(
        p.Id,
        p.Index,
        p.OriginalFileName,
        p.Width,
        p.Height,
        p.Focal35mm,
        CaptureComputeDocuments.ParseMarkers(p.MarkersJson).Select(m => m.Id).ToList(),
        warnings);

    /// <summary>A context after the admin check for <paramref name="wallId"/>; refused from a kiosk. Caller disposes.</summary>
    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync();
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, AdminAction);
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);
            return (db, user.Id);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Opens the capture's wall for an admin write. The wall comes from the capture row, so a caller
    /// can never aim a capture id at a wall they do not administer.
    /// </summary>
    private async Task<(BlocwerkDbContext Db, Guid UserId, WallCapture Capture)> OpenCaptureAsync(Guid captureId)
    {
        Guid wallId;
        await using (var lookup = await dbContextFactory.CreateDbContextAsync())
        {
            wallId = await lookup.WallCaptures.Where(c => c.Id == captureId).Select(c => (Guid?)c.WallId).FirstOrDefaultAsync()
                     ?? throw new InvalidOperationException("Capture not found");
        }

        var (db, userId) = await OpenForAdminAsync(wallId);
        var capture = await db.WallCaptures.FirstAsync(c => c.Id == captureId);
        return (db, userId, capture);
    }

    /// <summary>As <see cref="OpenCaptureAsync"/>, and only for the caller's own, still-open draft.</summary>
    private async Task<(BlocwerkDbContext Db, Guid UserId, WallCapture Capture)> OpenDraftAsync(Guid captureId)
    {
        var (db, userId, capture) = await OpenCaptureAsync(captureId);
        if (capture.Status != WallCaptureStatus.Draft || capture.CreatedByUserId != userId)
        {
            await db.DisposeAsync();
            throw new InvalidOperationException("This capture has already been started and can no longer be changed.");
        }

        return (db, userId, capture);
    }
}
