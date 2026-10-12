// <copyright file="LookOverSplitTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// "Different hold" on an unsure match takes the pair apart: the old hold goes on as a hold that is kept and not
/// confirmed, with its name and boulders, and the detection stays on the wall as a new hold. No answer afterwards
/// removes the detection, also not for a finding that was called a different hold before the pair was taken apart.
/// </summary>
public class LookOverSplitTests
{
    [Fact]
    public async Task DifferentHold_LeavesTwoHolds_TheOldOneKeptWithItsBoulder_TheNewOneWithoutAnIdentity()
    {
        using var h = new WallTestHarness();
        var (w, _, view) = await SplitAsync(h);

        var old = (await HoldAsync(h, view.HoldId))!;
        var added = (await HoldAsync(h, view.OtherHoldId))!;
        Assert.Equal(("Yellow crimp", (int?)3), (old.Name, old.KeptUnconfirmedGeneration));
        Assert.Equal((null, null, null, false), (added.Name, added.Color, added.KeptUnconfirmedGeneration, added.NeedsReview));
        Assert.Equal((old.WallPanelId, old.Generation, old.X, old.Y), (added.WallPanelId, added.Generation, added.X, added.Y));
        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((old.Id, false, true), (boulder.BoulderHolds.Single().HoldId, boulder.IsHistoric, boulder.NeedsReview));
        await using var db = h.CreateContext();
        Assert.False(await db.BoulderHolds.AnyAsync(bh => bh.HoldId == added.Id));
        Assert.False(await db.HoldGenerationLinks.AnyAsync(l => l.NewHoldId == added.Id));
        Assert.True(await db.HoldGenerationLinks.AnyAsync(l => l.NewHoldId == old.Id && l.OldHoldId == w.TargetId));

        // It is asked on as a hold the update could not find, with the new hold as the one it may be; nothing to undo.
        Assert.Equal((LookOverFindingKind.ReplacedByOther, true, true, true, false), (view.Kind, view.Converted, view.Missing, view.CanReassign, view.CanUndo));
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.UseNewHold, LookOverAnswer.RemoveHold], view.Answers);
    }

    [Fact]
    public async Task NotThereAnyMore_AfterDifferentHold_RemovesOnlyTheOldHold_AndItsBoulderBecomesHistoric()
    {
        using var h = new WallTestHarness();
        var (w, service, view) = await SplitAsync(h);

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold, [w.BoulderId]);

        Assert.Null(await HoldAsync(h, view.HoldId));
        Assert.NotNull(await HoldAsync(h, view.OtherHoldId));
        Assert.True((await BoulderAsync(h, w.BoulderId)).IsHistoric);
        Assert.Equal(LookOverAnswer.RemoveHold, (await FindingAsync(h, w.WallId)).Answer);
    }

    [Fact]
    public async Task StillHere_AfterDifferentHold_ConfirmsTheOldHold_AndUndoMakesItUnconfirmedAgain()
    {
        using var h = new WallTestHarness();
        var (w, service, view) = await SplitAsync(h);

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);

        Assert.Null((await HoldAsync(h, view.HoldId))!.KeptUnconfirmedGeneration);
        Assert.NotNull(await HoldAsync(h, view.OtherHoldId));
        Assert.False((await BoulderAsync(h, w.BoulderId)).IsHistoric);

        await service.UndoAsync(w.WallId, view.Id);

        Assert.Equal(3, (await HoldAsync(h, view.HoldId))!.KeptUnconfirmedGeneration);
        var again = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal((true, view.OtherHoldId), (again.Missing, again.OtherHoldId));
    }

    [Fact]
    public async Task ItIsThisHold_AfterDifferentHold_PutsTheOldIdentityOnTheNewHold()
    {
        using var h = new WallTestHarness();
        var (w, service, view) = await SplitAsync(h);

        await service.ReassignAsync(w.WallId, view.Id, view.OtherHoldId!.Value, [w.BoulderId]);

        Assert.Null(await HoldAsync(h, view.HoldId));
        Assert.Equal("Yellow crimp", (await HoldAsync(h, view.OtherHoldId))!.Name);
        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((view.OtherHoldId, false), ((Guid?)boulder.BoulderHolds.Single().HoldId, boulder.IsHistoric));
    }

    [Fact]
    public async Task NotThereAnyMore_ForAFindingCalledDifferentBeforeThePairWasSplit_KeepsTheDetectionAsANewHold()
    {
        using var h = new WallTestHarness();
        var (w, service, split) = await SplitAsync(h);

        // As it was before: one live hold, the detection wearing the old identity, and a replaced finding about it.
        await using (var db = h.CreateContext())
        {
            await db.Holds.Where(x => x.Id == split.OtherHoldId).ExecuteDeleteAsync();
            await db.Holds.Where(x => x.Id == split.HoldId).ExecuteUpdateAsync(s => s.SetProperty(x => x.KeptUnconfirmedGeneration, (int?)null));
            await db.WallLookOverFindings.Where(f => f.Id == split.Id).ExecuteUpdateAsync(
                s => s.SetProperty(f => f.HoldKept, false).SetProperty(f => f.OtherHoldId, (Guid?)null).SetProperty(f => f.Covered, false));
        }

        var view = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal((w.DetectionId, false, (Guid?)null), (view.HoldId, view.Missing, view.OtherHoldId));
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.RemoveHold], view.Answers);
        var spot = (await HoldAsync(h, view.HoldId))!;

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold, [w.BoulderId]);

        Assert.Null(await HoldAsync(h, view.HoldId));
        var finding = await FindingAsync(h, w.WallId);
        var added = (await HoldAsync(h, finding.OtherHoldId))!;
        Assert.Equal(
            (spot.WallPanelId, spot.Generation, spot.X, spot.Y, null, null),
            (added.WallPanelId, added.Generation, added.X, added.Y, added.Name, added.KeptUnconfirmedGeneration));
        Assert.Equal((LookOverFindingStatus.Settled, (LookOverAnswer?)LookOverAnswer.RemoveHold), (finding.Status, finding.Answer));
        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((true, 0), (boulder.IsHistoric, boulder.BoulderHolds.Count));
    }

    /// <summary>An unsure match with its boulder, answered "different hold"; the finding as it is listed afterwards.</summary>
    private static async Task<(LookWall Wall, WallLookOverService Service, LookOverFindingView View)> SplitAsync(WallTestHarness h)
    {
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal(w.DetectionId, view.HoldId);
        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.DifferentHold, [w.BoulderId]);
        return (w, service, Assert.Single(await service.ListAsync(w.WallId)));
    }
}
