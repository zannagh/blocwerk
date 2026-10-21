// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Read your own writes on the main editors (PR 1.8): a Blazor handler saves through the circuit's services and reloads
/// through the circuit's cache at once, before any hub delivery could arrive (the fixture's hub is never woken). Each
/// reload must see the save. The DomainChange interceptor used to promise this; the feed's commit observers do now.
/// </summary>
public sealed class OwnWriteEditorTests
{
    [Fact]
    public Task TheWallEditorsBatchSave_ReloadsFresh_Sqlite() => WallEditorSaveAsync(null);

    [PostgresFact]
    public async Task TheWallEditorsBatchSave_ReloadsFresh_Postgres()
    {
        using var pg = PostgresTestDatabase.Create();
        await WallEditorSaveAsync(pg);
    }

    [Fact]
    public async Task TheWallSettingsSave_ReloadsFresh()
    {
        await using var f = new OwnWriteFixture();
        await f.StartAsync();
        var wall = (await f.Cache.GetWallAsync(f.WallId))!;

        await f.Circuit.Walls.UpdateWallAsync(f.WallId, "Renamed", "New description", wall.Angle);

        var reloaded = (await f.Cache.GetWallAsync(f.WallId))!;
        Assert.Equal(("Renamed", "New description"), (reloaded.Name, reloaded.Description));
        Assert.Contains(f.Changes, c => c == WallCacheChange.OfWall(f.WallId));
    }

    [Fact]
    public Task CreatingABoulder_ShowsItOnTheWallAtOnce_Sqlite() => CreateBoulderAsync(null);

    [PostgresFact]
    public async Task CreatingABoulder_ShowsItOnTheWallAtOnce_Postgres()
    {
        using var pg = PostgresTestDatabase.Create();
        await CreateBoulderAsync(pg);
    }

    [Fact]
    public async Task RevisingADraft_ReloadsTheBoulderFresh()
    {
        await using var f = new OwnWriteFixture();
        var holds = await f.StartAsync();
        var draft = await f.Circuit.Boulders.CreateBoulderAsync(f.WallId, "Draft", "6A", [Start(holds[0])], isDraft: true);
        Assert.Single((await f.Cache.GetBoulderAsync(draft.Id))!.BoulderHolds);

        await f.Circuit.Boulders.ReviseBoulderAsync(draft.Id, [Start(holds[0]), Top(holds[1])], name: "Revised", grade: "6B");

        var reloaded = (await f.Cache.GetBoulderAsync(draft.Id))!;
        Assert.Equal(("Revised", "6B", 2), (reloaded.Name, reloaded.Grade, reloaded.BoulderHolds.Count));
    }

    [Fact]
    public async Task EditingABoulder_ReloadsTheBoulderAndTheWallFresh()
    {
        await using var f = new OwnWriteFixture();
        var holds = await f.StartAsync();
        var boulder = await f.Circuit.Boulders.CreateBoulderAsync(f.WallId, "Live", "6A", [Start(holds[0])]);
        await f.Cache.GetBoulderAsync(boulder.Id);
        await f.Cache.GetWallAsync(f.WallId);

        await f.Circuit.Boulders.UpdateBoulderAsync(boulder.Id, "Live, renamed", "7A");

        Assert.Equal("Live, renamed", (await f.Cache.GetBoulderAsync(boulder.Id))!.Name);
        Assert.Equal("7A", (await f.Cache.GetWallAsync(f.WallId))!.Boulders.Single(b => b.Id == boulder.Id).Grade);
    }

    [Fact]
    public async Task AComment_IsReadBackAtOnce_AndItsRowIsCommittedForTheOtherCircuits()
    {
        await using var f = new OwnWriteFixture();
        var holds = await f.StartAsync();
        var boulder = await f.Circuit.Boulders.CreateBoulderAsync(f.WallId, "Live", "6A", [Start(holds[0])]);
        await f.Cache.GetBoulderAsync(boulder.Id);
        f.Recorded.Clear();

        var comment = await f.Circuit.Comments.AddCommentAsync(boulder.Id, "Great heel hook");

        var (items, total) = await f.Circuit.Comments.GetCommentsAsync(boulder.Id);
        Assert.Equal((1, comment.Id), (total, items.Single().Id));
        Assert.Contains(f.Recorded.Rows, r => r.Kind == FeedKinds.CommentAdded && r.EntityId == comment.Id && r.WallId == f.WallId);
        Assert.True(f.Cache.Store.Boulders.TryPeek(boulder.Id, out _)); // a comment is not part of the cached boulder
    }

