// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Jobs;
using Blocwerk.Core.Pulse;
using Blocwerk.Web.Components.Pages.Walls;
using Blocwerk.Web.Components.Shared;
using Blocwerk.Web.State.Live;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>
/// The pages' loops without pulses (PR 1.7 review W2, N3): an idle page still re-reads at its idle interval (a circuit
/// with no viewer recovers), an active one at its safety interval, and the rows no pulse carries keep their old cadence.
/// </summary>
public sealed class PulseLoopTests : IAsyncLifetime
{
    private readonly CircuitCacheFixture f = new();
    private readonly WallPulseHub hub = new(interval: TimeSpan.Zero);

    public Task InitializeAsync() => f.StartAsync();

    public async Task DisposeAsync()
    {
        hub.Dispose();
        await f.DisposeAsync();
    }

    [Fact]
    public async Task AnIdlePage_WithNoViewer_StillReReadsAtItsIdleInterval()
    {
        f.Fake.Viewer = null; // no feed subscription, so no pulse can ever reach it
        var refreshes = 0;
        using var loop = Start(() => false, () => Interlocked.Increment(ref refreshes), idle: TimeSpan.FromMilliseconds(100));

        await CircuitCacheFixture.WaitUntilAsync(() => Volatile.Read(ref refreshes) >= 2);
    }

    [Fact]
    public async Task AnActivePage_ReReadsAtItsSafetyInterval_WithoutAPulse()
    {
        var refreshes = 0;
        using var loop = Start(() => true, () => Interlocked.Increment(ref refreshes), safety: TimeSpan.FromMilliseconds(100));

        await CircuitCacheFixture.WaitUntilAsync(() => Volatile.Read(ref refreshes) >= 2);
    }

    [Fact]
    public void TheRunnersPanel_KeepsItsOldFiveSecondCadence()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), WallGpuRunnersPanel.RunnerStateEvery);
    }

    [Theory]
    [InlineData(JobKinds.Import, JobStates.Running, true)]
    [InlineData(JobKinds.GpuTraining, JobStates.Queued, true)]
    [InlineData(JobKinds.GpuTextures, JobStates.Running, true)]
    [InlineData(JobKinds.GpuTraining, JobStates.Succeeded, false)]
    [InlineData(JobKinds.Capture, JobStates.Running, false)]
    [InlineData(JobKinds.HoldSearch, JobStates.Running, false)]
    public void TheJobList_KeepsItsInterval_ForImportsAndRunnerJobs(string kind, string state, bool interval)
    {
        var snapshot = new JobProgressSnapshot(DateTimeOffset.UtcNow, 24, [new JobProgressItem { Id = "j", Kind = kind, State = state }]);

        Assert.Equal(interval, JobProgressList.PollsOnInterval(snapshot));
        Assert.False(JobProgressList.PollsOnInterval(null));
    }

    private PulseLoop Start(Func<bool> active, Action refreshed, TimeSpan? safety = null, TimeSpan? idle = null) =>
        PulseLoop.Start(
            f.Cache,
            hub,
            f.WallId,
            [PulseTopics.Capture],
            active,
            work => work(),
            () =>
            {
                refreshed();
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            safety,
            idle);
}
