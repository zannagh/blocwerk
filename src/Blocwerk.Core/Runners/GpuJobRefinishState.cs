// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Runners;

/// <summary>
/// A GPU job's and its capture's state before a re-finish (<see cref="GpuJob.RefinishStateJson"/>): restored when the
/// re-finish fails, because the view installed before is still the model's view. <c>UsedPreview</c>: the re-finish took
/// the job's installed preview as its result.
/// </summary>
public sealed record GpuJobRefinishState(
    GpuJobStatus JobStatus,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? JobCompletedAt,
    string? JobError,
    string? JobStage,
    bool UsedPreview,
    WallCaptureStatus CaptureStatus,
    string? CaptureError,
    string? CaptureStage,
    DateTimeOffset? CaptureCompletedAt)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static GpuJobRefinishState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GpuJobRefinishState>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Puts the job back as it was before the re-finish (tracked, not saved).</summary>
    public void RestoreJob(GpuJob job)
    {
        if (UsedPreview)
        {
            job.InstalledPreviewPath = job.ResultPath;
            job.ResultPath = null;
            job.ResultFormat = null;
            job.ResultBytes = null;
            job.ResultStatsJson = null;
        }

        job.Status = JobStatus;
        job.InstalledAt = InstalledAt;
        job.CompletedAt = JobCompletedAt;
        job.Error = JobError;
        job.Stage = JobStage;
        job.FinishJobId = null;
        job.LeaseExpiresAt = null;
        job.RefinishStateJson = null;
    }
}
