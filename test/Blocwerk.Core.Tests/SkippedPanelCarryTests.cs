// <copyright file="SkippedPanelCarryTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.PanelUpdateCarryFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A panel an earlier subset update skipped keeps its holds at the older generation; the next update that
/// re-shoots it must carry those holds like any other. The carry set used to be
/// <c>Generation == CurrentGeneration</c>, so the second update never saw them: they dropped out of the live
/// wall, their boulders kept pointing at retired rows, and the fresh detections went live uncurated.
/// </summary>
public class SkippedPanelCarryTests
{
    // Update 1 re-shoots (0,0) and (1,0); (2,0) stays gen 2. Update 2 re-shoots all three. The far hold must
    // get a gen-4 successor with a lineage link FROM gen 2, keep its curation, and take its boulders along.
    // NON-VACUOUS: before the fix the far hold had no link, its boulder still pointed at the gen-2 row, and
    // the live read lost it.
    [Fact]
    public async Task SecondUpdate_ReShootingASkippedPanel_CarriesItsOlderGenerationHolds()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        var second = await RunUpdateOneThenStageUpdateTwoAsync(h, w);

        var first = await FirstUpdateSuccessorsAsync(h, w);
        await Service(h).PromoteAsync(w.WallId, Confirm(
            new CarryoverDecision(first.Centre, CarryKind.Carried, second[0].HoldId),
            new CarryoverDecision(first.Neighbour, CarryKind.Carried, second[1].HoldId),
            new CarryoverDecision(w.FarHoldId, CarryKind.Carried, second[2].HoldId)));

        await using var db = h.CreateContext();
        var farTwin = second[2].HoldId;
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == w.FarHoldId);
        Assert.Equal(farTwin, link.NewHoldId);
        Assert.Equal(2, link.FromGeneration);
        Assert.Equal(4, link.ToGeneration);
        Assert.Equal("Far jug", (await db.Holds.SingleAsync(x => x.Id == farTwin)).Name);

        var farBoulder = await db.Boulders.SingleAsync(b => b.Id == w.FarBoulderId);
        Assert.False(farBoulder.IsHistoric);
        Assert.Equal(4, farBoulder.Generation);
        Assert.Equal(farTwin, (await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == w.FarBoulderId)).HoldId);

        var span = await db.BoulderHolds.Where(bh => bh.BoulderId == w.SpanBoulderId).Select(bh => bh.HoldId).ToListAsync();
        Assert.Equal(new[] { second[1].HoldId, farTwin }.OrderBy(x => x), span.OrderBy(x => x));

        var wall = await h.WallService.GetWallAsync(w.WallId);
        var live = wall!.Holds.Select(x => x.Id).ToHashSet();
        Assert.Contains(farTwin, live);
        Assert.DoesNotContain(w.FarHoldId, live);
        Assert.Equal(3, live.Count);
    }

    // The review must offer the same set the promote carries: the skipped panel's gen-2 hold is a carried
    // old hold of update 2, drawn over that panel's live gen-2 photo.
    [Fact]
    public async Task Resume_AfterASkippedPanelIsReShot_ListsItsOlderGenerationHolds()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        await RunUpdateOneThenStageUpdateTwoAsync(h, w);

        var session = await Service(h, new IndexAlignedTestMatcher()).ResumeAsync(w.WallId);

        Assert.Contains(w.FarHoldId, session.CarriedOldHoldIds!);
        var far = session.CarriedPanels!.Single(p => p is { Col: 2, Row: 0 });
        Assert.Equal(w.PanelIds[2], far.LivePanelId);
        Assert.Equal([w.FarHoldId], far.OldHolds.Select(x => x.Id).ToList());
        Assert.Contains(session.Carryover, c => c.OldHoldId == w.FarHoldId);
    }

    // The "Then" view at gen 3: the far hold was carried 2 -> 4 in one step, but it was live at gen 3 on its
    // skipped panel, so the boulders still show it there. NON-VACUOUS: the walk used to require a row AT the
    // target generation and dropped the hold.
    [Fact]
    public async Task ThenView_AtTheSkippedGeneration_StillShowsTheSkippedPanelHold()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        var second = await RunUpdateOneThenStageUpdateTwoAsync(h, w);
        var first = await FirstUpdateSuccessorsAsync(h, w);
        await Service(h).PromoteAsync(w.WallId, Confirm(
            new CarryoverDecision(first.Centre, CarryKind.Carried, second[0].HoldId),
            new CarryoverDecision(first.Neighbour, CarryKind.Carried, second[1].HoldId),
            new CarryoverDecision(w.FarHoldId, CarryKind.Carried, second[2].HoldId)));

        var far = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(w.FarBoulderId, 3);
        Assert.Equal(w.FarHoldId, Assert.Single(far!).HoldId);

        var span = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(w.SpanBoulderId, 3);
        Assert.Equal(
            new[] { first.Neighbour, w.FarHoldId }.OrderBy(x => x),
            span!.Select(m => m.HoldId).OrderBy(x => x));
    }

    // Only the live panel at each re-shot position is the "before" side, so only its photo is read: the
    // superseded gen-2 rows of (0,0) and (1,0) keep their photos as history and must not be loaded.
    [Fact]
    public async Task CarryScope_IsTheLivePanelPerUpdatedPosition_NotEverySupersededRow()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        await RunUpdateOneThenStageUpdateTwoAsync(h, w);
        var first = await FirstUpdateSuccessorsAsync(h, w);

        await using var db = h.CreateContext();
        var positions = await WallBigUpdateService.LoadPanelPositionsAsync(db, w.WallId);
        var updated = await WallBigUpdateService.LoadUpdatedPositionsAsync(db, w.WallId, 4);
        var live = await WallBigUpdateService.LoadLiveUpdatedPanelIdsAsync(db, w.WallId, positions, updated);

        Assert.Equal(
            new[] { first.CentrePanel, first.NeighbourPanel, w.PanelIds[2] }.OrderBy(x => x),
            live.OrderBy(x => x));
        Assert.DoesNotContain(w.PanelIds[0], live);
        Assert.DoesNotContain(w.PanelIds[1], live);
    }

    private static async Task<Dictionary<int, (Guid PanelId, Guid HoldId)>> RunUpdateOneThenStageUpdateTwoAsync(
        WallTestHarness h, RowWall w)
    {
        var first = await StageAsync(h, w.WallId, 3, 0, 1);
        await Service(h).PromoteAsync(w.WallId, Confirm(
            new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, first[0].HoldId),
            new CarryoverDecision(w.NeighbourHoldId, CarryKind.Carried, first[1].HoldId)));
        return await StageAsync(h, w.WallId, 4, 0, 1, 2);
    }

    private static async Task<(Guid Centre, Guid Neighbour, Guid CentrePanel, Guid NeighbourPanel)> FirstUpdateSuccessorsAsync(
        WallTestHarness h, RowWall w)
    {
        await using var db = h.CreateContext();
        var centre = (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == w.CentreHoldId)).NewHoldId!.Value;
        var neighbour = (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == w.NeighbourHoldId)).NewHoldId!.Value;
        var centrePanel = (await db.Holds.SingleAsync(x => x.Id == centre)).WallPanelId!.Value;
        var neighbourPanel = (await db.Holds.SingleAsync(x => x.Id == neighbour)).WallPanelId!.Value;
        return (centre, neighbour, centrePanel, neighbourPanel);
    }
}
