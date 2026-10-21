// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.StructureFeedKit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The PR 1.2b review findings: bulk hold writers collapse, user-addressed rows survive a collapse, a failed lookup never
/// fails a save in a transaction, kiosk-only member changes stay out, staged work stays with the editors, and a restamped
/// boulder reaches the generation it left.
/// </summary>
public sealed class FeedReviewFixTests
{
    [Fact]
    public async Task AWholeWallVolumePlacement_WritesOneResync_NotARowPerHold()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 30);
        var model = new WallGeometryModel { WallId = harness.WallId, Json = "{}", Source = "test", IsActive = true };
        await using (var seed = harness.CreateContext())
        {
            seed.WallGeometryModels.Add(model);
            await seed.SaveChangesAsync();
            await seed.Holds.ExecuteUpdateAsync(s => s.SetProperty(h => h.VolumePlacementJson, "{}"));
        }

        int changed;
        await using (var db = Context(harness))
        {
            (_, changed) = await WallVolumeService.PlaceHoldsAsync(db, harness.WallId, model.Id, CancellationToken.None);
        }

        Assert.Equal(30, changed);
        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.WallResync, harness.WallId), (row.Kind, row.WallId!.Value));
        Assert.Contains("volume-placement", row.DataJson);
    }

    [Fact]
    public async Task AccessRevoked_SurvivesAScope_BesideTheWallsResync()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var user = await harness.AddMemberAsync("gone@test", WallRole.Moderator);

        await using (var db = Context(harness))
        using (FeedScope.Begin(harness.WallId, "probe-op"))
        {
            db.WallMembers.Remove(await db.WallMembers.SingleAsync(m => m.UserId == user.Id));
            await db.SaveChangesAsync();
        }

        var rows = await RowsAsync(harness);
        Assert.Equal([FeedKinds.WallResync, FeedKinds.AccessRevoked], rows.Select(r => r.Kind));
        Assert.Equal((FeedAudience.User, user.Id), (rows[1].Audience, rows[1].AudienceUserId));
    }

    [Fact]
    public Task AFailedLookupInATransaction_LeavesTheSaveAndTheTransactionIntact_Sqlite() => FailedLookupAsync(new WallTestHarness());

    [PostgresFact]
    public Task AFailedLookupInATransaction_LeavesTheSaveAndTheTransactionIntact_Postgres() =>
        FailedLookupAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public async Task AKioskOnlyMemberChange_WritesNothing()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);

        await using (var db = Context(harness))
        {
            var member = await db.WallMembers.SingleAsync();
            (member.KioskConsentedAt, member.KioskPinHash, member.KioskPinLength) = (DateTimeOffset.UtcNow, "hash", 4);
            await db.SaveChangesAsync();
        }

        Assert.Empty(await RowsAsync(harness));
    }

    [Fact]
    public async Task InsideAStagedAudience_RowsGoToTheSetters_AndUserRowsKeepTheirs()
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        var user = await harness.AddMemberAsync("demoted@test", WallRole.Admin);

        await using (var db = Context(harness))
        using (FeedAudienceScope.Begin(FeedAudience.Setters))
        {
            (await db.Holds.SingleAsync(h => h.Id == hold.Id)).X = 0.4;
            (await db.WallMembers.SingleAsync(m => m.UserId == user.Id)).Role = WallRole.Member;
            await db.SaveChangesAsync();
        }

        var rows = (await RowsAsync(harness)).ToDictionary(r => r.Kind);
        Assert.Equal(FeedAudience.Setters, rows[FeedKinds.HoldChanged].Audience);
        Assert.Equal(FeedAudience.Setters, rows[FeedKinds.MemberChanged].Audience);
        Assert.Equal(FeedAudience.User, rows[FeedKinds.AccessRevoked].Audience);
        Assert.Null(FeedAudienceScope.Current);
    }

    [Fact]
    public async Task ABoulderRestampedOntoAnotherGeneration_AlsoReachesTheOneItLeft()
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        var boulder = await SeedBoulderAsync(harness, hold.Id);

        await using (var db = Context(harness))
        {
            (await db.Boulders.SingleAsync()).Generation = 3;
            await db.SaveChangesAsync();
        }

        var rows = await RowsAsync(harness);
        Assert.Equal([3, 0], rows.Select(r => Payload(r).GetProperty("generation").GetInt32()));
        Assert.All(rows, r => Assert.Equal((FeedKinds.BoulderChanged, boulder.Id), (r.Kind, r.EntityId!.Value)));
    }

    // The hold is tracked, so the part knows its wall and the failed lookup can still resync it; a later save in the same
    // transaction proves the transaction survived the failed statement.
    private static async Task FailedLookupAsync(WallTestHarness harness)
    {
        using var owned = harness;
        var holds = await harness.SeedWallAsync(holdCount: 2);
        var boulder = await SeedBoulderAsync(harness, holds[0].Id);
        var breaker = new BreakingLookupInterceptor();

        await using (var db = TestDb.Create(harness.DbContextFactory.ConnectionString, Interceptor(), breaker))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var tracked = await db.Holds.SingleAsync(h => h.Id == holds[1].Id);
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = tracked.Id });
            await db.SaveChangesAsync();
            (await db.Holds.SingleAsync(h => h.Id == holds[0].Id)).X = 0.9; // the transaction is still usable
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Assert.Equal(1, breaker.Broken);
        await using var read = harness.CreateContext();
        Assert.Equal(2, await read.BoulderHolds.CountAsync(bh => bh.BoulderId == boulder.Id));
        var rows = await RowsAsync(harness);
        Assert.Equal([FeedKinds.WallResync, FeedKinds.HoldChanged], rows.Select(r => r.Kind));
        Assert.Contains(FeedCollector.MappingFailedReason, rows[0].DataJson);
    }
}
