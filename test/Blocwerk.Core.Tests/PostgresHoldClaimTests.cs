// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A wall-wide placement run claims each hold's row with a no-op <c>UPDATE</c>: on PostgreSQL that is a real row lock under
/// READ COMMITTED, so a user's move that lands after the claim waits for the run's commit and then wins, and one that
/// landed before it makes the claim match no row, so the run does not place the hold at its old position.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresHoldClaimTests
{
    [PostgresFact]
    public async Task AMoveLandingBeforeTheClaim_IsNotOverwritten_AndTheHoldIsPlacedAgainAtItsNewPosition()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var s = await HoldPlacementScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.25, 0.5);
        var factory = new MidWriteEditDbContextFactory(h.DbContextFactory.ConnectionString, () => MoveAsync(h, hold, 0.3));
        var service = Service(h, s, factory);

        await service.PlaceAsync(h.WallId);

        Assert.True(factory.Edited);
        var stored = (await s.LoadHoldsAsync())[hold];
        Assert.Equal((0.3, null, null), (stored.X, stored.FacetId, stored.PlaneAMm));
        await using (var db = h.CreateContext())
        {
            var run = await db.HoldPlacementRuns.SingleAsync();
            Assert.DoesNotContain(HoldPlacementEntry.FromJson(run.HoldsJson), e => e.HoldId == hold);
        }

        Assert.Equal([hold], await s.Service().PlaceEditedAsync(h.WallId, [hold]));
        Assert.Equal(0.3 * 4000, (await s.LoadHoldsAsync())[hold].PlaneAMm!.Value, 1);
    }

    [PostgresFact]
    public async Task AMoveLandingAfterTheClaim_WaitsOnTheRowLock_ThenWins_AndThePlacementIsNotSettled()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var s = await HoldPlacementScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.25, 0.5);
        Task? move = null;
        var waited = false;
        var factory = new MidWriteEditDbContextFactory(
            h.DbContextFactory.ConnectionString,
            async () =>
            {
                move = Task.Run(() => MoveAsync(h, hold, 0.3));
                waited = await WaitForALockWaitAsync(h.DbContextFactory.ConnectionString);
                Assert.False(move.IsCompleted);
            },
            afterClaim: true);

        await Service(h, s, factory).PlaceAsync(h.WallId);
        Assert.NotNull(move);
        await move;

        Assert.True(waited, "The edit was never seen waiting on the claimed row's lock.");

        // The edit read the row before the run committed (a reader never waits on PostgreSQL) and its UPDATE writes only
        // the columns it changed, so the run's placement can stay on the row beside the new centre. It is stale by
        // the geometry hash, and the edit path's placement (below) puts the hold right.
        var stored = (await s.LoadHoldsAsync())[hold];
        Assert.Equal(0.3, stored.X);
        await using (var db = h.CreateContext())
        {
            var entry = HoldPlacementEntry.FromJson((await db.HoldPlacementRuns.SingleAsync()).HoldsJson).Single();
            Assert.NotEqual(HoldPlacementEntry.HashGeometry(stored), entry.GeometryHash);
        }

        Assert.Equal([hold], await s.Service().PlaceEditedAsync(h.WallId, [hold]));
        Assert.Equal(0.3 * 4000, (await s.LoadHoldsAsync())[hold].PlaneAMm!.Value, 1);
    }

    [PostgresFact]
    public async Task AWallWidePlacement_WithNoEditInFlight_PlacesTheHoldAndRecordsIt()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var s = await HoldPlacementScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.25, 0.5);

        await s.Service().PlaceAsync(h.WallId);

        var placed = (await s.LoadHoldsAsync())[hold];
        Assert.Equal("0", placed.FacetId);
        await using var db = h.CreateContext();
        var entry = HoldPlacementEntry.FromJson((await db.HoldPlacementRuns.SingleAsync()).HoldsJson).Single();
        Assert.Equal(HoldPlacementEntry.HashGeometry(placed), entry.GeometryHash);
    }

    private static HoldTexturePlacementService Service(WallTestHarness h, HoldPlacementScenario s, MidWriteEditDbContextFactory factory) => new(
        factory, h.CurrentUser, NullLogger<HoldTexturePlacementService>.Instance, s.Matcher, s.Files, s.Queue);

    /// <summary>A user's 2D move: the new centre, and the 3D position un-placed until the edit path places it again.</summary>
    private static async Task MoveAsync(WallTestHarness h, Guid holdId, double x)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(e => e.Id == holdId);
        hold.InvalidateGlyphPosition();
        hold.X = x;
        await db.SaveChangesAsync();
    }

    /// <summary>Polls the server until some backend of the database waits on a lock (the move, blocked by the claim).</summary>
    private static async Task<bool> WaitForALockWaitAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND pid <> pg_backend_pid()",
            connection);
        for (var i = 0; i < 200; i++)
        {
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }
}
