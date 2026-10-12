// <copyright file="LookOverMissingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The answers of "Holds the update could not find": it is this hold (a merge into a hold of the new photo, for wall
/// admins), it is here (the editor's move), and "not there any more" for many unused holds at once.
/// </summary>
public class LookOverMissingTests
{
    [Fact]
    public async Task ItIsThisHold_MergesTheOldHoldIntoTheOneTapped_AndItsBoulderUsesThatHold()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var service = Service(h);
        var view = (await service.ListAsync(k.Wall.WallId)).Single(v => v.HoldId == k.UsedId);
        Assert.Equal((true, true, "Yellow crimp"), (view.Missing, view.CanReassign, view.HoldName));

        await service.ReassignAsync(k.Wall.WallId, view.Id, k.FoundId, [k.Wall.BoulderId]);

        Assert.Null(await HoldAsync(h, k.UsedId));
        var target = (await HoldAsync(h, k.FoundId))!;
        Assert.Equal(("Yellow crimp", (int?)null), (target.Name, target.KeptUnconfirmedGeneration));
        var boulder = await BoulderAsync(h, k.Wall.BoulderId);
        Assert.Equal((k.FoundId, false), (boulder.BoulderHolds.Single().HoldId, boulder.IsHistoric));
        var finding = (await FindingsAsync(h, k.Wall.WallId)).Single(f => f.Id == view.Id);
        Assert.Equal(
            (LookOverFindingStatus.Settled, (LookOverAnswer?)LookOverAnswer.Reassign, (Guid?)k.FoundId, 1),
            (finding.Status, finding.Answer, finding.OtherHoldId, finding.AnswerBoulders));
        await using var db = h.CreateContext();
        Assert.True(await db.ChangeJournalBatches.AnyAsync(b => b.Id == finding.MergeBatchId && b.Label == HoldDuplicateService.BatchLabel));
    }

    [Fact]
    public async Task ItIsThisHold_IsRefused_ForAModerator_ForAnotherUnconfirmedHold_AndWhenTheBouldersChanged()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var service = Service(h);
        var view = (await service.ListAsync(k.Wall.WallId)).Single(v => v.HoldId == k.UsedId);

        await Assert.ThrowsAsync<UserFacingException>(() => service.ReassignAsync(k.Wall.WallId, view.Id, k.UnusedId, [k.Wall.BoulderId]));
        await Assert.ThrowsAsync<UserFacingException>(() => service.ReassignAsync(k.Wall.WallId, view.Id, Guid.NewGuid(), [k.Wall.BoulderId]));
        var changed = await Assert.ThrowsAsync<UserFacingException>(() => service.ReassignAsync(k.Wall.WallId, view.Id, k.FoundId, []));
        Assert.Equal(WallLookOverService.BouldersChanged, changed.Message);

        h.ActingUser = await AddMemberAsync(h, k.Wall.WallId, WallRole.Moderator);
        var asModerator = (await service.ListAsync(k.Wall.WallId)).Single(v => v.Id == view.Id);
        Assert.Equal((true, false), (asModerator.Missing, asModerator.CanReassign));
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.RemoveHold], asModerator.Answers);
        await Assert.ThrowsAsync<UserFacingException>(() => service.ReassignAsync(k.Wall.WallId, view.Id, k.FoundId, [k.Wall.BoulderId]));

        Assert.NotNull(await HoldAsync(h, k.UsedId));
        Assert.Equal(LookOverFindingStatus.Open, (await FindingsAsync(h, k.Wall.WallId)).Single(f => f.Id == view.Id).Status);
    }

    [Fact]
    public async Task ItIsHere_MovesTheHold_ConfirmsIt_AndAsksForALookAtItsOutline()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var service = Service(h);
        h.ActingUser = await AddMemberAsync(h, k.Wall.WallId, WallRole.Moderator);
        var view = (await service.ListAsync(k.Wall.WallId)).Single(v => v.HoldId == k.UsedId);

        await service.MoveAsync(k.Wall.WallId, view.Id, 0.7, 0.35);

        var hold = (await HoldAsync(h, k.UsedId))!;
        Assert.Equal((0.7, 0.35, 0.03, true, (int?)null), (hold.X, hold.Y, hold.Radius, hold.NeedsReview, hold.KeptUnconfirmedGeneration));
        var boulder = await BoulderAsync(h, k.Wall.BoulderId);
        Assert.Equal((k.UsedId, false), (boulder.BoulderHolds.Single().HoldId, boulder.IsHistoric));
        var finding = (await FindingsAsync(h, k.Wall.WallId)).Single(f => f.Id == view.Id);
        Assert.Equal((LookOverFindingStatus.Settled, (LookOverAnswer?)LookOverAnswer.Moved), (finding.Status, finding.Answer));

        // Answered already: a second tap does nothing.
        await Assert.ThrowsAsync<UserFacingException>(() => service.MoveAsync(k.Wall.WallId, view.Id, 0.1, 0.1));
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 0)]
    public async Task NotThereAnyMoreForMany_RemovesOnlyUnusedHolds_OnlyThoseUnderANewHoldWhenAsked_InOneJournalBatch(bool covered, bool onlyCovered, int removed)
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h, covered);
        var service = Service(h);
        var ids = (await service.ListAsync(k.Wall.WallId)).Select(v => v.Id).ToList();

        var result = await service.RemoveUnusedAsync(k.Wall.WallId, ids, onlyCovered);

        Assert.Equal(new LookOverBulkResult(removed, 2), result);
        Assert.Equal(removed == 0, await HoldAsync(h, k.UnusedId) is not null);

        // The hold a boulder uses is never removed in bulk, and the big new hold stays.
        Assert.NotNull(await HoldAsync(h, k.UsedId));
        Assert.False((await BoulderAsync(h, k.Wall.BoulderId)).IsHistoric);
        Assert.Equal(k.BigId is not null, await HoldAsync(h, k.BigId) is not null);
        var unused = (await FindingsAsync(h, k.Wall.WallId)).Single(f => f.OldHoldId == k.UnusedOldId);
        Assert.Equal(removed == 1 ? LookOverFindingStatus.Settled : LookOverFindingStatus.Open, unused.Status);
        await using var db = h.CreateContext();
        var batches = await db.ChangeJournalBatches.Where(b => b.ScopeId == k.Wall.WallId && b.Label == WallLookOverService.RemoveBatchLabel).ToListAsync();
        Assert.Equal(removed, batches.Count);
        Assert.Equal(removed, await db.ChangeJournalEntries.CountAsync(e => e.EntityType == nameof(Hold) && e.Op == ChangeJournalOp.Delete));
    }

    [Fact]
    public async Task ThePhotosHolds_SayWhichAreUnconfirmed_AndThePicturesCarryTheOutlines()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h, covered: true);
        var service = Service(h);
        var view = (await service.ListAsync(k.Wall.WallId)).Single(v => v.HoldId == k.UnusedId);

        var holds = await service.GetPanelHoldsAsync(k.Wall.WallId, view.PanelId!.Value);

        Assert.Equal(new[] { k.UnusedId, k.UsedId }.Order(), holds.Where(x => x.Kept).Select(x => x.Id).Order());
        Assert.Contains(holds, x => x.Id == k.BigId && !x.Kept);
        Assert.Equal((true, k.BigId), (view.Covered, view.OtherHoldId));

        // "Before": the old hold on the old photo. "Now": the copy (faded) and the new hold over it, with its outline.
        var before = view.Pictures!.Single(p => p.View == CheckCropView.Old);
        var now = view.Pictures!.Single(p => p.View == CheckCropView.New);
        Assert.False(Assert.Single(before.Outlines).Kept);
        Assert.Equal([true, false], now.Outlines.Select(o => o.Kept));
        Assert.Equal(4, now.Outlines[1].Shape!.Count);
        Assert.InRange(now.Outlines[0].X, 0.45, 0.55);
    }
}
