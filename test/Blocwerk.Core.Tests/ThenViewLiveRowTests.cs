// <copyright file="ThenViewLiveRowTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using static Blocwerk.Core.Tests.PanelUpdateCarryFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The boulder "Then" view at a generation shows every hold row that was live then: a row from an older
/// generation counts while its panel had not been re-shot yet, and never once it was.
/// </summary>
public class ThenViewLiveRowTests
{
    // After update 1 the far panel (2,0) was never re-shot: its gen-2 hold is live at gen 3, so a live boulder
    // on it still shows it there. NON-VACUOUS: the walk dropped every own row below the target generation.
    [Fact]
    public async Task LiveBoulder_OnAHoldOfANeverReShotPanel_ShowsItAtLaterGenerations()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        var s = await StageAsync(h, w.WallId, 3, 0, 1);
        await Service(h).PromoteAsync(w.WallId, Confirm(
            new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s[0].HoldId),
            new CarryoverDecision(w.NeighbourHoldId, CarryKind.Carried, s[1].HoldId)));

        var far = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(w.FarBoulderId, 3);
        Assert.Equal(w.FarHoldId, Assert.Single(far!).HoldId);

        var span = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(w.SpanBoulderId, 3);
        Assert.Equal(
            new[] { s[1].HoldId, w.FarHoldId }.OrderBy(x => x),
            span!.Select(m => m.HoldId).OrderBy(x => x));
    }

    // A boulder frozen on a hold whose panel WAS re-shot at gen 3 (the hold was removed, so no lineage link
    // says so): that row is not live at gen 3 and must not be shown there, only at gen 2.
    [Fact]
    public async Task FrozenBoulder_OnARemovedHold_IsNotShownAfterItsPanelWasReShot()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        Guid boulderId;
        await using (var db = h.CreateContext())
        {
            var boulder = new Boulder { WallId = w.WallId, Name = "Gone", CreatedByUserId = h.Owner.Id, Generation = 2 };
            db.Boulders.Add(boulder);
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = w.CentreHoldId });
            await db.SaveChangesAsync();
            boulderId = boulder.Id;
        }

        await StageAsync(h, w.WallId, 3, 0);
        await Service(h).PromoteAsync(w.WallId, Confirm(new CarryoverDecision(w.CentreHoldId, CarryKind.Removed, null)));

        Assert.Empty((await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 3))!);
        Assert.Equal(w.CentreHoldId, Assert.Single((await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 2))!).HoldId);
    }
}
