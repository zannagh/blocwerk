// <copyright file="KeptHoldConfirmTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// A hold the update could not find stays "kept, not confirmed" until a person says it is there: in the look-over, or
/// by editing it in the hold editor. And until then it cannot be put on a boulder that does not use it already.
/// </summary>
public class KeptHoldConfirmTests
{
    [Fact]
    public async Task StillHere_ConfirmsTheHold_AndUndoMakesItUnconfirmedAgain()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var service = Service(h);
        var finding = await KeptHoldFixture.FindingOfAsync(h, k.UnusedId);

        await service.AnswerAsync(k.Wall.WallId, finding.Id, LookOverAnswer.Settle);

        Assert.Null((await HoldAsync(h, k.UnusedId))!.KeptUnconfirmedGeneration);
        Assert.Equal(1, (await service.GetStateAsync(k.Wall.WallId))!.MissingHolds);
        var answered = (await service.ListAsync(k.Wall.WallId)).Single(v => v.Id == finding.Id);
        Assert.Equal((false, true, false, true), (answered.HoldKept, answered.KeptAtUpdate, answered.Missing, answered.CanUndo));

        await service.UndoAsync(k.Wall.WallId, finding.Id);

        Assert.Equal(3, (await HoldAsync(h, k.UnusedId))!.KeptUnconfirmedGeneration);
        Assert.True((await service.ListAsync(k.Wall.WallId)).Single(v => v.Id == finding.Id).Missing);
    }

    [Fact]
    public async Task EditingTheHoldInTheHoldEditor_ConfirmsIt_AndClosesItsFinding()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var hold = (await HoldAsync(h, k.UnusedId))!;

        await h.WallService.UpdateHoldAsync(hold.Id, new HoldEdit { X = hold.X + 0.01, Y = hold.Y, Radius = hold.Radius });

        Assert.Null((await HoldAsync(h, k.UnusedId))!.KeptUnconfirmedGeneration);
        var view = (await Service(h).ListAsync(k.Wall.WallId)).Single(v => v.HoldId == k.UnusedId);
        Assert.Equal(
            (LookOverFindingStatus.Closed, (LookOverCloseReason?)LookOverCloseReason.CheckedInEditor, false),
            (view.Status, view.ClosedReason, view.Missing));
        Assert.Empty(view.Answers);
    }

    [Fact]
    public async Task MarkingTheHoldAsChanged_ConfirmsItToo()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);

        await h.WallService.MarkHoldModifiedAsync(k.UnusedId);

        Assert.Null((await HoldAsync(h, k.UnusedId))!.KeptUnconfirmedGeneration);
    }

    [Theory]
    [InlineData(true, LookOverCloseReason.CheckedInEditor)]
    [InlineData(false, LookOverCloseReason.MergedInEditor)]
    public async Task ADuplicateMerge_ConfirmsTheHoldThatStays_AndClosesTheFindingEitherWay(bool keptHoldStays, LookOverCloseReason reason)
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var (left, right) = keptHoldStays ? (k.UnusedId, k.FoundId) : (k.FoundId, k.UnusedId);

        await Duplicates(h).MergeAsync(k.Wall.WallId, left, right, HoldMergeMode.KeepLeft);

        var survivor = (await HoldAsync(h, left))!;
        Assert.Null(survivor.KeptUnconfirmedGeneration);
        Assert.Null(await HoldAsync(h, right));
        var view = (await Service(h).ListAsync(k.Wall.WallId)).Single(v => v.OldHoldId == k.UnusedOldId);
        Assert.Equal((LookOverFindingStatus.Closed, (LookOverCloseReason?)reason), (view.Status, view.ClosedReason));
    }

    [Fact]
    public async Task AnUnconfirmedHold_CannotBeAddedToABoulder_ButABoulderThatUsesOneKeepsIt()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        List<BoulderHoldInput> With(params Guid[] ids) => [.. ids.Select(id => new BoulderHoldInput(id))];

        var created = await Assert.ThrowsAsync<UserFacingException>(
            () => h.BoulderService.CreateBoulderAsync(k.Wall.WallId, "New line", null, With(k.FoundId, k.UnusedId)));
        Assert.Equal(BoulderService.UnconfirmedHoldMessage, created.Message);
        await Assert.ThrowsAsync<UserFacingException>(
            () => h.BoulderService.UpdateBoulderAsync(k.Wall.BoulderId, "Crimp line", null, With(k.UsedId, k.UnusedId)));
        await Assert.ThrowsAsync<UserFacingException>(() => h.BoulderService.ReviseBoulderAsync(k.Wall.BoulderId, With(k.UsedId, k.UnusedId)));

        // The boulder that already uses a hold the update could not find is saved with it, and with holds that are there.
        await h.BoulderService.UpdateBoulderAsync(k.Wall.BoulderId, "Crimp line", null, With(k.UsedId, k.FoundId));
        Assert.Equal(2, (await BoulderAsync(h, k.Wall.BoulderId)).BoulderHolds.Count);

        // Once somebody has confirmed the hold, it can be chosen like any other.
        await Service(h).AnswerAsync(k.Wall.WallId, (await KeptHoldFixture.FindingOfAsync(h, k.UnusedId)).Id, LookOverAnswer.Settle);
        var boulder = await h.BoulderService.CreateBoulderAsync(k.Wall.WallId, "New line", null, With(k.FoundId, k.UnusedId));
        await using var db = h.CreateContext();
        Assert.Equal(2, await db.BoulderHolds.CountAsync(bh => bh.BoulderId == boulder.Id));
    }
}
