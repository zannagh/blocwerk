// <copyright file="WallCaptureProcessor.Sfm.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Stage 2 for walls WITHOUT (usable) markers, mode <see cref="WallCaptureGeometryMode.Features"/>: the splat worker's
/// <c>splat-prepare</c> reconstructs the photos (plus the video frames and ~15 anchor photos of the active model's
/// capture) and returns <c>sparse.zip</c>; wall-geometry's <c>solve-sfm</c> turns it into the same geometry document as
/// the marker solve (<c>markers: []</c>, <c>world.frameSource = "features"</c>); the plane-based registration
/// (<c>WallCaptureProcessor.SfmRegister.cs</c>) ties it to the active model; then import. Textures and the follow-ups
/// run unchanged; the photo-real view later trains from the SAME prepared bundle (no second COLMAP). Every job id is
/// stored the moment it exists (<see cref="WallCapture.SfmJobId"/>, <see cref="WallCapture.SolveJobId"/>), so a restart
/// resumes the stage it was in.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    internal const string SparseFile = "sparse.zip";

    private const string ReconstructLabel = "Reconstructing the wall from the photos";
    private const long MaxSparseBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Markers when at least two photos show usable markers (today's path, unchanged), else Features when this server
    /// can run it; else Markers, whose solve then refuses as before. A capture that already chose keeps its mode.
    /// </summary>
    private async Task<WallCaptureGeometryMode> DecideModeAsync(CaptureRun run, CancellationToken ct)
    {
        var capture = run.Capture;
        if (capture.GeometryMode == WallCaptureGeometryMode.Features || capture.SolveJobId is not null)
        {
            return capture.GeometryMode;
        }

        var photos = await LoadPhotosAsync(capture.Id, ct);
        var withMarkers = photos.Count(p => CaptureComputeDocuments.UsableMarkers(run.Layout, p.MarkersJson).Count > 0);
        if (withMarkers >= 2 || !await MarkerlessCaptureSupport.IsAvailableAsync(computeClients, ct))
        {
            return WallCaptureGeometryMode.Markers;
        }

        logger.LogInformation(
            "Capture {CaptureId}: {WithMarkers} photo(s) with markers; measuring the wall from photo features", capture.Id, withMarkers);
        capture.GeometryMode = WallCaptureGeometryMode.Features;
        await UpdateAsync(capture.Id, c => c.GeometryMode = WallCaptureGeometryMode.Features, ct);
        return WallCaptureGeometryMode.Features;
    }

    private async Task SfmAndImportAsync(CaptureRun run, IComputeJobClient geometry, CancellationToken ct)
    {
        var capture = run.Capture;
        if (await ImportedModelAsync(capture, ct) is { } existing)
        {
            logger.LogInformation("Capture {CaptureId} already imported model {ModelId}; resuming from it", capture.Id, existing.Id);
            capture.GeometryModelId = existing.Id;
            await UpdateAsync(capture.Id, c => c.GeometryModelId = existing.Id, ct);
            if (!existing.IsActive)
            {
                throw new CaptureFailedException(FeaturesNotActivatedMessage(null));
            }

            return;
        }

        var anchors = await AnchorsAsync(capture, ct);
        var json = await ReconstructAndSolveAsync(run, geometry, anchors, ct);
        await SetStageAsync(capture.Id, WallCaptureStatus.Solving, 0.72, "Activating the 3D model", ct);
        var frame = await RegisterFeaturesAsync(run, json, anchors, ct);
        var glyphs = new WallGlyphService(
            dbContextFactory, new CaptureActingUser(run.User), loggerFactory.CreateLogger<WallGlyphService>());
        var notes = string.IsNullOrWhiteSpace(capture.Notes) ? "In-app capture (no markers)" : $"In-app capture (no markers): {capture.Notes}";
        var imported = await glyphs.ImportGeometryAsync(
            capture.WallId, frame.Json, notes, ModelSource(capture.Id), new GeometryImportOptions(null, frame.Activate));
        if (!imported.Succeeded)
        {
            throw new CaptureFailedException("The computed model could not be used: " + string.Join(" ", imported.Errors));
        }

        capture.GeometryModelId = imported.Model!.Id;
        await UpdateAsync(capture.Id, c => c.GeometryModelId = imported.Model.Id, ct);
        if (!frame.Activate)
        {
            throw new CaptureFailedException(FeaturesNotActivatedMessage(frame.Refusal));
        }
    }

    private static string FeaturesNotActivatedMessage(string? reason) =>
        (reason is null ? string.Empty : reason + " ")
        + "The new 3D model was saved but NOT activated, so hold positions stay as they are. Re-capture with more of the "
        + "same views as the current model's photos, or activate the new model from the model history if its frame change is acceptable.";

    /// <summary>The reconstruction (resumed from <see cref="WallCapture.SfmJobId"/>) and the solve; returns the document.</summary>
    private async Task<string> ReconstructAndSolveAsync(CaptureRun run, IComputeJobClient geometry, CaptureAnchors? anchors, CancellationToken ct)
    {
        var capture = run.Capture;
        var splat = computeClients.Get(ComputeServiceKind.Splat);
        var sfmJobId = capture.SfmJobId;
        if (capture.SolveJobId is null)
        {
            var prepared = await RunJobAsync(
                capture.SfmJobId,
                () => SubmitReconstructionAsync(run, anchors, splat, ct),
                jobId => UpdateAsync(capture.Id, c => (c.SfmJobId, c.AnchorCaptureId) = (jobId, anchors?.CaptureId), ct),
                splat,
                new JobStage(capture.Id, WallCaptureStatus.Solving, 0.2, 0.55, ReconstructLabel, s => CaptureSplatDocuments.Describe(s, ReconstructLabel)),
                ct);
            sfmJobId = prepared.JobId!;
            capture.SfmJobId = sfmJobId;
            capture.AnchorCaptureId = anchors?.CaptureId;
        }

        var status = await RunJobAsync(
            capture.SolveJobId,
            () => SubmitSolveSfmAsync(run, geometry, splat, sfmJobId!, anchors, ct),
            jobId => UpdateAsync(capture.Id, c => c.SolveJobId = jobId, ct),
            geometry,
            new JobStage(capture.Id, WallCaptureStatus.Solving, 0.55, 0.7, "Finding the wall's surfaces"),
            ct);
        return CaptureComputeDocuments.GeometryFromSolveResult(status.Result)
               ?? throw new CaptureFailedException("The 3D computation finished without a wall model.");
    }

    private async Task<string> SubmitReconstructionAsync(CaptureRun run, CaptureAnchors? anchors, IComputeJobClient splat, CancellationToken ct)
    {
        var captureId = run.Capture.Id;
        await PrepareVideoFramesAsync(captureId, WallCaptureStatus.Solving, ct);
        var parts = await PhotoPartsAsync(await LoadPhotosAsync(captureId, ct), CaptureComputeDocuments.PhotoName, ct);
        parts.AddRange(await FramePartsAsync(captureId, ct));
        if (anchors is not null)
        {
            // Anchor pixels never reach training: the worker drops them from the model before the bundle.
            parts.AddRange(await PhotoPartsAsync(anchors.Photos, i => anchors.StemByIndex[i], ct));
        }

        parts.Add(ComputeJobPart.Json("options", CaptureSplatDocuments.BuildOptions(settings.SplatMaxSteps, run.Capture.SplatQuality)));
        return await splat.SubmitMultipartAsync(PrepareKind, parts, ct);
    }

    private async Task<string> SubmitSolveSfmAsync(
        CaptureRun run, IComputeJobClient geometry, IComputeJobClient splat, string sfmJobId, CaptureAnchors? anchors, CancellationToken ct)
    {
        var capture = run.Capture;
        var sparse = await splat.DownloadFileAsync(sfmJobId, SparseFile, MaxSparseBytes, ct);
        var photos = await LoadPhotosAsync(capture.Id, ct);
        var request = CaptureSfmDocuments.BuildRequest(
            photos.Select(p => p.Index),
            await AngleHintsAsync(capture.WallId, ct),
            CaptureSfmDocuments.ParseScale(capture.ScaleReferenceJson),
            anchors?.Map ?? new Dictionary<string, string>(),
            anchors?.ReferenceJson,
            run.Layout.DefaultSizeMm ?? WallGlyphSettings.DefaultMarkerSizeMm);
        var parts = new List<ComputeJobPart>
        {
            ComputeJobPart.Json("request", request),
            ComputeJobPart.File("sparse", SparseFile, sparse, "application/zip"),
        };
        return await geometry.SubmitMultipartAsync(MarkerlessCaptureSupport.SolveKind, parts, ct);
    }

    /// <summary>The wall's declared angles (its segments', else its own) as hints for finding "up".</summary>
    private async Task<IReadOnlyList<CaptureAngleHint>> AngleHintsAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var segments = await db.WallSegments.AsNoTracking()
            .Where(s => s.WallId == wallId)
            .OrderBy(s => s.SortOrder)
            .Select(s => new { s.Name, s.Angle, s.MarkerSegmentIndex })
            .ToListAsync(ct);
        if (segments.Count > 0)
        {
            return segments.Select((s, i) => new CaptureAngleHint(s.MarkerSegmentIndex ?? i, s.Name, s.Angle)).DistinctBy(h => h.Index).ToList();
        }

        var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking().Where(w => w.Id == wallId).Select(w => new { w.Name, w.Angle }).FirstAsync(ct);
        return [new CaptureAngleHint(0, wall.Name, wall.Angle)];
    }
}