    [Fact]
    public async Task ATickAndARating_AreReadBackAtOnce_AndTheirRowsAreCommitted()
    {
        await using var f = new OwnWriteFixture();
        var holds = await f.StartAsync();
        var boulder = await f.Circuit.Boulders.CreateBoulderAsync(f.WallId, "Live", "6A", [Start(holds[0])]);
        await f.Cache.GetBoulderAsync(boulder.Id);
        f.Recorded.Clear();

        await f.Circuit.Attempts.LogAttemptAsync(boulder.Id, AttemptType.Send);
        await f.Circuit.Feedback.SetRatingAsync(boulder.Id, 4);

        Assert.True((await f.Circuit.Attempts.GetBoulderSummaryForUserAsync(boulder.Id)).HasSent);
        Assert.Equal(4, (await f.Circuit.Feedback.GetRatingAsync(boulder.Id)).MyRating);
        var kinds = f.Recorded.Rows.Where(r => r.EntityId == boulder.Id).Select(r => r.Kind).ToHashSet();
        Assert.Superset(new HashSet<string> { FeedKinds.BoulderStats, FeedKinds.TickMine, FeedKinds.RatingMine }, kinds);
    }

    private static async Task WallEditorSaveAsync(PostgresTestDatabase? pg)
    {
        await using var f = new OwnWriteFixture(pg);
        var holds = await f.StartAsync();
        await f.Cache.GetWallAsync(f.WallId);
        await f.Cache.GetHoldsForGenerationAsync(f.WallId, 0);

        // WallDetail.OnBatchSave: add, update, delete, then LoadWall straight away.
        var added = await f.Circuit.Walls.AddHoldAsync(f.WallId, 0.7, 0.7, 0.03, "red");
        await f.Circuit.Walls.UpdateHoldAsync(holds[0].Id, new HoldEdit { X = 0.11, Y = 0.22, Radius = 0.04 });
        await f.Circuit.Walls.DeleteHoldAsync(holds[1].Id);

        var wall = (await f.Cache.GetWallAsync(f.WallId))!;
        var set = await f.Cache.GetHoldsForGenerationAsync(f.WallId, 0);
        foreach (var live in new[] { wall.Holds.ToList(), set })
        {
            Assert.Contains(live, h => h.Id == added.Id);
            Assert.DoesNotContain(live, h => h.Id == holds[1].Id);
            Assert.Equal((0.11, 0.22), live.Where(h => h.Id == holds[0].Id).Select(h => (h.X, h.Y)).Single());
        }

        var database = await f.DatabaseWallAsync();
        Assert.Equal(database.Holds.Select(h => h.Id).Order(), wall.Holds.Select(h => h.Id).Order());
    }

    private static async Task CreateBoulderAsync(PostgresTestDatabase? pg)
    {
        await using var f = new OwnWriteFixture(pg);
        var holds = await f.StartAsync();
        Assert.Empty((await f.Cache.GetWallAsync(f.WallId))!.Boulders);

        // BoulderCreate saves, then navigates to the wall, which reads the cached aggregate.
        var boulder = await f.Circuit.Boulders.CreateBoulderAsync(f.WallId, "Fresh", "6A", [Start(holds[0]), Top(holds[1])]);

        var wall = (await f.Cache.GetWallAsync(f.WallId))!;
        Assert.Equal("Fresh", wall.Boulders.Single(b => b.Id == boulder.Id).Name);
        Assert.Equal(2, (await f.Cache.GetBoulderAsync(boulder.Id))!.BoulderHolds.Count);
    }

    private static BoulderHoldInput Start(Hold hold) => new(hold.Id, HoldType.Start);

    private static BoulderHoldInput Top(Hold hold) => new(hold.Id, HoldType.Top);
}
