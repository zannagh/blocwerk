// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Jobs;

/// <summary>
/// GPU jobs: the training on a runner (step, rate, trainer numbers), and the server's finish of a delivered view (and of a
/// pending preview) as its own job.
/// </summary>
public sealed partial class JobProgressReader
{
    private static async Task<List<JobProgressItem>> GpuItemsAsync(BlocwerkDbContext db, JobProgressReadContext context, CancellationToken ct)
    {
        var walls = context.Walls;
        var since = context.Since;
        var scoped = db.GpuJobs.AsNoTracking().Where(j => walls == null || walls.Contains(j.WallId));

        // Running work all, ended work the newest MaxRows: the cap never cuts off a running job.
        var active = await scoped
            .Where(j => j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running
                        || (j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null) || j.PreviewFinishJobId != null)
            .OrderByDescending(j => j.CreatedAt)
            .Take(MaxActiveRows)
            .Select(j => new { Job = j, Runner = j.ClaimedByRunner == null ? null : j.ClaimedByRunner.Name })
            .ToListAsync(ct);
        var ids = active.Select(r => r.Job.Id).ToList();
        var ended = await scoped
            .Where(j => (j.CompletedAt >= since || j.InstalledAt >= since) && !ids.Contains(j.Id))
            .OrderByDescending(j => j.CompletedAt)
            .Take(MaxRows)
            .Select(j => new { Job = j, Runner = j.ClaimedByRunner == null ? null : j.ClaimedByRunner.Name })
            .ToListAsync(ct);
        var rows = active.Concat(ended);

        var items = new List<JobProgressItem>();
        foreach (var row in rows)
        {
            if (TrainingItem(row.Job, row.Runner, context) is { } training)
            {
                items.Add(training);
            }

            items.AddRange(FinishItems(row.Job, context));
        }

        return items;
    }

    private static JobProgressItem? TrainingItem(GpuJob job, string? runner, JobProgressReadContext context)
    {
        var state = TrainingState(job);
        if (!JobStates.IsActive(state) && !(job.CompletedAt >= context.Since))
        {
            return null;
        }

        var stage = TrainingStage(job, state);
        var (eta, source) = state == JobStates.Running ? TrainingEta(job, stage, context) : (null, null);
        return new JobProgressItem
        {
            Id = $"{JobKinds.GpuTraining}:{job.Id}",
            Kind = JobKinds.GpuTraining,
            State = state,
            Stage = stage,
            Detail = job.Stage,
            Percent = JobEta.Percent(job.Progress),
            Step = job.Step,
            TotalSteps = job.TotalSteps,
            EtaSeconds = eta,
            EtaSource = source,
            StartedAt = job.ClaimedAt,
            UpdatedAt = job.HeartbeatAt ?? job.ClaimedAt ?? job.CreatedAt,
            EndedAt = JobStates.IsActive(state) ? null : job.CompletedAt,
            LastError = job.Error,
            WallId = job.WallId,
            CaptureId = job.CaptureId,
            GpuJobId = job.Id,
            RunnerName = state == JobStates.Running ? runner : null,
            Attempts = job.Attempts,
            Training = Facts(job, context.Now),
        };
    }

    /// <summary>The step rate of the current claim when it is measurable, else the history of the quality's trainings.</summary>
    private static (double? Eta, string? Source) TrainingEta(GpuJob job, string stage, JobProgressReadContext context)
    {
        var rate = JobProgressReadContext.FromRate(job.Step, job.TotalSteps, job.StepAnchor, job.StepAnchorAt, context.Now);
        if (rate.Eta is not null || stage != "training")
        {
            return rate;
        }

        return context.FromHistory(JobKinds.GpuTraining, JobStageHistory.TrainingStage(job.Quality), job.ClaimedAt);
    }

    /// <summary>Delivered by the runner, the training succeeded, even when the server's finish failed afterwards.</summary>
    private static string TrainingState(GpuJob job) => job.Status switch
    {
        GpuJobStatus.Queued => JobStates.Queued,
        GpuJobStatus.Claimed or GpuJobStatus.Running => JobStates.Running,
        GpuJobStatus.Succeeded => JobStates.Succeeded,
        GpuJobStatus.Failed when job.ResultPath is not null => JobStates.Succeeded,
        GpuJobStatus.Failed => JobStates.Failed,
        _ => JobStates.Cancelled,
    };

    private static string TrainingStage(GpuJob job, string state)
    {
        if (state != JobStates.Running)
        {
            return state == JobStates.Queued ? "queued" : "done";
        }

        if (job.Stage?.StartsWith(GpuJobQueue.UploadingStage, StringComparison.Ordinal) == true)
        {
            return "upload";
        }

        return job.Status == GpuJobStatus.Claimed || (job.Step is null && job.Stage?.Contains("download", StringComparison.Ordinal) == true)
            ? "download"
            : "training";
    }

    private static JobTrainingFacts Facts(GpuJob job, DateTimeOffset now)
    {
        var rate = job is { Step: { } step, StepAnchor: { } anchor, StepAnchorAt: { } at }
            ? JobEta.Rate(step - anchor, (now - at).TotalSeconds)
            : null;
        return new JobTrainingFacts(job.Loss, job.SplatCount, job.PreviewStep, job.PreviewInstalledStep, rate is { } r ? Math.Round(r, 3) : null);
    }

    private static IEnumerable<JobProgressItem> FinishItems(GpuJob job, JobProgressReadContext context)
    {
        if (job.PreviewFinishJobId is not null && job.PreviewStep is { } previewStep)
        {
            yield return FinishItem(job, "preview") with { Step = previewStep, TotalSteps = job.TotalSteps };
        }

        var delivered = job.Status == GpuJobStatus.Succeeded || (job.Status == GpuJobStatus.Failed && job.ResultPath is not null);
        if (!delivered || job.CompletedAt is not { } completed)
        {
            yield break;
        }

        var running = job is { Status: GpuJobStatus.Succeeded, InstalledAt: null };
        if (!running && !((job.InstalledAt ?? completed) >= context.Since))
        {
            yield break;
        }

        var stage = job.RefinishStateJson is null ? "finishing" : "refinishing";
        var (eta, source) = running ? context.FromHistory(JobKinds.Finish, "finishing", completed) : (null, null);
        yield return FinishItem(job, stage) with
        {
            Id = $"{JobKinds.Finish}:{job.Id}",
            State = running ? JobStates.Running : job.Status == GpuJobStatus.Failed ? JobStates.Failed : JobStates.Succeeded,
            EtaSeconds = eta,
            EtaSource = source,
            StartedAt = completed,
            UpdatedAt = job.InstalledAt ?? completed,
            EndedAt = running ? null : job.InstalledAt ?? completed,
            LastError = job.Status == GpuJobStatus.Failed ? job.Error : null,
        };
    }

    private static JobProgressItem FinishItem(GpuJob job, string stage) => new()
    {
        Id = $"{JobKinds.Finish}:{job.Id}:{stage}",
        Kind = JobKinds.Finish,
        State = JobStates.Running,
        Stage = stage,
        WallId = job.WallId,
        CaptureId = job.CaptureId,
        GpuJobId = job.Id,
    };
}
