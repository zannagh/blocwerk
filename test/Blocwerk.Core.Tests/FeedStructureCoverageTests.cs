// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Tests.StructureFeedKit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// PR 1.2b's part of the coverage guard (every wall-structure entity is mapped, none is still pending), and a bulk call
/// site that now opens a <see cref="FeedScope"/>: its save writes one resync instead of a row per hold.
/// </summary>
public sealed class FeedStructureCoverageTests
{
    private static readonly Type[] Structure =
    [
        typeof(Wall), typeof(WallMember), typeof(WallSegment), typeof(WallPanel), typeof(HoldLink), typeof(WallVolume),
        typeof(Hold), typeof(Boulder), typeof(BoulderHold), typeof(BoulderSetter), typeof(BoulderHoldMove),
    ];

    private static readonly Type[] Decided = [typeof(WallReset), typeof(HoldGenerationLink), typeof(WallTemperatureReading)];

    [Fact]
    public void TheStructureFamily_IsMapped_AndNothingOfItIsPending()
    {
        var mapped = new FeedMapperRegistry(FeedMappers.Create()).MappedTypes;

        Assert.All(Structure, t => Assert.Contains(t, mapped));
        Assert.All(Decided, t => Assert.True(FeedCoverage.ReviewedExcluded.ContainsKey(t), t.Name));
        Assert.DoesNotContain(FeedCoverage.Pending.Values, reason => reason.StartsWith("1.2b:", StringComparison.Ordinal));
        Assert.All(Structure.Concat(Decided), t => Assert.False(FeedCoverage.Pending.ContainsKey(t), t.Name));
    }

    [Fact]
    public async Task ClearingAutoDetectedHolds_WritesOneResync_NotARowPerHold()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 5);
        await using (var seed = harness.CreateContext())
        {
            await seed.Holds.ExecuteUpdateAsync(s => s.SetProperty(h => h.IsAutoDetected, true));
        }

        var cs = harness.DbContextFactory.ConnectionString;
        var journal = new ChangeJournal(() => TestDb.Create(cs));
        IInterceptor[] interceptors = [new ChangeJournalInterceptor(journal), Interceptor()];
        var service = new WallService(
            new InterceptedDbContextFactory(cs, interceptors), harness.CurrentUser, harness.HoldDetection, harness.ActivityLog,
            NullLogger<WallService>.Instance, changeJournal: journal);

        await service.ClearAutoDetectedHoldsAsync(harness.WallId);

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.WallResync, harness.WallId), (row.Kind, row.WallId!.Value));
        Assert.Contains("hold-clear-autodetected", row.DataJson);
    }
}
