// <copyright file="WallCaptureProcessor.SfmSplat.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The photo-real view of a markerless capture: its reconstruction (<see cref="WallCapture.SfmJobId"/>) already IS a
/// <c>splat-prepare</c> job, so its training bundle goes to a 3D runner as it is (no second COLMAP). That prepare ran
/// before any geometry existed, so the new model is put into <c>prepared.json</c> for <c>splat-finish</c>'s alignment.
/// Without runners (mode off) there is nothing to train it: the capture ends quietly without the view, as without a GPU.
/// A retrain clears <see cref="WallCapture.SfmJobId"/> and takes the normal route (the model exists by then).
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>The note a markerless capture keeps when no 3D runner is set up (a hint, not an error).</summary>
    internal const string NoRunnerNote =
        "The photo-real view was skipped: without markers it is trained on a 3D runner, and none is set up. Everything else is done.";

    /// <summary>True when the photo-real view should train from the markerless reconstruction's own bundle.</summary>
    private static bool FromReconstruction(WallCapture capture) =>
        capture.GeometryMode == WallCaptureGeometryMode.Features && capture.SfmJobId is not null;

    /// <summary>Queues the reconstruction's bundle for a runner (resumes like any prepare: <see cref="PrepareMark"/>).</summary>
    private async Task PrepareFromReconstructionAsync(WallCapture capture, Guid modelId, IComputeJobClient client, CancellationToken ct)
    {
        if (capture.SplatJobId is null)
        {
            var mark = PrepareMark + capture.SfmJobId;
            capture.SplatJobId = mark;
            await UpdateAsync(capture.Id, c => c.SplatJobId = mark, ct);
        }

        await PrepareForRunnerAsync(capture, modelId, client, ct);
    }

    /// <summary><c>prepared.json</c> with the model as its geometry when the prepare had none (a markerless reconstruction).</summary>
    private async Task<byte[]> WithGeometryAsync(byte[] prepared, Guid modelId, CancellationToken ct)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(prepared) as JsonObject;
        }
        catch (JsonException)
        {
            return prepared;
        }

        if (root is null || root["geometry"] is JsonObject)
        {
            return prepared;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var geometry = await db.WallGeometryModels.AsNoTracking().Where(m => m.Id == modelId).Select(m => m.Json).FirstAsync(ct);
        root["geometry"] = JsonNode.Parse(geometry);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private async Task EndWithoutRunnerAsync(CaptureRun run, CancellationToken ct)
    {
        var capture = run.Capture;
        logger.LogInformation("Capture {CaptureId}: no 3D runner for the markerless photo-real view; finishing without it", capture.Id);
        await FollowUpAsync(run, CaptureFollowUpPhase.Final, ct);
        await UpdateAsync(capture.Id, c => AddFollowUpNote(c, NoRunnerNote), ct);
        await CompleteAsync(capture.Id, TextureOutcome(capture.Error), capture.Error, ct);
    }
}
