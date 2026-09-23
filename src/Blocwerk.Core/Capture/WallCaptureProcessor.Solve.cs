using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>Stage 2: the solve job, then import + activation through <see cref="IWallGlyphService"/>.</summary>
public sealed partial class WallCaptureProcessor
{
    private const string SolveKind = "solve";

    /// <summary>The <see cref="WallGeometryModel.Source"/> of the model a capture imported.</summary>
    internal static string ModelSource(Guid captureId) => $"capture {captureId:N}";

    private async Task SolveAndImportAsync(CaptureRun run, IComputeJobClient client, CancellationToken ct)
    {
        var capture = run.Capture;
        if (await ImportedModelAsync(capture, ct) is { } existing)
        {
            // A previous process imported (and activated) the model but died before recording it.
            logger.LogInformation("Capture {CaptureId} already imported model {ModelId}; resuming from it", capture.Id, existing);
            capture.GeometryModelId = existing;
            await UpdateAsync(capture.Id, c => c.GeometryModelId = existing, ct);
            await CheckPlacementAsync(run, null, ct);
            return;
        }

        var status = await RunJobAsync(
            capture.SolveJobId,
            () => SubmitSolveAsync(run, client, ct),
            jobId => UpdateAsync(capture.Id, c => c.SolveJobId = jobId, ct),
            client,
            new JobStage(capture.Id, WallCaptureStatus.Solving, 0.2, 0.7, "Solving the 3D model"),
            ct);

        var json = CaptureComputeDocuments.GeometryFromSolveResult(status.Result)
                   ?? throw new CaptureFailedException("The 3D computation finished without a wall model.");
        await SetStageAsync(capture.Id, WallCaptureStatus.Solving, 0.72, "Activating the 3D model", ct);

        // The import runs AS the capture's creator, through the same wall-admin gate as the UI.
        var glyphs = new WallGlyphService(
            dbContextFactory, new CaptureActingUser(run.User), loggerFactory.CreateLogger<WallGlyphService>());
        var notes = string.IsNullOrWhiteSpace(capture.Notes) ? "In-app capture" : $"In-app capture: {capture.Notes}";
        var imported = await glyphs.ImportGeometryAsync(capture.WallId, json, notes, ModelSource(capture.Id));
        if (!imported.Succeeded)
        {
            throw new CaptureFailedException("The computed model could not be used: " + string.Join(" ", imported.Errors));
        }

        capture.GeometryModelId = imported.Model!.Id;
        await UpdateAsync(capture.Id, c => c.GeometryModelId = imported.Model.Id, ct);
        await CheckPlacementAsync(run, json, ct);
    }

    private async Task<Guid?> ImportedModelAsync(WallCapture capture, CancellationToken ct)
    {
        var source = ModelSource(capture.Id);
        await using var db = dbContextFactory.CreateDbContext();
        return await db.WallGeometryModels
            .Where(m => m.WallId == capture.WallId && m.Source == source)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<string> SubmitSolveAsync(CaptureRun run, IComputeJobClient client, CancellationToken ct)
    {
        var photos = await LoadPhotosAsync(run.Capture.Id, ct);
        var usable = photos.Where(p => CaptureComputeDocuments.UsableMarkers(run.Layout, p.MarkersJson).Count > 0).ToList();
        if (usable.Count < 2)
        {
            throw new CaptureFailedException(
                $"Only {usable.Count} photo(s) show a marker. At least two photos with markers are needed.");
        }

        var noFocal = usable.Where(p => p.Focal35mm is null).Select(p => p.OriginalFileName ?? $"photo {p.Index}").ToList();
        if (noFocal.Count > 0)
        {
            throw new CaptureFailedException(
                "These photos carry no focal length (EXIF), so their camera cannot be modelled: "
                + string.Join(", ", noFocal) + ". Upload the camera's original photos, not edited copies or screenshots.");
        }

        var markerSize = run.Layout.DefaultSizeMm ?? WallGlyphSettings.DefaultMarkerSizeMm;
        var declarations = CaptureDeclarationRules.Deserialize(run.Capture.DeclarationsJson);
        var request = CaptureComputeDocuments.BuildSolveRequest(run.Layout, markerSize, declarations, usable);
        return await client.SubmitJsonAsync(SolveKind, request, ct);
    }

    /// <summary>
    /// Submits (unless a job id is already recorded), persists the id at once, and polls to a
    /// terminal state. A recorded job the worker no longer knows — it restarted, or the result
    /// expired while the app was down — is submitted again, once.
    /// </summary>
    private async Task<ComputeJobStatus> RunJobAsync(
        string? existingJobId,
        Func<Task<string>> submit,
        Func<string, Task> persistJobId,
        IComputeJobClient client,
        JobStage stage,
        CancellationToken ct)
    {
        var resubmitted = false;
        var jobId = existingJobId;
        while (true)
        {
            if (jobId is null)
            {
                await SetStageAsync(stage.CaptureId, stage.Status, stage.From, $"{stage.Label}: sending", ct);
                jobId = await submit();
                await persistJobId(jobId);
            }

            try
            {
                return await PollAsync(client, jobId, stage, ct) with { JobId = jobId };
            }
            catch (ComputeJobException ex) when (ex.Kind == ComputeFailureKind.JobNotFound && !resubmitted)
            {
                logger.LogInformation("Compute job {JobId} is gone; submitting capture {CaptureId} again", jobId, stage.CaptureId);
                resubmitted = true;
                jobId = null;
            }
        }
    }
}
