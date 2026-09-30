// <copyright file="WallRefreshFlowTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// "Update panels + 3D" end to end over the real services: without 3D it is just the panel update in quick mode,
/// nothing goes live before the user's confirm, and after the promote the holds are placed on an active 3D model.
/// </summary>
public class WallRefreshFlowTests
{
    [Fact]
    public async Task Sorting_ProposesAPhotoForTheCentrePanel()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        var id = await s.DropPhotosAsync();

        await s.Service.SortAsync(id);
        await s.RunQueuedAsync();

        var view = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.ReadyToStart, view.Status);
        var pick = Assert.Single(view.Picks);
        Assert.Equal((0, 0), (pick.Col, pick.Row));
        Assert.Contains(view.Photos, p => p.PhotoId == pick.PhotoId);
        Assert.Equal(PanelPickConfidence.High, pick.Confidence);
    }

    [Fact]
    public async Task WithoutMarkersOrCompute_ItIsThePanelUpdateOnly_WaitingForTheConfirm()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();

        var view = await s.PrepareAsync();

        Assert.Equal(WallRefreshStatus.ReadyToApply, view.Status);
        Assert.False(view.Steps.Any(st => st.State == RefreshStepState.Failed), string.Join("; ", view.Steps.Select(st => $"{st.Key}:{st.State}:{st.Detail}")));
        Assert.Equal(RefreshStepState.Skipped, view.Steps.Single(st => st.Key == RefreshTimeline.Capture).State);
        Assert.Null(view.Capture);
        Assert.Equal(2, view.Summary!.KeptInPlace + view.Summary.Refound);
        await using var db = h.CreateContext();
        Assert.False(await db.WallCaptures.AnyAsync(c => c.Status != WallCaptureStatus.Draft), "no capture may start");
        Assert.False(await db.WallCaptures.AnyAsync(), "the unused draft is discarded");
        Assert.NotNull(await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId));
    }

    [Fact]
    public async Task OnAMarkerWall_TheCaptureStartsWithEveryPhoto_AndThePanelUpdateGoesAhead()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedMarkerWallWithEarlierCaptureAsync();

        var view = await s.PrepareAsync();

        Assert.Equal(WallRefreshStatus.ReadyToApply, view.Status);
        var capture = view.Steps.Single(st => st.Key == RefreshTimeline.Capture);
        Assert.True(capture.State == RefreshStepState.Done, capture.Detail);
        Assert.NotNull(view.Capture);
        Assert.Equal(WallCaptureStatus.Queued, view.Capture!.Status);
        Assert.Equal(2, view.Capture.PhotoCount);
        Assert.NotNull(await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId));
    }

    [Fact]
    public async Task QuickMode_PromotesOnlyAfterTheConfirm()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();

        Assert.Equal(0, await GenerationAsync(h));
        await s.Processor.ProcessAsync(view.Id, CancellationToken.None);
        Assert.Equal(0, await GenerationAsync(h));

        await s.Service.ApplyAsync(view.Id);
        await s.RunQueuedAsync();

        var done = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.Done, done.Status);
        Assert.Equal(1, await GenerationAsync(h));
        Assert.Null(await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId));
        await Assert.ThrowsAsync<UserFacingException>(() => s.Service.ApplyAsync(view.Id));
    }

    [Fact]
    public async Task AfterThePromote_HoldsArePlacedOnTheActiveModel()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        s.Placement.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new HoldPlacementStatus(true, true, null));
        s.Placement.PlaceAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HoldPlacementResult(Guid.NewGuid(), 2, 0, 0, []));
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        await s.Placement.DidNotReceiveWithAnyArgs().PlaceAsync(default, default!, default);

        await s.Service.ApplyAsync(view.Id);
        await s.RunQueuedAsync();

        await s.Placement.Received(1).PlaceAsync(h.WallId, WallRefreshProcessor.PlacementTrigger, Arg.Any<CancellationToken>());
        var place = (await s.CurrentAsync()).Steps.Single(st => st.Key == RefreshTimeline.Place);
        Assert.Equal(RefreshStepState.Done, place.State);
        Assert.Contains("2 holds placed", place.Detail);
    }

    [Fact]
    public async Task WithoutAModel_PlacementIsSkipped_AndSaysWhy()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();

        await s.Service.ApplyAsync(view.Id);
        await s.RunQueuedAsync();

        await s.Placement.DidNotReceiveWithAnyArgs().PlaceAsync(default, default!, default);
        var place = (await s.CurrentAsync()).Steps.Single(st => st.Key == RefreshTimeline.Place);
        Assert.Equal(RefreshStepState.Skipped, place.State);
        Assert.Contains("no 3D model", place.Detail);
    }

    [Fact]
    public void Start_RefusesAnOuterPanelWithoutTheOneTowardTheCentre()
    {
        var photo = Guid.NewGuid();
        IReadOnlyList<PanelPick> picks =
        [
            new PanelPick(0, 0, null, PanelPickConfidence.None, []),
            new PanelPick(1, 0, photo, PanelPickConfidence.High, []),
        ];

        var ex = Assert.Throws<UserFacingException>(() => WallRefreshService.ApplyChoices(picks, []));
        Assert.Contains("(1,0)", ex.Message);
        Assert.Throws<UserFacingException>(() => WallRefreshService.ApplyChoices(picks, [new PanelChoice(0, 0, photo)]));
    }

    private static async Task<int> GenerationAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.Walls.Where(w => w.Id == h.WallId).Select(w => w.CurrentGeneration).SingleAsync();
    }
}
