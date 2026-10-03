// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// What solving a finished capture again produced: the stored (inactive) model, its job, why it may not be activated,
/// and the document the placement check reads.
/// </summary>
internal sealed record ResolveOutcome(Guid ModelId, string JobId, string? Refusal, string PlacementJson);

/// <summary>
/// Solving a finished capture's 3D model again from its kept photos (<see cref="WallCaptureService.ResolveModelAsync"/>),
/// beside the capture worker (<see cref="WallModelResolveWorker"/>): the same solve request as the capture's own, the
/// result registered to the active model (so it lands in the frame the photo-real view is aligned to) and stored
/// inactive. When that registration holds it is activated together with the capture row in one transaction, then the
/// textures are rendered again and the follow-ups re-derived (<c>WallCaptureProcessor.ResolveAdopt.cs</c>). A restart
/// after the model was stored resumes from it. Nothing is trained; the capture's status stays finished.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>The <see cref="WallGeometryModel.Source"/> of a model solved again from a capture's photos.</summary>
    internal static string ResolvedModelSource(Guid captureId) => $"capture {captureId:N} re-solve";

    /// <summary>The <see cref="WallGeometryModel.Notes"/> of that model: names the solve job, so a restart finds the run's model.</summary>
    internal static string ResolvedModelNotes(Guid captureId, string jobId) => $"Solved again from the photos of capture {captureId:N} (job {jobId})";

    /// <summary>Runs the capture's pending re-solve (no-op without one). Cancellation leaves it to resume.</summary>
    public async Task ResolveModelAsync(Guid captureId, CancellationToken ct)
    {
        if (await ResolveRunAsync(captureId, ct) is not { } run)
        {
            await ResumeRederiveAsync(captureId, ct);
            return;
        }

        ResolveOutcome outcome;
        try
        {
            outcome = await StoredResolveAsync(run, ct) ?? await SolveAgainAsync(run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Solving the 3D model of capture {CaptureId} again failed", captureId);
            var reason = ex switch
            {
                DbUpdateException => "it could not be saved.",
                CaptureFailedException or ComputeJobException or InvalidDataException => ex.Message,
                _ => "something went wrong on the server.",
            };
            await EndResolveAsync(captureId, null, Noted(run, $"Solving the 3D model again failed ({reason}); the active model stays."), ct);
            return;
        }

        if (outcome.Refusal is not null)
        {
            await EndResolveAsync(
                captureId,
                outcome.JobId,
                Noted(run, $"The 3D model was solved again and stored, but NOT activated: {outcome.Refusal} The active model stays."),
                ct);
            return;
        }

        await AdoptResolvedModelAsync(run, outcome, ct);
    }

    private async Task<ResolveOutcome> SolveAgainAsync(CaptureRun run, CancellationToken ct)
    {
        var client = computeClients.Get(ComputeServiceKind.Geometry);
        if (!client.IsConfigured)
        {
            throw new CaptureFailedException("the 3D computation service is not configured on this server.");
        }

        var capture = run.Capture;
        var status = await RunJobAsync(
            CaptureResolveMark.JobId(capture.SolveJobId),
            () => SubmitSolveAsync(run, client, ct),
            jobId => UpdateAsync(capture.Id, c => c.SolveJobId = CaptureResolveMark.Mark + jobId, ct),
            client,
            new JobStage(capture.Id, WallCaptureStatus.Solving, 0, 1, "Solving the 3D model again", Silent: true),
            ct);
        var solved = CaptureComputeDocuments.GeometryFromSolveResult(status.Result)
                     ?? throw new CaptureFailedException("the 3D computation finished without a wall model.");
        var json = CaptureIgnoredDetections.AddToModel(solved, await LoadPhotosAsync(capture.Id, ct), run.Layout);
        await CheckShownRevisionAsync(run, json, ct);
        var frame = await RegisterToActiveAsync(run, json, ct);
        var refusal = ResolveRefusal(frame, json);
        var glyphs = new WallGlyphService(dbContextFactory, new CaptureActingUser(run.User), loggerFactory.CreateLogger<WallGlyphService>());

        // Stored inactive: it goes live together with the capture row, in one transaction (AdoptResolvedModelAsync).
        var options = new GeometryImportOptions(capture.PlanJson is null ? null : capture.PlanRevision, Activate: false);
        var notes = ResolvedModelNotes(capture.Id, status.JobId!);
        var imported = await glyphs.ImportGeometryAsync(capture.WallId, frame.Json, notes, ResolvedModelSource(capture.Id), options);
        if (!imported.Succeeded)
        {
            throw new CaptureFailedException("the computed model could not be used: " + string.Join(" ", imported.Errors));
        }

        logger.LogInformation(
            "Capture {CaptureId} solved again into model {ModelId} (to be activated: {Activate})", capture.Id, imported.Model!.Id, refusal is null);
        return new ResolveOutcome(imported.Model.Id, status.JobId!, refusal, json);
    }

    /// <summary>Why the new model may not replace the active one: it failed the checks, or is not in the view's frame.</summary>
    private static string? ResolveRefusal(FrameOutcome frame, string solvedJson)
    {
        if (WallGeometrySanityGate.Problems(solvedJson) is { Count: > 0 } problems)
        {
            return WallGeometrySanityGate.Describe(problems);
        }

        if (!frame.Activate)
        {
            return frame.Refusal ?? "It could not be tied to the active model.";
        }

        return FrameLineage.IsReset(frame.Json)
            ? "The active model cannot be tied to (no markers, or it fails the checks), so the new one would start a new frame "
              + "and the photo-real view would no longer line up."
            : null;
    }

    /// <summary>The capture as a run (its creator, its layout); null (and the mark dropped) when the re-solve may not run.</summary>
    private async Task<CaptureRun?> ResolveRunAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var capture = await db.WallCaptures.AsNoTracking().FirstOrDefaultAsync(c => c.Id == captureId, ct);
        if (capture is null || !CaptureResolveMark.IsResolving(capture.SolveJobId))
        {
            return null;
        }

        var modelId = capture.GeometryModelId;
        var active = modelId is not null
            && await db.WallGeometryModels.AnyAsync(m => m.Id == modelId && m.WallId == capture.WallId && m.IsActive, ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == capture.CreatedByUserId && u.DeletedAt == null, ct);
        var admin = user is not null && await WallAdminGuard.IsWallAdminAsync(db, capture.WallId, user.Id, ct);
        if (!active || !admin || capture.Status is not (WallCaptureStatus.Succeeded or WallCaptureStatus.SucceededWithoutTextures
            or WallCaptureStatus.SucceededWithoutSplat))
        {
            logger.LogInformation("Capture {CaptureId}: its model is not solved again (no longer active, finished or administered)", captureId);
            await EndResolveAsync(captureId, null, "Solving the 3D model again was dropped: its model is no longer the active one.", ct);
            return null;
        }

        var markerSize = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == capture.WallId).Select(w => w.MarkerSizeMm).FirstOrDefaultAsync(ct);
        return await WithCurrentPlanAsync(db, new CaptureRun(capture, user!, WallMarkerLayoutResolver.Resolve(capture.PlanJson, markerSize)), markerSize, ct);
    }

    private static string Noted(CaptureRun run, string note) => run.PlanNote is null ? note : $"{note} {run.PlanNote}";

    /// <summary>Drops the mark (a finished job id stays as the capture's solve job) and tells the admin what happened.</summary>
    private Task EndResolveAsync(Guid captureId, string? jobId, string note, CancellationToken ct) =>
        UpdateAsync(
            captureId,
            c =>
            {
                c.SolveJobId = jobId;
                AddFollowUpNote(c, note);
            },
            ct);
}
