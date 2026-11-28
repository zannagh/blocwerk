// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Migrations;
using Blocwerk.Core.Services.Membership;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Ending memberships on a real PostgreSQL: two admins removing each other at the same moment cannot both succeed, and
/// the invite-token split gives every existing wall an invite equal to its old token.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresMembershipTests
{
    [PostgresFact]
    public async Task TwoAdminsRemovingEachOther_AtTheSameMoment_OnlyOneSucceeds()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await h.SeedWallAsync(holdCount: 0);
        var a = await h.AddMemberAsync("a@test", WallRole.Admin);
        var b = await h.AddMemberAsync("b@test", WallRole.Admin);

        // Hold the wall's member rows, so both removals have passed their first admin check and wait at the same point.
        await using var blocker = h.CreateContext();
        await using (var hold = await blocker.Database.BeginTransactionAsync())
        {
            await blocker.Database.ExecuteSqlRawAsync("SELECT 1 FROM \"WallMembers\" WHERE \"WallId\" = {0} FOR UPDATE", h.WallId);
            var removeB = Capture(As(h, a).RemoveMemberAsync(h.WallId, b.Id));
            var removeA = Capture(As(h, b).RemoveMemberAsync(h.WallId, a.Id));
            // Watched from a connection of its own: inside a transaction pg_stat_activity is read once and then cached.
            await using (var watcher = h.CreateContext())
            {
                await WaitForLockWaitersAsync(watcher, 2, removeB, removeA);
            }

            await hold.RollbackAsync();

            var outcomes = await Task.WhenAll(removeB, removeA);
            Assert.Single(outcomes, o => o is null);
            Assert.IsType<UnauthorizedAccessException>(Assert.Single(outcomes, o => o is not null));
        }

        await using var db = h.CreateContext();
        Assert.Equal(1, await db.WallMembers.IgnoreQueryFilters().CountAsync(m => m.UserId == a.Id || m.UserId == b.Id));
    }

    [PostgresFact]
    public async Task TheJoinTokenMigration_CopiesEachWallsShareToken()
    {
        using var database = PostgresTestDatabase.CreateEmpty();
        await PostgresMigrationTests.MigrateBeforeAsync(database, nameof(AddWallJoinToken));
        var user = await PostgresRows.InsertAsync(database.ConnectionString, "Users", ("Identifier", "u@test"), ("DisplayName", "U"));
        var shared = await PostgresRows.InsertAsync(database.ConnectionString, "Walls", ("Name", "Shared"), ("OwnerId", user), ("ShareToken", "abc123"));
        var never = await PostgresRows.InsertAsync(database.ConnectionString, "Walls", ("Name", "Never shared"), ("OwnerId", user));

        await PostgresMigrationTests.MigrateToAsync(database, nameof(AddWallJoinToken));

        var tokens = (await PostgresRows.QueryAsync(
            database.ConnectionString,
            "SELECT \"Id\", \"ShareToken\", \"JoinToken\" FROM \"Walls\"",
            r => (Id: r.GetGuid(0), Share: r.IsDBNull(1) ? null : r.GetString(1), Join: r.IsDBNull(2) ? null : r.GetString(2))))
            .ToDictionary(r => r.Id);
        Assert.Equal(("abc123", "abc123"), (tokens[shared].Share, tokens[shared].Join));
        Assert.Equal(((string?)null, (string?)null), (tokens[never].Share, tokens[never].Join));
    }

    private static WallMembershipService As(WallTestHarness h, User user)
    {
        var current = Substitute.For<ICurrentUserService>();
        current.GetCurrentUserAsync().Returns(user);
        return new WallMembershipService(h.DbContextFactory, current, h.ActivityLog, h.BetaStorage, NullLogger<WallMembershipService>.Instance);
    }

    private static async Task<Exception?> Capture(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task WaitForLockWaitersAsync(DbContext db, int count, params Task<Exception?>[] removals)
    {
        for (var i = 0; i < 1200; i++)
        {
            if (removals.All(r => r.IsCompleted))
            {
                Assert.Fail("The removals ended without waiting: " + string.Join(" | ", removals.Select(r => r.Result?.ToString() ?? "succeeded")));
            }

            var waiting = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'")
                .SingleAsync();
            if (waiting >= count)
            {
                return;
            }

            await Task.Delay(50);
        }

        var sessions = await db.Database
            .SqlQueryRaw<string>("SELECT coalesce(state,'') || ' ' || coalesce(wait_event_type,'') || ' ' || coalesce(wait_event,'') || ' ' || left(coalesce(query,''), 120) AS \"Value\" FROM pg_stat_activity WHERE datname = current_database()")
            .ToListAsync();
        Assert.Fail($"Fewer than {count} sessions came to wait on the member rows: " + string.Join(" | ", sessions));
    }
}
