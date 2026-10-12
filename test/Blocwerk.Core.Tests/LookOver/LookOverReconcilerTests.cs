// <copyright file="LookOverReconcilerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The wall can be edited before it is looked over: whenever the look-over is read, what the hold editor has dealt
/// with since is closed with its reason, and what it changed around a finding is taken in. (A hold removed, edited or
/// merged there is in <see cref="LookOverAnswerTests"/> and <see cref="KeptHoldConfirmTests"/>.)
/// </summary>
public class LookOverReconcilerTests
{
    [Fact]
    public async Task TheNewHoldOnTheSpot_RemovedInTheEditor_LeavesTheOldHoldsQuestionOpen()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h, covered: true);
        var service = Service(h);

        await h.WallService.DeleteHoldAsync(k.BigId!.Value);

        var view = (await service.ListAsync(k.Wall.WallId)).Single(v => v.HoldId == k.UnusedId);
        Assert.Equal(
            (LookOverFindingKind.NotFound, LookOverFindingStatus.Open, (Guid?)null, false, true),
            (view.Kind, view.Status, view.OtherHoldId, view.Covered, view.Missing));
        Assert.Equal(3, (await HoldAsync(h, k.UnusedId))!.KeptUnconfirmedGeneration);
        Assert.Equal(2, (await service.GetStateAsync(k.Wall.WallId))!.OpenFindings);
    }

    [Fact]
    public async Task AHoldPlacedByHandOnTheSpotOfAHoldTheUpdateCouldNotFind_BecomesThatFindingsNewHold()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var service = Service(h);
        var old = (await HoldAsync(h, k.UnusedId))!;

        var added = await KeptHoldFixture.AddByHandAsync(h, k.Wall.WallId, old.WallPanelId!.Value, old.X + 0.005, old.Y);

        var views = await service.ListAsync(k.Wall.WallId);
        var view = views.Single(v => v.HoldId == k.UnusedId);
        Assert.Equal(((Guid?)added.Id, true, true), (view.OtherHoldId, view.Covered, view.Missing));
        Assert.Null(views.Single(v => v.HoldId == k.UsedId).OtherHoldId);

        // Reading it again changes nothing.
        await using var db = h.CreateContext();
        Assert.Equal(0, await LookOverReconciler.ReconcileAsync(db, k.Wall.WallId, CancellationToken.None));
    }

    [Fact]
    public async Task ADetectionThatWasLeftOut_WhereAHoldWasPlacedByHandSince_IsClosed()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var panel = (await HoldAsync(h, k.UnusedId))!.WallPanelId!.Value;
        WallLookOverFinding LeftOut(double x) =>
            new() { WallId = k.Wall.WallId, Generation = 3, Kind = LookOverFindingKind.ConflictingNew, PanelId = panel, X = x, Y = 0.3, Radius = 0.03 };
        var here = await AddFindingAsync(h, LeftOut(0.4));
        var elsewhere = await AddFindingAsync(h, LeftOut(0.7));

        await KeptHoldFixture.AddByHandAsync(h, k.Wall.WallId, panel, 0.41, 0.3);

        var views = (await Service(h).ListAsync(k.Wall.WallId)).ToDictionary(v => v.Id);
        Assert.Equal(
            (LookOverFindingStatus.Closed, (LookOverCloseReason?)LookOverCloseReason.HoldAddedHere),
            (views[here].Status, views[here].ClosedReason));
        Assert.Equal(LookOverFindingStatus.Open, views[elsewhere].Status);
        Assert.NotNull((await FindingsAsync(h, k.Wall.WallId)).Single(f => f.Id == here).ClosedAt);
    }

    [Fact]
    public async Task AnUnsureMatch_WhoseHoldWasMarkedUnchangedInTheEditor_IsClosed_AndNoLongerCounted()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        Assert.Equal(1, (await service.GetStateAsync(w.WallId))!.OpenFindings);
        var hold = (await HoldAsync(h, w.DetectionId))!;

        // Reshaping it leaves its review mark, and so the finding: only taking the mark off says the hold was checked.
        await h.WallService.UpdateHoldAsync(hold.Id, new HoldEdit { X = hold.X, Y = hold.Y, Radius = hold.Radius + 0.005 });
        Assert.Equal(1, (await service.GetStateAsync(w.WallId))!.OpenFindings);

        await h.WallService.RestoreBouldersForUnchangedHoldAsync(hold.Id);

        // The wall page's count drops at once, without the look-over being opened.
        var state = (await service.GetStateAsync(w.WallId))!;
        Assert.Equal((true, 0, 1), (state.Pending, state.OpenFindings, state.TotalFindings));
        var view = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal(
            (LookOverFindingStatus.Closed, (LookOverCloseReason?)LookOverCloseReason.CheckedInEditor),
            (view.Status, view.ClosedReason));
    }
}
