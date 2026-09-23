using System.Data.Common;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Runs ONE capture end to end: detect markers → solve (compute worker) → import + activate →
/// textures (compute worker) → notify. Every stage is resumable from the capture row (job ids and
/// the produced model id are persisted as soon as they exist), so a restarted app continues where
/// the previous process stopped instead of leaving a capture "running" forever.
/// </summary>
public sealed partial class WallCaptureProcessor(
    RootDbContextFactory dbContextFactory,
    BlocwerkSettings settings,
    IComputeJobClientFactory computeClients,
    ICaptureFileStore files,
    IPushNotificationService push,
    ILoggerFactory loggerFactory,
    WallCapturePipelineOptions options,
    IMarkerDetectionService? markerDetection = null,
    ICaptureVideoFrameExtractor? videoFrames = null)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<WallCaptureProcessor>();

    /// <summary>Processes a capture. Returns normally on success or on a handled failure.</summary>
    /// <remarks>Cancellation (app shutdown) propagates and leaves the row as is, to be resumed.</remarks>
    public async Task ProcessAsync(Guid captureId, CancellationToken ct)
    {
        CaptureRun? context = null;
        try
        {
            context = await BeginAsync(captureId, ct);
            if (context is null)
            {
                return;
            }

            await RunStagesAsync(context, ct);
        }
        catch (CaptureFailedException ex)
        {
            await FailAsync(captureId, ex.Message, ct);
        }
        catch (ComputeJobException ex)
        {
            await FailAsync(captureId, ex.Message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A clean shutdown (a deploy) is not a failed attempt; only a process that dies keeps it.
            if (context is not null)
            {
                await RefundAttemptAsync(captureId);
            }

            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Capture {CaptureId} failed unexpectedly", captureId);
            await FailAsync(captureId, "Something went wrong on the server while processing the photos.", ct);
        }
    }

    internal static bool IsRunnable(WallCaptureStatus status) => status is WallCaptureStatus.Queued
        or WallCaptureStatus.Detecting or WallCaptureStatus.Solving or WallCaptureStatus.Texturing
        or WallCaptureStatus.Splatting;

    private async Task RunStagesAsync(CaptureRun run, CancellationToken ct)
    {
        // Model and textures are already done: only the photo-real stage is left to resume.
        if (run.Capture.Status == WallCaptureStatus.Splatting)
        {
            await SplatAsync(run, ct);
            return;
        }

        var client = computeClients.Get(ComputeServiceKind.Geometry);
        if (!client.IsConfigured)
        {
            throw new CaptureFailedException("The 3D computation service is not configured on this server.");
        }

        if (run.Capture.GeometryModelId is null)
        {
            await DetectMarkersAsync(run, ct);
            await SolveAndImportAsync(run, client, ct);
        }

        var textureError = await TextureAsync(run, client, ct);
        await push.NotifyWallModelReadyAsync(run.Capture.WallId, run.User.Id);
        await AfterTexturesAsync(run, textureError, ct);
    }

    /// <summary>
    /// Loads the capture, counts the attempt and re-checks that its creator may still administer the
    /// wall. Null when there is nothing (left) to do.
    /// </summary>
    private async Task<CaptureRun?> BeginAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var capture = await db.WallCaptures.FirstOrDefaultAsync(c => c.Id == captureId, ct);
        if (capture is null || !IsRunnable(capture.Status))
        {
            return null;
        }

        capture.Attempts++;
        if (capture.Attempts > options.MaxAttempts)
        {
            throw new CaptureFailedException("Processing was interrupted too often. Please start the capture again.");
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == capture.CreatedByUserId && u.DeletedAt == null, ct)
                   ?? throw new CaptureFailedException("The account that started this capture no longer exists.");
        db.CurrentUserId = user.Id;
        if (!await WallAdminGuard.IsWallAdminAsync(db, capture.WallId, user.Id, ct))
        {
            throw new CaptureFailedException("You are no longer an admin of this wall, so the capture was stopped.");
        }

        capture.StartedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        db.Entry(capture).State = EntityState.Detached;
        var markerSize = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == capture.WallId).Select(w => w.MarkerSizeMm).FirstOrDefaultAsync(ct);
        return new CaptureRun(capture, user, WallMarkerLayoutResolver.Resolve(capture.PlanJson, markerSize));
    }

    /// <summary>Writes status/progress/stage (and anything else) onto the row in its own context.</summary>
    private async Task UpdateAsync(Guid captureId, Action<WallCapture> change, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var capture = await db.WallCaptures.FirstAsync(c => c.Id == captureId, ct);
        change(capture);
        await db.SaveChangesAsync(ct);
    }

    private Task SetStageAsync(Guid captureId, WallCaptureStatus status, double progress, string stage, CancellationToken ct) =>
        UpdateAsync(captureId, c =>
        {
            c.Status = status;
            c.Progress = Math.Clamp(progress, 0, 1);
            c.Stage = stage.Length <= 200 ? stage : stage[..200];
        }, ct);

    private async Task FailAsync(Guid captureId, string message, CancellationToken ct)
    {
        logger.LogInformation("Capture {CaptureId} failed: {Reason}", captureId, message);
        try
        {
            await UpdateAsync(captureId, c =>
            {
                // Model and textures are already live: a failure now only costs the photo-real view.
                if (c.Status == WallCaptureStatus.Splatting)
                {
                    EndWithoutSplat(c, message);
                    return;
                }

                c.Status = WallCaptureStatus.Failed;
                c.Error = message.Length <= 2048 ? message : message[..2048];
                c.Stage = "Failed";
                c.CompletedAt = DateTimeOffset.UtcNow;
            }, ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogError(ex, "Could not record the failure of capture {CaptureId}", captureId);
        }
    }

    private async Task RefundAttemptAsync(Guid captureId)
    {
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            await db.WallCaptures
                .Where(c => c.Id == captureId && c.Attempts > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => c.Attempts - 1), CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not refund the attempt of capture {CaptureId} on shutdown", captureId);
        }
    }

    private async Task<List<WallCapturePhoto>> LoadPhotosAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.WallCapturePhotos.AsNoTracking()
            .Where(p => p.CaptureId == captureId)
            .OrderBy(p => p.Index)
            .ToListAsync(ct);
    }
}
