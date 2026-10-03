// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics.Metrics;
using Blocwerk.Core.Jobs;
using Blocwerk.Core.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The job telemetry is derived from the same snapshot as the API: gauges per kind, stage and state (low cardinality), and
/// each stage that ended recorded once (duration, failures), never again after a restart or a second read.
/// </summary>
public class JobTelemetryTests
{
    private static readonly DateTimeOffset Now = JobProgressReaderTests.Now;

    [Fact]
    public void Gauges_GroupTheActiveJobs_ByKindStageAndState()
    {
        var snapshot = Snapshot(
            Job("gpuTraining:1", JobKinds.GpuTraining, JobStates.Running, "training", percent: 40, eta: 100),
            Job("gpuTraining:2", JobKinds.GpuTraining, JobStates.Running, "training", percent: 60, eta: 900),
            Job("gpuTraining:3", JobKinds.GpuTraining, JobStates.Queued, "queued"),
            Job("capture:4", JobKinds.Capture, JobStates.Succeeded, "succeeded"));

        var rows = JobTelemetry.Gauges(snapshot);

        Assert.Equal(
            [
                new JobGaugeRow(JobKinds.GpuTraining, "queued", JobStates.Queued, 1, null, null),
                new JobGaugeRow(JobKinds.GpuTraining, "training", JobStates.Running, 2, 50, 900),
            ],
            rows);
    }

    [Fact]
    public void Ended_IsEveryClosedCaptureStage_AndEveryOtherEndedJobWithAKnownStart()
    {
        var capture = Job("capture:1", JobKinds.Capture, JobStates.Running, "texturing") with
        {
            Stages =
            [
                new JobStageSpan("solving", Now.AddMinutes(-3), Now.AddMinutes(-1), "failed", 120),
                new JobStageSpan("texturing", Now.AddMinutes(-1), null, null, 60),
            ],
        };
        var training = Job("gpuTraining:2", JobKinds.GpuTraining, JobStates.Succeeded, "done") with { StartedAt = Now.AddHours(-1), EndedAt = Now };
        var unknownStart = Job("resolve:3", JobKinds.Resolve, JobStates.Failed, "resolve") with { EndedAt = Now };

        var ended = JobTelemetry.Ended(Snapshot(capture, training, unknownStart)).ToList();

        Assert.Equal([("solving", "failed", 120.0), ("training", JobStates.Succeeded, 3600.0)], ended.Select(e => (e.Stage, e.Outcome, e.Seconds)));
        Assert.True(ended[0].Failed);
    }

    [Fact]
    public void TheCollector_RecordsEachEndedStageOnce_AndNothingFromBeforeItStarted()
    {
        var collector = new JobTelemetryCollector(Substitute.For<IJobProgressReader>(), NullLogger<JobTelemetryCollector>.Instance);
        var before = Job("followUp:1:place", JobKinds.FollowUp, JobStates.Succeeded, "place") with { StartedAt = Now.AddMinutes(-5), EndedAt = Now.AddMinutes(-4) };
        var later = Job("followUp:1:links", JobKinds.FollowUp, JobStates.Failed, "links") with { StartedAt = Now.AddMinutes(-2), EndedAt = Now.AddMinutes(-1) };
        var measured = new List<(string Name, double Value)>();
        using var listener = Listen(measured);

        Assert.Equal(0, collector.Apply(Snapshot(before)));
        Assert.Equal(1, collector.Apply(Snapshot(before, later)));
        Assert.Equal(0, collector.Apply(Snapshot(before, later)));

        Assert.Contains(("blocwerk.jobs.stage.duration", 60.0), measured);
        Assert.Single(measured, m => m.Name == "blocwerk.jobs.failures");
    }

    private static MeterListener Listen(List<(string Name, double Value)> measured)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == Otel.Meter && instrument.Name.StartsWith("blocwerk.jobs.", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((i, v, _, _) => Record(measured, i.Name, v));
        listener.SetMeasurementEventCallback<long>((i, v, _, _) => Record(measured, i.Name, v));
        listener.Start();
        return listener;
    }

    private static void Record(List<(string Name, double Value)> measured, string name, double value)
    {
        lock (measured)
        {
            measured.Add((name, value));
        }
    }

    private static JobProgressSnapshot Snapshot(params JobProgressItem[] jobs) => new(Now, 1, jobs);

    private static JobProgressItem Job(string id, string kind, string state, string stage, double? percent = null, double? eta = null) =>
        new() { Id = id, Kind = kind, State = state, Stage = stage, Percent = percent, EtaSeconds = eta, WallId = Guid.Empty };
}
