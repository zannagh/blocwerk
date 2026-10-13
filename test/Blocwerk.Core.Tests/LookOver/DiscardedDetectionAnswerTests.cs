// <copyright file="DiscardedDetectionAnswerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Tests.HoldColors;
using Blocwerk.Web.Components.Shared.LookOver;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Enums.NewHoldDiscardReason;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The look-over's cards for discarded detections: their order and the duplicates kept behind their own chip, "Add
/// as a hold" with its undo, "Not a hold", and the two bulk steps, which take only what still fits.
/// </summary>
public class DiscardedDetectionAnswerTests
{
    [Fact]
    public async Task TheCards_ComeLikelyMissedFirst_AndDuplicatesAreBehindTheirOwnChip()
    {
        using var s = await DiscardedScenario.ApplyAsync(
            (HoldColorScenario.RedSpot, 0.6, OnMarker),
            (HoldColorScenario.BlueSpot, 0.99, KnownHoldIn3D),
            (HoldColorScenario.YellowSpot, 0.92, UnchangedSinceOldPhoto),
            (HoldColorScenario.GreenSpot, 0.97, SeenOnNeighbourPanel));

        var listed = await s.LookOver().ListAsync(s.WallId);

        // On no hold before on a hold, the surest first; each says why it was discarded and takes both answers.
        Assert.Equal([0.97, 0.92, 0.6, 0.99], listed.Select(f => f.Confidence!.Value));
        Assert.Equal(["SeenOnNeighbourPanel", "UnchangedSinceOldPhoto", "OnMarker", "KnownHoldIn3D"], listed.Select(f => f.DiscardReason));
        Assert.All(listed, f => Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.AddHold], f.Answers));
        Assert.All(listed, f => Assert.Single(f.Pictures!));

        var listing = new LookOverListing();
        listing.Take(listed);
        var discarded = new LookOverFilter(Kind: LookOverFindingKind.DiscardedDetection);
        Assert.Equal(listed.Take(3).Select(f => f.Id), listing.Queue(LookOverFilter.All, null).Select(f => f.Id));
        Assert.Equal(listed.Take(3).Select(f => f.Id), listing.Queue(discarded, null).Select(f => f.Id));
        Assert.Equal([listed[3].Id], listing.Queue(LookOverFilter.Duplicates, null).Select(f => f.Id));
        Assert.Equal(
            [("All", 3), ("Discarded detections", 3), ("Probably duplicates", 1)],
            listing.Chips(null).Select(c => (c.Label, c.Open)));
        Assert.Equal([listed[3].Id], listing.DuplicateIds());
        Assert.Equal(listed.Take(2).Select(f => f.Id), listing.LikelyMissedIds());
        Assert.Empty(listing.Cards);
    }

    [Fact]
    public async Task AddAsAHold_MakesALiveHoldWithTheOutlineAndASuggestedColour_AndUndoRemovesIt()
    {
        using var s = await DiscardedScenario.ApplyAsync((HoldColorScenario.YellowSpot, 0.97, UnchangedSinceOldPhoto));
        var service = s.LookOver();
        var card = Assert.Single(await service.ListAsync(s.WallId));

        await service.AnswerAsync(s.WallId, card.Id, LookOverAnswer.AddHold, []);

        var answered = Assert.Single(await service.ListAsync(s.WallId));
        Assert.Equal((LookOverFindingStatus.Settled, LookOverAnswer.AddHold, true), (answered.Status, answered.Answer!.Value, answered.CanUndo));
        var hold = Assert.Single(await s.HoldsAsync(), h => h.Id != s.KeptId);
        Assert.Equal(answered.HoldId, hold.Id);
        Assert.Equal(
            (s.PanelId, HoldColorScenario.YellowSpot.X, HoldColorScenario.YellowSpot.Y, 0.07, true, true, 0.97, null),
            (hold.WallPanelId!.Value, hold.X, hold.Y, hold.Radius, hold.IsAutoDetected, hold.NeedsReview, hold.Confidence, hold.Name));
        Assert.Equal(DiscardedScenario.Outline.Select(p => (p.Dx, p.Dy)), hold.ShapePoints!.Select(p => (p.Dx, p.Dy)));
        Assert.Equal(("yellow", true), (hold.Color, hold.HasProposedColor));
        await using (var db = s.Harness.CreateContext())
        {
            var batches = await db.ChangeJournalBatches.Where(b => b.Label == WallLookOverService.BatchLabel).Select(b => b.Id).ToListAsync();
            Assert.True(await db.ChangeJournalEntries.AnyAsync(e => batches.Contains(e.BatchId) && e.EntityType == nameof(Hold)));
        }

        await service.UndoAsync(s.WallId, card.Id);

        Assert.Equal([s.KeptId], (await s.HoldsAsync()).Select(h => h.Id));
        var reopened = Assert.Single(await service.ListAsync(s.WallId));
        Assert.Equal((LookOverFindingStatus.Open, null), (reopened.Status, reopened.HoldId));
    }

    [Fact]
    public async Task NotAHold_SettlesWithoutAHold_AndCanBeTakenBack()
    {
        using var s = await DiscardedScenario.ApplyAsync((HoldColorScenario.YellowSpot, 0.97, UnchangedSinceOldPhoto));
        var service = s.LookOver();
        var card = Assert.Single(await service.ListAsync(s.WallId));

        await service.AnswerAsync(s.WallId, card.Id, LookOverAnswer.Settle);

        Assert.Equal(0, (await service.GetStateAsync(s.WallId))!.OpenFindings);
        Assert.Equal([s.KeptId], (await s.HoldsAsync()).Select(h => h.Id));
        Assert.True(Assert.Single(await service.ListAsync(s.WallId)).CanUndo);
        await service.UndoAsync(s.WallId, card.Id);
        Assert.Equal(1, (await service.GetStateAsync(s.WallId))!.OpenFindings);
    }

    [Fact]
    public async Task AddAll_TakesOnlySureDetectionsOnNoHold_AndOfTwoOnOneSpotOnlyTheSurer()
    {
        using var s = await DiscardedScenario.ApplyAsync(
            (HoldColorScenario.YellowSpot, 0.97, UnchangedSinceOldPhoto),
            (HoldColorScenario.YellowSpot, 0.93, KnownHoldIn3D),
            (HoldColorScenario.RedSpot, 0.6, OnMarker),
            (HoldColorScenario.BlueSpot, 0.99, KnownHoldIn3D),
            (HoldColorScenario.GreenSpot, 0.91, OutsideOldPhoto));
        var service = s.LookOver();
        var listed = await service.ListAsync(s.WallId);

        var result = await service.AddDetectionsAsync(s.WallId, [.. listed.Select(f => f.Id)]);

        Assert.Equal((2, 5), (result.Settled, result.Asked));
        var added = (await s.HoldsAsync()).Where(h => h.Id != s.KeptId).OrderBy(h => h.Confidence).ToList();
        Assert.Equal([(0.91, "green"), (0.97, "yellow")], added.Select(h => (h.Confidence, h.Color!)));

        // The second detection of the yellow hold is covered by the hold just added: a duplicate now.
        var after = await service.ListAsync(s.WallId);
        var open = after.Where(f => f.Status == LookOverFindingStatus.Open).ToList();
        Assert.Equal([(0.6, false), (0.93, true), (0.99, true)], open.OrderBy(f => f.Confidence).Select(f => (f.Confidence!.Value, f.ProbablyDuplicate)));

        var settled = await service.SettleAllAsync(
            s.WallId, LookOverFindingKind.DiscardedDetection, [.. open.Where(f => f.ProbablyDuplicate).Select(f => f.Id)], onlyWithoutBoulders: false);
        Assert.Equal((2, 2), (settled.Settled, settled.Asked));
        Assert.Single((await service.ListAsync(s.WallId)).Where(f => f.Status == LookOverFindingStatus.Open));
    }
}
