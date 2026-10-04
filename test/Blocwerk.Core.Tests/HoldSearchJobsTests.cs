// <copyright file="HoldSearchJobsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Jobs;
using Blocwerk.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>"Find holds from all photos" as a background job: lifecycle, progress and one search per wall.</summary>
public sealed class HoldSearchJobsTests : IDisposable
{
    private static readonly HoldProposalRunResult Done = new(12, 300, 20, 5, 4, "test");
    private readonly MutableTestClock clock = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly HoldSearchJobs jobs;
    private readonly Guid wallId = Guid.NewGuid();

    public HoldSearchJobsTests() => jobs = new HoldSearchJobs(clock);

    public void Dispose() => jobs.Dispose();

    [Fact]
    public async Task ASearch_GoesFromQueuedThroughRunningToSucceeded_WithItsResult()
    {
        var finish = new TaskCompletionSource<HoldProposalRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<HoldSearchProgress>? reporter = null;
        var reported = new TaskCompletionSource();

        Assert.True(jobs.TryStart(wallId, (progress, _) => { reporter = progress; reported.SetResult(); return finish.Task; }, out var started));
        Assert.Equal(JobStates.Queued, started.State);
        await reported.Task;
        reporter!.Report(new HoldSearchProgress(3, 12));

        var running = jobs.Get(wallId)!;
        Assert.Equal((JobStates.Running, 3, 12, true), (running.State, running.Done, running.Total, running.Active));
        clock.Advance(TimeSpan.FromMinutes(2));
        finish.SetResult(Done);
        var ended = await EndedAsync(wallId);

        Assert.Equal((JobStates.Succeeded, 12, Done), (ended.State, ended.Done, ended.Result));
        Assert.Equal(clock.GetUtcNow(), ended.EndedAt);
        Assert.False(ended.Active);
    }

    [Fact]
    public async Task ASecondSearchOfTheSameWall_IsRefusedWhileOneIsActive_ButOtherWallsAndLaterRunsAreNot()
    {
        var release = new TaskCompletionSource<HoldProposalRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        HoldSearchJobs.Work counted = (_, _) => { Interlocked.Increment(ref runs); return release.Task; };

        Assert.True(jobs.TryStart(wallId, counted, out _));
        Assert.False(jobs.TryStart(wallId, counted, out var existing));
        Assert.True(existing.Active);
        Assert.True(jobs.TryStart(Guid.NewGuid(), counted, out _));
        release.SetResult(Done);
        await EndedAsync(wallId);

        Assert.True(jobs.TryStart(wallId, (_, _) => Task.FromResult(Done), out _));
        await EndedAsync(wallId);
        Assert.Equal(2, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task AFailedSearch_KeepsAUserFacingReason_ButHidesAnUnexpectedOne()
    {
        jobs.TryStart(wallId, (_, _) => throw new UserFacingException("No 3D model to search."), out _);
        var refused = await EndedAsync(wallId);
        jobs.TryStart(wallId, (_, _) => throw new InvalidOperationException("connection string leaked"), out _);
        var crashed = await EndedAsync(wallId);

        Assert.Equal((JobStates.Failed, "No 3D model to search."), (refused.State, refused.Error));
        Assert.Equal(JobStates.Failed, crashed.State);
        Assert.DoesNotContain("leaked", crashed.Error);
    }

    [Fact]
    public async Task AShutdown_CancelsARunningSearch()
    {
        var started = new TaskCompletionSource();
        jobs.TryStart(wallId, async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Done; }, out _);
        await started.Task;

        jobs.Dispose();

        Assert.Equal(JobStates.Cancelled, (await EndedAsync(wallId)).State);
    }

    [Fact]
    public async Task TheJobList_ShowsARunningSearch_AndAnEndedOneOnlyWithinItsWindow()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var release = new TaskCompletionSource<HoldProposalRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<HoldSearchProgress>? reporter = null;
        jobs.TryStart(h.WallId, (progress, _) => { reporter = progress; return release.Task; }, out _);
        await WaitAsync(() => reporter is not null);
        reporter!.Report(new HoldSearchProgress(5, 10));
        var reader = new JobProgressReader(h.RootContextFactory, clock: clock, holdSearches: jobs);
        var scope = new JobProgressScope(null, TimeSpan.FromHours(1));

        var running = Assert.Single((await reader.ReadAsync(scope, default)).Jobs);
        release.SetResult(Done);
        await EndedAsync(h.WallId);
        var ended = Assert.Single((await reader.ReadAsync(scope, default)).Jobs);
        clock.Advance(TimeSpan.FromHours(2));
        var gone = (await reader.ReadAsync(scope, default)).Jobs;

        Assert.Equal(($"holdSearch:{h.WallId}", JobKinds.HoldSearch, JobStates.Running, 50.0), (running.Id, running.Kind, running.State, running.Percent));
        Assert.Equal("5 of 10 photos searched", running.Detail);
        Assert.Equal((JobStates.Succeeded, 100.0), (ended.State, ended.Percent));
        Assert.Empty(gone);
    }

    [Fact]
    public async Task TheService_ChecksTheCallerFirst_AndRefusesADoubleStart()
    {
        var release = new TaskCompletionSource<HoldProposalRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proposals = Substitute.For<IHoldProposalService>();
        proposals.FindInBackgroundAsync(wallId, Arg.Any<IProgress<HoldSearchProgress>?>(), Arg.Any<CancellationToken>()).Returns(release.Task);
        using var provider = new ServiceCollection().AddSingleton(proposals).BuildServiceProvider();
        var service = new HoldSearchService(proposals, jobs, provider.GetRequiredService<IServiceScopeFactory>());

        var first = await service.StartAsync(wallId);
        var second = await Assert.ThrowsAsync<UserFacingException>(() => service.StartAsync(wallId));
        proposals.EnsureCanFindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new UnauthorizedAccessException()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StartAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(wallId));
        release.SetResult(Done);

        Assert.True(first.Active);
        Assert.Contains("already running", second.Message);
        Assert.Equal(JobStates.Succeeded, (await EndedAsync(wallId)).State);
    }

    private async Task<HoldSearchStatus> EndedAsync(Guid id)
    {
        await WaitAsync(() => jobs.Get(id) is { Active: false });
        return jobs.Get(id)!;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the background search");
            await Task.Delay(10);
        }
    }
}
