// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Read your own writes after a JS→API write (PR 1.8): the browser posts to a controller (its own request scope and
/// services), then tells the page to reload, and the page reads the circuit's cache. The request's commit must have
/// evicted the circuit's entries before the response returned; no hub delivery is involved (the fixture's hub is never
/// woken).
/// </summary>
public sealed class OwnWriteApiTests
{
    [Fact]
    public Task AnOfflineBoulderCreate_IsOnTheCircuitsWall_BeforeTheResponse_Sqlite() => OfflineCreateAsync(null);

    [PostgresFact]
    public async Task AnOfflineBoulderCreate_IsOnTheCircuitsWall_BeforeTheResponse_Postgres()
    {
        using var pg = PostgresTestDatabase.Create();
        await OfflineCreateAsync(pg);
    }

    [Fact]
    public async Task AnOfflineRevise_ReloadsTheCircuitsBoulderFresh()
    {
        await using var f = new OwnWriteFixture();
        var holds = await f.StartAsync();
        var draft = await f.Circuit.Boulders.CreateBoulderAsync(f.WallId, "Draft", "6A", [new(holds[0].Id, HoldType.Start)], isDraft: true);
        await f.Cache.GetBoulderAsync(draft.Id);
        var request = f.Request();
        var controller = new OfflineBouldersController(request.Boulders, f.Harness.CurrentUser);

        var result = await controller.Revise(draft.Id, new ReviseBoulderRequest
        {
            Name = "Revised offline",
            Holds = [new BoulderHoldDto { HoldId = holds[0].Id, Type = HoldType.Start }, new BoulderHoldDto { HoldId = holds[2].Id, Type = HoldType.Top }],
        });

        Assert.Equal(200, Assert.IsType<OkObjectResult>(result).StatusCode);
        var reloaded = (await f.Cache.GetBoulderAsync(draft.Id))!;
        Assert.Equal(("Revised offline", 2), (reloaded.Name, reloaded.BoulderHolds.Count));
    }

    [Fact]
    public async Task AHoldWriteOfAnotherRequest_ReloadsTheCircuitsHoldsFresh()
    {
        // The pattern of the hold-shape, volume and refresh-apply endpoints: a service call in the request's scope.
        await using var f = new OwnWriteFixture();
        var holds = await f.StartAsync();
        await f.Cache.GetWallAsync(f.WallId);
        await f.Cache.GetHoldsForGenerationAsync(f.WallId, 0);

        await f.Request().Walls.UpdateHoldAsync(holds[2].Id, new HoldEdit { X = 0.42, Y = 0.43, Radius = 0.05 });

        Assert.Equal(0.42, (await f.Cache.GetWallAsync(f.WallId))!.Holds.Single(h => h.Id == holds[2].Id).X);
        Assert.Equal(0.42, (await f.Cache.GetHoldsForGenerationAsync(f.WallId, 0)).Single(h => h.Id == holds[2].Id).X);
    }

    [Fact]
    public async Task OfflineCommentsAndTicks_AreReadBack_AndCommitted()
    {
        await using var f = new OwnWriteFixture();
        var holds = await f.StartAsync();
        var boulder = await f.Circuit.Boulders.CreateBoulderAsync(f.WallId, "Live", "6A", [new(holds[0].Id, HoldType.Start)]);
        var request = f.Request();
        var controller = new OfflineActionsController(request.Attempts, request.Feedback, request.Comments, f.Harness.CurrentUser);
        f.Recorded.Clear();

        Assert.IsType<OkObjectResult>(await controller.AddComment(new AddCommentRequest { BoulderId = boulder.Id, Text = "Offline beta" }));
        Assert.IsType<OkObjectResult>(await controller.LogAttempt(new LogAttemptRequest { BoulderId = boulder.Id, Type = AttemptType.Send }));

        Assert.Equal("Offline beta", (await f.Circuit.Comments.GetCommentsAsync(boulder.Id)).Items.Single().Text);
        Assert.True((await f.Circuit.Attempts.GetBoulderSummaryForUserAsync(boulder.Id)).HasSent);
        Assert.Contains(f.Recorded.Rows, r => r.Kind == FeedKinds.CommentAdded);
        Assert.Contains(f.Recorded.Rows, r => r.Kind == FeedKinds.TickMine && r.EntityId == boulder.Id);
    }

    private static async Task OfflineCreateAsync(PostgresTestDatabase? pg)
    {
        await using var f = new OwnWriteFixture(pg);
        var holds = await f.StartAsync();
        Assert.Empty((await f.Cache.GetWallAsync(f.WallId))!.Boulders);
        var request = f.Request();
        var controller = new OfflineBouldersController(request.Boulders, f.Harness.CurrentUser);
        var id = Guid.NewGuid();

        var result = await controller.Create(new CreateBoulderRequest
        {
            Id = id,
            WallId = f.WallId,
            Name = "Queued",
            Grade = "6A",
            Holds = [new BoulderHoldDto { HoldId = holds[0].Id, Type = HoldType.Start }],
        });

        Assert.Equal(200, Assert.IsType<OkObjectResult>(result).StatusCode);
        Assert.Equal("Queued", (await f.Cache.GetWallAsync(f.WallId))!.Boulders.Single(b => b.Id == id).Name);
    }
}
