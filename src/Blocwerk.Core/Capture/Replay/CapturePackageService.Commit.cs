// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// The commit: checked again, the staged files moved into the capture store (stamped as new, so the orphan sweep's grace
/// covers the moment before the rows exist), then every row in ONE transaction through the normal activation swap
/// (<see cref="WallGlyphService.SwapActiveModelAsync"/>), then the capture is queued. A failed move or insert moves the files back.
/// The capture and model take this server's number of the same marker plan; a model registered to a model that is not here
/// keeps that registration as history only.
/// </summary>
public sealed partial class CapturePackageService
{
    public async Task<CaptureImportReport> CommitImportAsync(Guid importId, CancellationToken ct)
    {
        var userId = await EnsureAdminAsync(ct);
        return await CaptureImportLocks.RunAsync(importId, () => CommitLockedAsync(importId, userId, ct), ct);
    }

    private async Task<CaptureImportReport> CommitLockedAsync(Guid importId, Guid userId, CancellationToken ct)
    {
        var manifest = await staging.LoadManifestAsync(importId, ct);
        if (manifest is null)
        {
            throw new UserFacingException("There is no open import with this id: begin it first (an import already committed answers there as such).");
        }

        var report = await CheckAsync(manifest, ct);
        if (report.AlreadyImported)
        {
            staging.Delete(importId);
            return report;
        }

        if (!report.ReadyToCommit)
        {
            var missing = report.Files.Count(f => f.State == CaptureImportFileState.Missing);
            return missing == 0 ? report : report with { Blockers = [.. report.Blockers, $"{missing} file(s) are not uploaded yet."] };
        }

        var moved = new List<string>();
        try
        {
            MoveIntoStore(importId, report.Files, moved);
            await ScoreUnscoredPhotosAsync(manifest.Rows.Photos, ct);
            await InsertAsync(manifest, ct);
        }
        catch
        {
            MoveBack(importId, moved);
            throw;
        }

        staging.Delete(importId);
        queue.Enqueue(manifest.CaptureId);
        logger.LogInformation(
            "Capture import {ImportId} committed by {UserId}: capture, {Photos} photos, model {ModelId} (active), {Textures} textures, GPU job {JobId} delivered; the capture is queued to finish its view",
            importId, userId, manifest.Rows.Photos.Count, manifest.Rows.Model.Id, manifest.Rows.Textures.Count, manifest.Rows.GpuJob.Id);
        return report with { Committed = true };
    }

    /// <summary>Moves the staged files into the store, adding each to <paramref name="moved"/> as it lands (so a failure part-way moves those back).</summary>
    private void MoveIntoStore(Guid importId, IReadOnlyList<CaptureImportFile> states, List<string> moved)
    {
        foreach (var file in states.Where(f => f.State == CaptureImportFileState.Staged))
        {
            var target = staging.StorePath(file.Name);
            File.Move(staging.StagedPath(importId, file.Name), target, overwrite: false);
            moved.Add(file.Name);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        }
    }

    private void MoveBack(Guid importId, List<string> moved)
    {
        foreach (var name in moved)
        {
            try
            {
                File.Move(staging.StorePath(name), staging.StagedPath(importId, name), overwrite: true);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Capture import {ImportId}: could not move {Name} back; the capture sweep removes it as an orphan", importId, name);
            }
        }
    }

    private async Task InsertAsync(CapturePackageManifest m, CancellationToken ct)
    {
        var rows = m.Rows;
        var now = DateTimeOffset.UtcNow;
        var document = WallGeometryDocument.Parse(rows.Model.Json);
        await using var db = dbContextFactory.CreateDbContext();
        var wall = await db.Walls.IgnoreQueryFilters().FirstAsync(w => w.Id == m.WallId, ct);
        var previous = await db.WallGeometryModels.Where(x => x.WallId == m.WallId && x.IsActive).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        var referenceHere = m.ReferenceModelId is { } reference && await db.WallGeometryModels.AnyAsync(x => x.Id == reference, ct);
        var model = CapturePackageRowCopies.Model(rows.Model, previous, m.ReferenceModelId, referenceHere);
        var capture = CapturePackageRowCopies.Capture(rows.Capture);
        if (m.PlanRevision is not null && (await TargetPlanRevisionAsync(db, m, ct)).Revision is { } planRevision)
        {
            capture.PlanRevision = planRevision;
            model.PlanRevision = planRevision;
        }

        await WallGlyphService.SwapActiveModelAsync(db, m.WallId, document, keep: null, () =>
        {
            db.WallGeometryModels.Add(model);
            db.WallCaptures.Add(capture);
            db.WallCapturePhotos.AddRange(rows.Photos);
            db.WallGeometryTextures.AddRange(rows.Textures);
            db.GpuJobs.Add(CapturePackageRowCopies.DeliveredJob(rows.GpuJob, model.Id, now));
            if (!document.IsFeatureFrame)
            {
                wall.MarkerSizeMm ??= document.MarkerSizeMm;
            }
        });
    }
}
