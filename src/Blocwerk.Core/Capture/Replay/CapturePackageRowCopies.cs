// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// The package's rows as the target inserts them (they are fresh per commit, read from the staged manifest, and changed in
/// place): ids, stored names and data as on the source; the state is that of a capture whose runner just delivered its
/// trained view, so the pipeline's resume path finishes it, and the follow-up chain has no record yet, so all of it runs.
/// </summary>
internal static class CapturePackageRowCopies
{
    internal const string ImportStage = "Photo-real view: finishing on the server (imported capture)";

    /// <summary>The model, active. Replacing a model it is not tied to, it starts a new frame (hold positions derived again).</summary>
    public static WallGeometryModel Model(WallGeometryModel model, Guid? previousActive, Guid? referenceModelId)
    {
        model.IsActive = true;
        if (previousActive is { } previous && previous != referenceModelId)
        {
            model.Json = FrameLineage.StampReset(model.Json, previous, "imported from another Blocwerk instance, not tied to the model it replaced");
        }

        return model;
    }

    /// <summary>The capture, back in its photo-real stage like a runner's hand-back; the source's worker job ids are dropped.</summary>
    public static WallCapture Capture(WallCapture capture)
    {
        capture.Error = capture.Status == WallCaptureStatus.SucceededWithoutTextures ? WallCaptureService.TexturePart(capture.Error) : null;
        capture.Status = WallCaptureStatus.Splatting;
        capture.Stage = ImportStage;
        capture.Progress = 0.96;
        capture.Attempts = 0;
        capture.CompletedAt = null;
        capture.FollowUpJson = null;
        capture.SolveJobId = null;
        capture.SfmJobId = null;
        capture.TexturesJobId = null;
        capture.SplatJobId = null;
        return capture;
    }

    /// <summary>
    /// The trained view as delivered by a runner and not installed, claimed by nobody: the capture pipeline finishes it
    /// (<c>splat-finish</c>) on this server's splat worker. Completed now, so the sweep's one-day give-up counts from here.
    /// </summary>
    public static GpuJob DeliveredJob(GpuJob job, DateTimeOffset now)
    {
        job.Status = GpuJobStatus.Succeeded;
        job.ClaimedByRunnerId = null;
        job.CompletedAt = now;
        job.LeaseExpiresAt = now + TimeSpan.FromMinutes(15);
        job.InstalledAt = null;
        job.FinishJobId = null;
        job.Progress = 1;
        job.Stage = "trained (imported); finishing on the server";
        job.Error = null;
        job.RefinishStateJson = null;
        job.PreviewPath = null;
        job.PreviewFinishJobId = null;
        job.PreviewBaseSplatId = null;
        job.InstalledPreviewPath = null;
        job.PreviewInstalledStep = null;
        return job;
    }
}
