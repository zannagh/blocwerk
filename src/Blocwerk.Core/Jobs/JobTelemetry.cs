// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Telemetry;

namespace Blocwerk.Core.Jobs;

/// <summary>One stage that ended: what the duration histogram, the failure counter and a trace span are made of.</summary>
/// <param name="Key">Unique per ended stage, so it is recorded once.</param>
/// <param name="Item">The job it belongs to.</param>
/// <param name="Stage">The stage key (low cardinality).</param>
/// <param name="Outcome">The job state it ended in (<see cref="JobStates"/>) or the timeline outcome.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="EndedAt">When it ended.</param>
public sealed record JobStageRecord(
    string Key, JobProgressItem Item, string Stage, string Outcome, DateTimeOffset StartedAt, DateTimeOffset EndedAt)
{
    /// <summary>Whether it failed.</summary>
    public bool Failed => Outcome is JobStates.Failed;

    /// <summary>How long it took, in seconds.</summary>
    public double Seconds => Math.Max(0, (EndedAt - StartedAt).TotalSeconds);
}

/// <summary>Pure mappings from a snapshot to the telemetry: the gauge rows and the stages that ended.</summary>
public static class JobTelemetry
{
    /// <summary>The active jobs grouped by kind, stage and state.</summary>
    public static List<JobGaugeRow> Gauges(JobProgressSnapshot snapshot) =>
        snapshot.Jobs
            .Where(j => JobStates.IsActive(j.State))
            .GroupBy(j => (j.Kind, Stage: MetricStage(j), j.State))
            .Select(g => new JobGaugeRow(
                g.Key.Kind,
                g.Key.Stage,
                g.Key.State,
                g.Count(),
                g.Any(j => j.Percent is not null) ? Math.Round(g.Where(j => j.Percent is not null).Average(j => j.Percent!.Value), 1) : null,
                g.Max(j => j.EtaSeconds)))
            .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Stage, StringComparer.Ordinal).ThenBy(r => r.State, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Every stage the snapshot shows as ended: a capture's closed timeline stages one by one, any other job as a whole
    /// (when its start is known).
    /// </summary>
    public static IEnumerable<JobStageRecord> Ended(JobProgressSnapshot snapshot)
    {
        foreach (var job in snapshot.Jobs)
        {
            if (job.Kind == JobKinds.Capture)
            {
                foreach (var span in job.Stages ?? [])
                {
                    if (span is { EndedAt: { } end, Outcome: { } outcome })
                    {
                        yield return new JobStageRecord($"{job.Id}:{span.Stage}:{span.StartedAt.UtcTicks}", job, span.Stage, outcome, span.StartedAt, end);
                    }
                }

                continue;
            }

            if (!JobStates.IsActive(job.State) && job is { StartedAt: { } start, EndedAt: { } ended })
            {
                yield return new JobStageRecord($"{job.Id}:{ended.UtcTicks}", job, MetricStage(job), job.State, start, ended);
            }
        }
    }

    /// <summary>The stage tag of a job: its stage, except that an ended training reports as <c>training</c>.</summary>
    internal static string MetricStage(JobProgressItem job) =>
        job.Kind == JobKinds.GpuTraining && !JobStates.IsActive(job.State) ? "training" : job.Stage ?? "unknown";
}
