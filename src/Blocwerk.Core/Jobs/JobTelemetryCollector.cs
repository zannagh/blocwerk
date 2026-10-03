// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics;
using Blocwerk.Core.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Jobs;

/// <summary>
/// Feeds the job telemetry from the progress read model, like <see cref="TelemetryStatsCollector"/> feeds the totals: every
/// <see cref="PollInterval"/> it reads every wall's jobs, publishes the gauge rows, and records each stage that ended since
/// (duration histogram, failure counter, and a span with the stage's real start and end). Reading the derived model instead
/// of hooking each pipeline keeps one source of truth for the API, the page and the dashboards.
/// </summary>
/// <remarks>
/// The first read only remembers what had already ended (a restart must not record it twice). A stage seen once is
/// remembered for <see cref="Window"/>; the reads look back as far, so nothing ends unseen between two reads.
/// </remarks>
public sealed class JobTelemetryCollector(IJobProgressReader reader, ILogger<JobTelemetryCollector> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly Dictionary<string, DateTimeOffset> recorded = new(StringComparer.Ordinal);
    private readonly HashSet<(string Kind, string Stage, string State)> series = [];
    private bool primed;

    /// <summary>Applies one snapshot: publishes the gauges and records the newly ended stages. Returns how many it recorded.</summary>
    public int Apply(JobProgressSnapshot snapshot)
    {
        var rows = JobTelemetry.Gauges(snapshot);
        foreach (var row in rows)
        {
            series.Add((row.Kind, row.Stage, row.State));
        }

        // A series whose jobs are gone reports 0 instead of keeping its last value.
        var zeros = series.Where(s => !rows.Any(r => (r.Kind, r.Stage, r.State) == s)).Select(s => new JobGaugeRow(s.Kind, s.Stage, s.State, 0, null, null));
        JobMetrics.Publish([.. rows, .. zeros]);

        var fresh = 0;
        var since = snapshot.GeneratedAt - Window;
        foreach (var stage in JobTelemetry.Ended(snapshot).Where(s => s.EndedAt >= since && !recorded.ContainsKey(s.Key)))
        {
            recorded[stage.Key] = stage.EndedAt;
            if (primed)
            {
                Record(stage);
                fresh++;
            }
        }

        primed = true;
        foreach (var old in recorded.Where(r => r.Value < snapshot.GeneratedAt - (2 * Window)).Select(r => r.Key).ToList())
        {
            recorded.Remove(old);
        }

        return fresh;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        JobMetrics.Initialize();
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                Apply(await reader.ReadAsync(new JobProgressScope(null, Window), stoppingToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not read the long-running jobs for telemetry");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static void Record(JobStageRecord stage)
    {
        JobMetrics.RecordStage(stage.Item.Kind, stage.Stage, stage.Outcome, stage.Seconds, stage.Failed);
        using var activity = Otel.ActivitySource.StartActivity(
            $"job {stage.Item.Kind} {stage.Stage}", ActivityKind.Internal, parentContext: default, startTime: stage.StartedAt);
        if (activity is null)
        {
            return;
        }

        activity.SetTag("job.kind", stage.Item.Kind);
        activity.SetTag("job.stage", stage.Stage);
        activity.SetTag("job.outcome", stage.Outcome);
        activity.SetTag("job.id", stage.Item.Id);
        activity.SetTag("wall", BlocwerkMetrics.AnonymizeWallId(stage.Item.WallId));
        if (stage.Failed)
        {
            activity.SetStatus(ActivityStatusCode.Error, stage.Item.LastError);
        }

        activity.SetEndTime(stage.EndedAt.UtcDateTime);
    }
}
