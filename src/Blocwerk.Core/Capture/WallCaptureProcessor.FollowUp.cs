// <copyright file="WallCaptureProcessor.FollowUp.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The post-capture chain (<see cref="CaptureFollowUpChain"/>): right after the model and its textures are live
/// the existing holds are placed on it and their 3D shapes refined from the new photos; at the end (with or
/// without a photo-real view) the photo-real steps follow. Nothing here can fail the capture.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private async Task FollowUpAsync(CaptureRun run, CaptureFollowUpPhase phase, CancellationToken ct)
    {
        if (followUps is null)
        {
            return;
        }

        try
        {
            await followUps.RunAsync(run.Capture.Id, phase, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The steps isolate their own failures; this is the chain's bookkeeping (a DB hiccup) failing.
            logger.LogWarning(ex, "Capture {CaptureId}: the follow-up chain ({Phase}) could not run", run.Capture.Id, phase);
        }
    }

    /// <summary>
    /// Once the capture shows as done (its model ready, its photo-real view stored or waiting for a runner): the
    /// slow proposal steps, still on this background worker. A capture handed back to the photo-real stage in the
    /// meantime skips them here and runs them when it is done again.
    /// </summary>
    private Task AfterCompletionAsync(CaptureRun run, CancellationToken ct) => FollowUpAsync(run, CaptureFollowUpPhase.AfterCompletion, ct);

    /// <summary>Adds a plain-words note to the capture's follow-up record (e.g. why there is no photo-real view).</summary>
    private static void AddFollowUpNote(WallCapture capture, string note) =>
        capture.FollowUpJson = (CaptureFollowUpRecord.Parse(capture.FollowUpJson) with { Note = note }).ToJson();
}
