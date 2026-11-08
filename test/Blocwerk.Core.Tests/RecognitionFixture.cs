// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>A <see cref="RecognitionQueue"/> over <see cref="RunnerFixture"/>'s store, clock and runner tables.</summary>
internal sealed class RecognitionFixture : IDisposable
{
    private RecognitionFixture(RunnerFixture runners, RecognitionQueueOptions options)
    {
        Runners = runners;
        Options = options;
        Queue = NewQueue();
    }

    public RunnerFixture Runners { get; }

    public WallTestHarness Harness => Runners.Harness;

    public MutableTestClock Clock => Runners.Clock;

    public RecognitionQueueOptions Options { get; }

    public RecognitionWaiters Waiters { get; } = new();

    public RecognitionQueue Queue { get; private set; }

    /// <summary>Waiters poll fast by default, so a test never waits seconds for a missed signal.</summary>
    public static async Task<RecognitionFixture> CreateAsync(
        WallTestHarness harness, RecognitionQueueOptions? options = null, GpuRunnerOptions? runnerOptions = null) =>
        new(await RunnerFixture.CreateAsync(harness, runnerOptions), options ?? new RecognitionQueueOptions { WaiterPoll = TimeSpan.FromMilliseconds(50) });

    /// <summary>The app restarted: a new queue (and start time) over the same database.</summary>
    public void Restart()
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        Queue = NewQueue();
    }

    /// <summary>A runner of the harness owner serving <paramref name="walls"/> (default: the harness wall) with recognition kinds.</summary>
    public async Task<GpuRunner> RunnerAsync(string name, int memoryBudgetMb = 2048, string? capabilities = null, params Guid[] walls)
    {
        var (runner, _) = await Runners.AddRunnerAsync(name, walls: walls.Length == 0 ? [Harness.WallId] : walls);
        runner.Capabilities = capabilities ?? string.Join(',', RecognitionKinds.All);
        runner.MemoryBudgetMb = memoryBudgetMb;
        await using var db = Harness.CreateContext();
        await db.GpuRunners.Where(r => r.Id == runner.Id).ExecuteUpdateAsync(
            s => s.SetProperty(r => r.Capabilities, runner.Capabilities).SetProperty(r => r.MemoryBudgetMb, runner.MemoryBudgetMb));
        return runner;
    }

    /// <summary>Queues a job one second after the previous one, on the harness wall unless <paramref name="wallId"/> says otherwise.</summary>
    public Task<RecognitionJob> EnqueueAsync(
        RecognitionLane lane, string kind = RecognitionKinds.Markers, int? memoryMb = null, Guid? wallId = null, string? callerKey = null)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        ComputeJobPart[] parts =
        [
            ComputeJobPart.Json("request", "{\"version\":1}"),
            ComputeJobPart.File("photo", "photo.jpg", CaptureScenario.TinyJpeg(), "image/jpeg"),
        ];
        return Queue.EnqueueAsync(new RecognitionRequest(kind, lane, wallId ?? Harness.WallId, parts, memoryMb, callerKey), CancellationToken.None);
    }

    /// <summary>The job as stored now.</summary>
    public async Task<RecognitionJob> RowAsync(Guid jobId)
    {
        await using var db = Harness.CreateContext();
        return await db.RecognitionJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    public void Dispose() => Runners.Dispose();

    private RecognitionQueue NewQueue() =>
        new(Harness.RootContextFactory, Runners.Files, Options, Runners.Queue, new GpuJobSignal(), Waiters,
            NullLogger<RecognitionQueue>.Instance, Clock);
}
