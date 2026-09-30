// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A re-solved model that was activated (<see cref="WallCaptureProcessor.ResolveModelAsync"/>) becomes the capture's:
/// the installed photo-real view is kept (registration put the new model in the frame the view's <c>frame.json</c> maps
/// to, so the same files and frame are shared, as a correction shares them), the wall textures are rendered again
/// (<see cref="WallCaptureProcessor.RerenderTexturesAsync"/>, which re-places the holds) and the other follow-ups run
/// again for the new model. No GPU job and no training.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private async Task AdoptResolvedModelAsync(CaptureRun run, Guid previousId, ResolveOutcome outcome, CancellationToken ct)
    {
        var captureId = run.Capture.Id;
        var kept = await ShareViewAsync(previousId, outcome.ModelId, ct);
        var note = Noted(run, "The 3D model was solved again from this capture's photos and activated" + (kept ? "; the photo-real view is kept." : "."));
        await UpdateAsync(
            captureId,
            c =>
            {
                c.GeometryModelId = outcome.ModelId;
                c.PlanJson = run.Capture.PlanJson;
                c.PlanRevision = run.Capture.PlanRevision;
                c.SolveJobId = outcome.JobId;
                c.TexturesJobId = CaptureTextureOutcome.RerenderMark;
                c.CoverageJson = null;
                c.FollowUpJson = (CaptureFollowUpRecord.Empty with { Note = note, Rederive = true }).ToJson();
            },
            ct);
        logger.LogInformation(
            "Capture {CaptureId}: re-solved model {ModelId} replaces {PreviousId} (photo-real view kept: {Kept})",
            captureId, outcome.ModelId, previousId, kept);

        await RerenderTexturesAsync(captureId, ct);
        if (followUps is not null)
        {
            await followUps.RunMissingAsync(captureId, ct);
        }
    }

    /// <summary>
    /// Finishes the follow-ups of an adopted re-solve a previous process left unfinished (the record is still marked
    /// <see cref="CaptureFollowUpRecord.Rederive"/>). A pending texture re-render finishes them itself once it is done.
    /// </summary>
    private async Task ResumeRederiveAsync(Guid captureId, CancellationToken ct)
    {
        if (followUps is null)
        {
            return;
        }

        await using (var db = dbContextFactory.CreateDbContext())
        {
            var textures = await db.WallCaptures.AsNoTracking().Where(c => c.Id == captureId).Select(c => c.TexturesJobId).FirstOrDefaultAsync(ct);
            if (CaptureTextureOutcome.IsRerendering(textures))
            {
                return;
            }
        }

        await followUps.RunMissingAsync(captureId, ct);
    }

    /// <summary>Gives <paramref name="toModelId"/> the view of <paramref name="fromModelId"/> (same files, same frame).</summary>
    private async Task<bool> ShareViewAsync(Guid fromModelId, Guid toModelId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var view = await db.WallGeometrySplats.AsNoTracking().FirstOrDefaultAsync(s => s.GeometryModelId == fromModelId, ct);
        if (view is null)
        {
            return false;
        }

        db.WallGeometrySplats.Add(new WallGeometrySplat
        {
            GeometryModelId = toModelId,
            StoredPath = view.StoredPath,
            SizeBytes = view.SizeBytes,
            MobileStoredPath = view.MobileStoredPath,
            MobileSizeBytes = view.MobileSizeBytes,
            SplatCount = view.SplatCount,
            LodLevelsJson = view.LodLevelsJson,
            UncleanedStoredPath = view.UncleanedStoredPath,
            UncleanedSizeBytes = view.UncleanedSizeBytes,
            FrameJson = view.FrameJson,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }
}
