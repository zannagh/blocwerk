// <copyright file="WallEnhancementLoadFixTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>The review's load fixes: the outline step plans without the hold lock, and refinements wait for a running search.</summary>
public sealed class WallEnhancementLoadFixTests
{
    [Fact]
    public async Task TheOutlineUpgrade_TracesWithoutTheWallsHoldLock_AndTakesItOnlyToWrite()
    {
        using var harness = new WallTestHarness();
        var scenario = await OutlineUpgradeScenario.CreateAsync(harness);
        var outlines = EnrichmentFakes.Outlines();
        var freeWhileTracing = new List<bool>();
        outlines.When(o => o.OpenSession(Arg.Any<byte[]>())).Do(_ =>
        {
            try
            {
                WallHoldWriteLock.TryAcquire(harness.WallId, "busy").Dispose();
                freeWhileTracing.Add(true);
            }
            catch (UserFacingException)
            {
                freeWhileTracing.Add(false);
            }
        });

        var result = await scenario.Service(outlines).ApplyAsync(harness.WallId, new HoldOutlineUpgradeOptions());

        Assert.NotEmpty(freeWhileTracing);
        Assert.All(freeWhileTracing, Assert.True);
        Assert.True(result.Outlined > 0);
    }

    [Fact]
    public async Task QueuedRefinements_WaitWhileAHoldSearchRuns()
    {
        var scopes = Substitute.For<IServiceScopeFactory>();
        var searching = true;
        var queue = new HoldRefinementQueue(scopes, NullLogger<HoldRefinementQueue>.Instance)
        {
            SearchRunning = () => searching, BurstQuiet = TimeSpan.FromMilliseconds(20), SearchWait = TimeSpan.FromMilliseconds(20),
        };
        using var stop = new CancellationTokenSource();
        await queue.StartAsync(stop.Token);
        queue.Enqueue(Guid.NewGuid(), [Guid.NewGuid()]);

        await Task.Delay(400);
        Assert.Empty(scopes.ReceivedCalls());

        searching = false;
        for (var i = 0; i < 100 && !scopes.ReceivedCalls().Any(); i++)
        {
            await Task.Delay(20);
        }

        Assert.NotEmpty(scopes.ReceivedCalls());
        await stop.CancelAsync();
        await queue.StopAsync(CancellationToken.None);
    }
}
