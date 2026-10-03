// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Replay;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Retention;

/// <summary>
/// A capture import (<c>captures/imports/{id}/</c>, <see cref="CapturePackageStaging"/>) lives outside the orphan sweep on
/// purpose, and is only removed by its commit or abort. One whose rollout script died, or that was begun for a capture
/// already imported, would keep its photos and trained files (gigabytes) forever: once nothing in it changed for
/// <see cref="WallCapturePipelineOptions.ImportStagingLifetime"/>, it is deleted under the import's own lock (a request
/// for it waits, and a request that came meanwhile keeps it). Begin the import again to continue.
/// </summary>
public static class ImportStagingRetention
{
    /// <summary>Deletes (or, in a dry run, measures) the abandoned import folders.</summary>
    public static async Task<RetentionOutcome> RunAsync(
        ICaptureFileStore files, WallCapturePipelineOptions options, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        if (files.ResolvePhysicalPath("probe.bin") is null)
        {
            return RetentionOutcome.None; // a store without a folder (tests) has no imports
        }

        var staging = new CapturePackageStaging(files);
        var cutoff = now - options.ImportStagingLifetime;
        var outcome = RetentionOutcome.None;
        foreach (var importId in staging.List())
        {
            if (CaptureImportLocks.Has(importId) || !(staging.LastActivity(importId) < cutoff))
            {
                continue;
            }

            if (options.RetentionDryRun)
            {
                outcome += new RetentionOutcome(1, RetentionFiles.FolderSize(staging.Folder(importId)));
                continue;
            }

            outcome += await CaptureImportLocks.RunAsync(importId, () => Task.FromResult(Remove(staging, importId, cutoff, logger)), ct);
        }

        return outcome;
    }

    private static RetentionOutcome Remove(CapturePackageStaging staging, Guid importId, DateTimeOffset cutoff, ILogger logger)
    {
        // Checked again under the lock: a request that held it may have touched the import.
        if (!(staging.LastActivity(importId) < cutoff))
        {
            return RetentionOutcome.None;
        }

        var size = RetentionFiles.FolderSize(staging.Folder(importId));
        try
        {
            staging.Delete(importId);
            logger.LogInformation("Removed the abandoned capture import {ImportId} ({Size})", importId, RetentionFiles.Format(size));
            return new(1, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the abandoned capture import {ImportId}; the next sweep retries", importId);
            return RetentionOutcome.None;
        }
    }
}
