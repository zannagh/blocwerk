// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The rules of a GPU job's previews (the splats so far, uploaded while a runner trains): they only move forward (a
/// higher step than any preview taken or installed before), never replace the job's final result (once it is delivered
/// or installed no preview is installed any more), never outlive a cancel or a failure (a preview still pending then
/// is dropped with the failure), and an installed one stays when the final result fails for good. The conditional updates in <see cref="GpuJobQueue"/> enforce the same in the database.
/// </summary>
public static class GpuJobPreviews
{
    /// <summary>
    /// Whether a runner's preview after <paramref name="step"/> of <paramref name="total"/> steps is taken: it moves
    /// forward, within the job's bounds (<paramref name="options"/>: a step of at least
    /// <see cref="GpuRunnerOptions.MinPreviewStep"/>, a sane total, a step gap, a count and a rate limit per job).
    /// </summary>
    public static bool MayAccept(GpuJob job, int step, int total, GpuRunnerOptions? options = null, DateTimeOffset? now = null) =>
        step > 0 && total > step && job.InstalledAt is null
        && job.Status is GpuJobStatus.Claimed or GpuJobStatus.Running
        && (job.PreviewStep is null || job.PreviewStep < step)
        && WithinBounds(job, step, total, options ?? new GpuRunnerOptions(), now ?? DateTimeOffset.UtcNow);

    private static bool WithinBounds(GpuJob job, int step, int total, GpuRunnerOptions options, DateTimeOffset now) =>
        step >= GpuRunnerOptions.MinPreviewStep && total <= GpuRunnerOptions.MaxPreviewTotalSteps
        && job.PreviewCount < options.MaxPreviewsPerJob
        && (job.PreviewStep is null || step - job.PreviewStep >= options.MinPreviewStepGap)
        && (job.PreviewAcceptedAt is null || now - job.PreviewAcceptedAt >= options.MinPreviewInterval);

    /// <summary>Whether the preview after <paramref name="step"/> steps may still be installed on the model.</summary>
    public static bool MayInstall(GpuJob job, int step) =>
        job.InstalledAt is null && job.Status is not (GpuJobStatus.Succeeded or GpuJobStatus.Failed or GpuJobStatus.Cancelled)
        && (job.PreviewInstalledStep is null || job.PreviewInstalledStep < step);

    /// <summary>Whether the job has a delivered preview waiting to be installed.</summary>
    public static bool IsPending(GpuJob job) =>
        job.PreviewPath is not null && job.PreviewStep is { } step && MayInstall(job, step);

    /// <summary>"Photo-real preview (step 7000 of 50000)".</summary>
    public static string Label(int step, int? total) => total is > 0
        ? string.Create(CultureInfo.InvariantCulture, $"Photo-real preview (step {step} of {total})")
        : string.Create(CultureInfo.InvariantCulture, $"Photo-real preview (step {step})");

    /// <summary>The capture's note when the final result failed for good and the preview stays.</summary>
    public static string KeptNote(int step, int? total, string reason) =>
        $"{Label(step, total)} kept: the final photo-real view could not be made ({reason}).";
}
