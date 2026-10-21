// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A failing feed lookup never fails the user's save, not even inside an open Postgres transaction (where a failed
/// statement would abort it, 25P02): the lookups run under a savepoint. What could not be placed is dropped and logged.
/// </summary>
public sealed class SocialFeedLookupFailureTests
{
    [Fact]
    public Task AFailedPlacementLookup_KeepsTheSave_AndLogsTheDroppedRows_Sqlite() => PlacementAsync(new SocialFeedFixture(), transaction: false);

    [PostgresFact]
    public Task AFailedPlacementLookup_KeepsTheSave_AndLogsTheDroppedRows_Postgres() => PlacementAsync(new SocialFeedFixture(PostgresTestDatabase.Create()), transaction: false);

    [PostgresFact]
    public Task AFailedPlacementLookup_InsideATransaction_KeepsTheSaveAndTheCommit_Postgres() => PlacementAsync(new SocialFeedFixture(PostgresTestDatabase.Create()), transaction: true);

    [PostgresFact]
    public async Task AFailedUserWallsLookup_InsideATransaction_KeepsTheSaveAndTheCommit_Postgres()
    {
        using var fixture = new SocialFeedFixture(PostgresTestDatabase.Create());
        await fixture.SeedAsync();
        var breaker = new FailingQueryInterceptor("\"WallMembers\"") { On = false };

        await using (var db = fixture.Context(fixture.OwnerId, new CapturingLogger<FeedInterceptor>(), breaker))
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var user = await db.Users.SingleAsync(u => u.Id == fixture.OwnerId);
            user.CustomDisplayName = "Renamed in a transaction";
            breaker.On = true;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        Assert.True(breaker.Broken > 0);
        await using var check = fixture.Harness.CreateContext();
        Assert.Equal("Renamed in a transaction", (await check.Users.SingleAsync(u => u.Id == fixture.OwnerId)).CustomDisplayName);
        var row = Assert.Single(await fixture.RowsAsync());
        Assert.Equal((FeedKinds.UserProfile, FeedAudience.User), (row.Kind, row.Audience));
    }

    private static async Task PlacementAsync(SocialFeedFixture fixture, bool transaction)
    {
        using var owned = fixture;
        var boulder = await fixture.SeedAsync();
        var logger = new CapturingLogger<FeedInterceptor>();
        var breaker = new FailingQueryInterceptor(FeedBoulderResolver.LookupTag);
        var comment = new BoulderComment { BoulderId = boulder, UserId = fixture.OwnerId, Text = "kept" };

        await using (var db = fixture.Context(fixture.OwnerId, logger, breaker))
        await using (var tx = transaction ? await db.Database.BeginTransactionAsync() : null)
        {
            db.Add(comment);
            db.Add(new BoulderFavorite { BoulderId = boulder, UserId = fixture.OwnerId });
            await db.SaveChangesAsync();
            if (tx is not null)
            {
                await tx.CommitAsync();
            }
        }

        Assert.Equal(1, breaker.Broken);
        await using var check = fixture.Harness.CreateContext();
        Assert.True(await check.BoulderComments.AnyAsync(c => c.Id == comment.Id));

        // The comment had no knowable wall and is dropped; the user's own row stays, wall-less, for /api/me.
        var row = Assert.Single(await fixture.RowsAsync());
        Assert.Equal((FeedKinds.FavoriteChanged, (Guid?)null), (row.Kind, row.WallId));
        Assert.Contains(logger.Lines, l => l.Level == LogLevel.Error && l.Message.Contains($"{FeedKinds.CommentAdded}:{comment.Id}@{boulder}"));
    }
}
