// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Blocwerk.Core.Telemetry;

/// <summary>One gauge series: the jobs of one kind in one stage and state.</summary>
/// <param name="Kind">The job kind (<c>Jobs.JobKinds</c>).</param>
/// <param name="Stage">The stage key.</param>
/// <param name="State"><c>queued</c> or <c>running</c>.</param>
/// <param name="Count">How many jobs.</param>
/// <param name="MeanPercent">Their mean progress (0..100), null when none reports one.</param>
/// <param name="MaxEtaSeconds">The longest remaining time among them, null when none is known.</param>
public sealed record JobGaugeRow(string Kind, string Stage, string State, int Count, double? MeanPercent, double? MaxEtaSeconds);

/// <summary>
/// The long-running job instruments, on the same <see cref="Otel.Meter"/> as <see cref="BlocwerkMetrics"/> (so they reach the
/// OTLP exporter with no extra wiring). Tags are low-cardinality only: job kind, stage, state or outcome; never a wall or job
/// id. The gauges read the latest snapshot the job telemetry collector published.
/// </summary>
public static class JobMetrics
{
    public static readonly Histogram<double> StageDuration = Otel.Meter.CreateHistogram<double>(
        "blocwerk.jobs.stage.duration", unit: "s",
        description: "How long a stage of a long-running job took (tagged kind, stage, outcome).");

    public static readonly Counter<long> Failures = Otel.Meter.CreateCounter<long>(
        "blocwerk.jobs.failures", unit: "{failure}", description: "Long-running jobs or stages that failed (tagged kind, stage).");

    private const string RunningState = "running";

    private static IReadOnlyList<JobGaugeRow> rows = [];

    static JobMetrics()
    {
        Otel.Meter.CreateObservableGauge(
            "blocwerk.jobs.active",
            () => Volatile.Read(ref rows).Select(r => new Measurement<long>(r.Count, Tags(r, withState: true))),
            unit: "{job}",
            description: "Queued and running long-running jobs (tagged kind, stage, state).");

        Otel.Meter.CreateObservableGauge(
            "blocwerk.jobs.progress",
            () => Volatile.Read(ref rows).Where(r => r.State == RunningState && r.MeanPercent is not null)
                .Select(r => new Measurement<double>(r.MeanPercent!.Value, Tags(r, withState: false))),
            unit: "%",
            description: "Mean progress of the running jobs of a kind and stage.");

        Otel.Meter.CreateObservableGauge(
            "blocwerk.jobs.eta",
            () => Volatile.Read(ref rows).Where(r => r.State == RunningState && r.MaxEtaSeconds is not null)
                .Select(r => new Measurement<double>(r.MaxEtaSeconds!.Value, Tags(r, withState: false))),
            unit: "s",
            description: "The longest known remaining time among the running jobs of a kind and stage.");
    }

    /// <summary>Registers the gauges (runs the type initializer). Called by the collector on start.</summary>
    public static void Initialize()
    {
        // Referencing any member is enough to trigger the type initializer.
    }

    /// <summary>Publishes the latest gauge rows (progress and remaining time are reported for the running rows only).</summary>
    public static void Publish(IReadOnlyList<JobGaugeRow> latest) => Volatile.Write(ref rows, latest);

    /// <summary>Records one ended stage: its duration, and a failure when it failed.</summary>
    public static void RecordStage(string kind, string stage, string outcome, double seconds, bool failed)
    {
        var tags = new TagList { { "kind", kind }, { "stage", stage }, { "outcome", outcome } };
        StageDuration.Record(seconds, tags);
        if (failed)
        {
            Failures.Add(1, new TagList { { "kind", kind }, { "stage", stage } });
        }
    }

    private static KeyValuePair<string, object?>[] Tags(JobGaugeRow r, bool withState) => withState
        ? [new("kind", r.Kind), new("stage", r.Stage), new("state", r.State)]
        : [new("kind", r.Kind), new("stage", r.Stage)];
}
