// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A re-solved model that may be activated (<see cref="WallCaptureProcessor.ResolveModelAsync"/>) becomes the wall's
/// active model and the capture's in ONE transaction: the model it was registered to must still be the active one, its
/// installed photo-real view is shared (registration put the new model in the frame that view's <c>frame.json</c> maps
/// to, so the same files and frame are shared, as a correction shares them), and the capture row is re-pointed with the
/// texture re-render and re-derive marks. After the commit the wall textures are rendered again
/// (<see cref="WallCaptureProcessor.RerenderTexturesAsync"/>, which re-places the holds) and the other follow-ups run
/// again for the new model. No GPU job and no training.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private async Task AdoptResolvedModelAsync(CaptureRun run, ResolveOutcome outcome, CancellationToken ct)
    {
        var captureId = run.Capture.Id;
        bool? kept;
        try
        {
            kept = await ActivateResolvedAsync(run, outcome, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Capture {CaptureId}: the re-solved model {ModelId} could not be activated", captureId, outcome.ModelId);
            var reason = ex is DbUpdateException ? "it could not be saved" : "something went wrong on the server";
            await EndResolveAsync(
                captureId, outcome.JobId, Noted(run, $"The 3D model was solved again and stored, but NOT activated ({reason}). The active model stays."), ct);
            return;
        }

        if (kept is null)
        {
            const string Moved = "The 3D model was solved again and stored, but NOT activated: the wall's active model changed meanwhile, "
                + "so the new one would not line up with it. The active model stays.";
            await EndResolveAsync(captureId, outcome.JobId, Noted(run, Moved), ct);
            return;
        }

        logger.LogInformation(
            "Capture {CaptureId}: re-solved model {ModelId} activated (photo-real view kept: {Kept})", captureId, outcome.ModelId, kept);
        await CheckResolvedPlacementAsync(run, outcome, ct);
        await RerenderTexturesAsync(captureId, ct);
        if (followUps is not null)
        {
            await followUps.RunMissingAsync(captureId, ct);
        }
    }

    /// <summary>
    /// Activates the stored model and re-points the capture in one transaction. Null (nothing changed) when the model it
    /// was registered to is no longer the wall's active one; else whether a photo-real view was shared onto it.
    /// </summary>
    private async Task<bool?> ActivateResolvedAsync(CaptureRun run, ResolveOutcome outcome, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var model = await db.WallGeometryModels.FirstAsync(m => m.Id == outcome.ModelId, ct);
        var reference = RegisteredGeometry.Carried(model.Json).ReferenceModelId;
        var activeId = await db.WallGeometryModels.Where(m => m.WallId == model.WallId && m.IsActive).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
        if (reference is null || reference != activeId)
        {
            logger.LogWarning(
                "Capture {CaptureId}: re-solved model {ModelId} was registered to {ReferenceId}, but {ActiveId} is active now; not activated",
                run.Capture.Id, model.Id, reference, activeId);
            return null;
        }

        var view = await db.WallGeometrySplats.AsNoTracking().FirstOrDefaultAsync(s => s.GeometryModelId == reference, ct);
        var capture = await db.WallCaptures.FirstAsync(c => c.Id == run.Capture.Id, ct);
        var note = Noted(run, "The 3D model was solved again from this capture's photos and activated" + (view is null ? "." : "; the photo-real view is kept."));
        await WallGlyphService.SwapActiveModelAsync(db, model.WallId, WallGeometryDocument.Parse(model.Json), keep: model.Id, () =>
        {
            model.IsActive = true;
            if (view is not null)
            {
                db.WallGeometrySplats.Add(SharedView(view, model.Id));
            }

            capture.GeometryModelId = model.Id;
            capture.PlanJson = run.Capture.PlanJson;
            capture.PlanRevision = run.Capture.PlanRevision;
            capture.SolveJobId = outcome.JobId;
            capture.TexturesJobId = CaptureTextureOutcome.RerenderMark;
            capture.CoverageJson = null;
            capture.FollowUpJson = (CaptureFollowUpRecord.Empty with { Note = note, Rederive = true }).ToJson();
        });
        return view is not null;
    }

    /// <summary>The planned-vs-observed check for the new model; advisory, so nothing here stops the textures and follow-ups.</summary>
    private async Task CheckResolvedPlacementAsync(CaptureRun run, ResolveOutcome outcome, CancellationToken ct)
    {
        try
        {
            await CheckPlacementAsync(run, outcome.PlacementJson, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Capture {CaptureId}: the placement check of the re-solved model failed", run.Capture.Id);
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

    /// <summary>A copy of <paramref name="view"/> for <paramref name="modelId"/> (same files, same frame).</summary>
    private static WallGeometrySplat SharedView(WallGeometrySplat view, Guid modelId) => new()
    {
        GeometryModelId = modelId,
        StoredPath = view.StoredPath,
        SizeBytes = view.SizeBytes,
        MobileStoredPath = view.MobileStoredPath,
        MobileSizeBytes = view.MobileSizeBytes,
        SplatCount = view.SplatCount,
        LodLevelsJson = view.LodLevelsJson,
        UncleanedStoredPath = view.UncleanedStoredPath,
        UncleanedSizeBytes = view.UncleanedSizeBytes,
        FrameJson = view.FrameJson,
    };
}
