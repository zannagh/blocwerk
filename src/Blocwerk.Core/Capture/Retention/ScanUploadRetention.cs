// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Retention;

/// <summary>
/// An iPhone scan upload (<see cref="ScanStorage"/>) that was begun but never committed, and in which nothing changed for
/// <see cref="WallCapturePipelineOptions.ImportStagingLifetime"/>, is removed with its draft capture: begin it again to
/// continue. A scan folder whose capture is gone (its wall was deleted) goes too. A RECEIVED scan is never touched here,
/// nor anything processing later keeps in its folder (its point cloud): those live as long as the scan.
/// </summary>
public static class ScanUploadRetention
{
    /// <summary>Deletes (or, in a dry run, measures) the abandoned scan uploads.</summary>
    public static async Task<RetentionOutcome> RunAsync(
        RootDbContextFactory dbContextFactory, ICaptureFileStore files, WallCapturePipelineOptions options, DateTimeOffset now,
        ILogger logger, CancellationToken ct)
    {
        var storage = new ScanStorage(files);
        if (!storage.HasFolder)
        {
            return RetentionOutcome.None;
        }

        var cutoff = now - options.ImportStagingLifetime;
        var outcome = RetentionOutcome.None;
        foreach (var scanId in storage.List())
        {
            if (CaptureImportLocks.Has(scanId) || !(storage.LastActivity(scanId) < cutoff) || await IsKeptAsync(dbContextFactory, scanId, ct))
            {
                continue;
            }

            outcome += options.RetentionDryRun
                ? new RetentionOutcome(1, RetentionFiles.FolderSize(storage.Folder(scanId)))
                : await CaptureImportLocks.RunAsync(scanId, () => RemoveAsync(dbContextFactory, storage, scanId, cutoff, logger, ct), ct);
        }

        return outcome;
    }

    /// <summary>True for a scan past its upload: its capture exists and is no draft.</summary>
    private static async Task<bool> IsKeptAsync(RootDbContextFactory dbContextFactory, Guid scanId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.WallCaptures.AnyAsync(c => c.Id == scanId && c.Status != WallCaptureStatus.Draft, ct);
    }

    private static async Task<RetentionOutcome> RemoveAsync(
        RootDbContextFactory dbContextFactory, ScanStorage storage, Guid scanId, DateTimeOffset cutoff, ILogger logger, CancellationToken ct)
    {
        // Checked again under the lock: a request that held it may have touched or committed the scan.
        if (!(storage.LastActivity(scanId) < cutoff) || await IsKeptAsync(dbContextFactory, scanId, ct))
        {
            return RetentionOutcome.None;
        }

        var size = RetentionFiles.FolderSize(storage.Folder(scanId));
        try
        {
            await using (var db = dbContextFactory.CreateDbContext())
            {
                if (await db.WallCaptures.FirstOrDefaultAsync(c => c.Id == scanId && c.Status == WallCaptureStatus.Draft, ct) is { } draft)
                {
                    db.WallCaptures.Remove(draft);
                    await db.SaveChangesAsync(ct);
                }
            }

            storage.Delete(scanId);
            logger.LogInformation("Removed the abandoned scan upload {ScanId} ({Size})", scanId, RetentionFiles.Format(size));
            return new(1, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the abandoned scan upload {ScanId}; the next sweep retries", scanId);
            return RetentionOutcome.None;
        }
    }
}
