using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The single consumer of <see cref="WallCaptureQueue"/>: processes one capture at a time.
/// </summary>
/// <remarks>
/// Single-instance assumption: the app runs as ONE process, so an in-memory queue plus the row
/// status is enough — two app instances would both resume the same captures. On start every capture
/// a previous process left queued or in flight is re-enqueued (oldest first) and resumes from its
/// recorded job ids; <see cref="WallCapturePipelineOptions.MaxAttempts"/> stops a capture that keeps
/// dying from looping forever. Abandoned drafts are swept at the same time.
/// </remarks>
public sealed class WallCaptureWorker(
    RootDbContextFactory dbContextFactory,
    WallCaptureQueue queue,
    WallCaptureProcessor processor,
    ICaptureFileStore files,
    WallCapturePipelineOptions options,
    ILogger<WallCaptureWorker> logger) : BackgroundService
{
    /// <summary>Re-enqueues unfinished captures and removes stale drafts. Public for tests.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            var unfinished = await db.WallCaptures
                .Where(c => c.Status == WallCaptureStatus.Queued || c.Status == WallCaptureStatus.Detecting
                            || c.Status == WallCaptureStatus.Solving || c.Status == WallCaptureStatus.Texturing
                            || c.Status == WallCaptureStatus.Splatting)
                .Select(c => new { c.Id, c.CreatedAt })
                .ToListAsync(ct);
            foreach (var capture in unfinished.OrderBy(c => c.CreatedAt))
            {
                queue.Enqueue(capture.Id);
            }

            if (unfinished.Count > 0)
            {
                logger.LogInformation("Resuming {Count} capture(s) left unfinished by a previous run", unfinished.Count);
            }

            await SweepDraftsAsync(db, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not recover unfinished captures on startup");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid captureId;
            try
            {
                captureId = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await processor.ProcessAsync(captureId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // ProcessAsync contains its own failures; this only keeps a DB blip from killing the host.
                logger.LogError(ex, "Capture worker failed on capture {CaptureId}; continuing", captureId);
            }
        }
    }

    private async Task SweepDraftsAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - options.DraftLifetime;
        var drafts = (await db.WallCaptures.Where(c => c.Status == WallCaptureStatus.Draft).ToListAsync(ct))
            .Where(c => c.CreatedAt < cutoff)
            .ToList();
        if (drafts.Count == 0)
        {
            return;
        }

        var ids = drafts.Select(d => d.Id).ToList();
        var paths = await db.WallCapturePhotos.Where(p => ids.Contains(p.CaptureId)).Select(p => p.StoredPath).ToListAsync(ct);
        db.WallCaptures.RemoveRange(drafts);
        await db.SaveChangesAsync(ct);
        foreach (var path in paths)
        {
            files.Delete(path);
        }
    }
}
