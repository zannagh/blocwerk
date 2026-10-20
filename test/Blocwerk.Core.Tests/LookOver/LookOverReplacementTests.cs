// <copyright file="LookOverReplacementTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// "Different hold, and THIS hold replaced it" on an unsure match (The Attic, October 2026, "Greetings to Patrick"): the
/// matcher paired the old orange hold with a brown hold beside it, while a new purple hold sits on the orange's spot.
/// Naming the purple hold takes the pair apart (the brown hold stays a new hold of its own) and makes the purple hold the
/// one the per-boulder "use the new hold" moves boulders onto.
/// </summary>
public class LookOverReplacementTests
{
    [Fact]
    public async Task TheNamedReplacement_IsWhatUseTheNewHoldMovesTheYesBoulderOnto()
    {
        using var h = new WallTestHarness();
        var (w, second, view, purple) = await GreetingsAsync(h);
        var service = Service(h);

        await service.DifferentHoldAsync(w.WallId, view.Id, purple, [w.BoulderId, second]);

        var replaced = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal((LookOverFindingKind.ReplacedByOther, (Guid?)purple, true), (replaced.Kind, replaced.OtherHoldId, replaced.Missing));
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.UseNewHold, LookOverAnswer.RemoveHold], replaced.Answers);
        var brown = await BrownAsync(h, w, purple);
        Assert.Null(brown.Name);

        await service.AnswerAsync(w.WallId, replaced.Id, LookOverAnswer.UseNewHold, [w.BoulderId, second], [w.BoulderId]);

        Assert.Null(await HoldAsync(h, replaced.HoldId));
        var yes = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((purple, false), (yes.BoulderHolds.Single().HoldId, yes.IsHistoric));
        Assert.True((await BoulderAsync(h, second)).IsHistoric);
        await using var db = h.CreateContext();
        Assert.True(await db.Holds.AnyAsync(x => x.Id == brown.Id));
        Assert.False(await db.BoulderHolds.AnyAsync(bh => bh.HoldId == brown.Id));
        Assert.Equal(purple, (await FindingAsync(h, w.WallId)).OtherHoldId);
    }

    [Fact]
    public async Task WithoutANamedReplacement_TheDetectionItWasPairedWithStaysTheNewHold()
    {
        using var h = new WallTestHarness();
        var (w, second, view, purple) = await GreetingsAsync(h);

        await Service(h).AnswerAsync(w.WallId, view.Id, LookOverAnswer.DifferentHold, [w.BoulderId, second]);

        var replaced = Assert.Single(await Service(h).ListAsync(w.WallId));
        Assert.NotEqual(purple, replaced.OtherHoldId);
        Assert.Equal((await BrownAsync(h, w, purple)).Id, replaced.OtherHoldId);
    }

    [Fact]
    public async Task AReplacementThatIsTheHoldItself_NotOnThePhoto_OrWithOtherBoulders_IsRefused_AndNothingChanges()
    {
        using var h = new WallTestHarness();
        var (w, second, view, purple) = await GreetingsAsync(h);
        var service = Service(h);

        var itself = await Assert.ThrowsAsync<UserFacingException>(() => service.DifferentHoldAsync(w.WallId, view.Id, view.HoldId!.Value, [w.BoulderId, second]));
        var elsewhere = await Assert.ThrowsAsync<UserFacingException>(() => service.DifferentHoldAsync(w.WallId, view.Id, Guid.NewGuid(), [w.BoulderId, second]));
        var boulders = await Assert.ThrowsAsync<UserFacingException>(() => service.DifferentHoldAsync(w.WallId, view.Id, purple, [w.BoulderId]));

        Assert.Equal(
            (WallLookOverService.ReplacementNotOnPhoto, WallLookOverService.ReplacementNotOnPhoto, WallLookOverService.BouldersChanged),
            (itself.Message, elsewhere.Message, boulders.Message));
        var still = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Equal((LookOverFindingKind.UnsureMatch, LookOverFindingStatus.Open, false), (still.Kind, still.Status, still.Converted));
    }

    /// <summary>
    /// An unsure match (the old hold carried onto the detection, "brown") with two boulders on it, and a new "purple" hold
    /// on the same photo beside it, as a person would add it in the editor.
    /// </summary>
    private static async Task<(LookWall Wall, Guid Second, LookOverFindingView View, Guid Purple)> GreetingsAsync(WallTestHarness h)
    {
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var finding = await FindingAsync(h, w.WallId);
        Guid second, purple;
        await using (var db = h.CreateContext())
        {
            var brown = await db.Holds.AsNoTracking().SingleAsync(x => x.Id == finding.HoldId);
            var first = await db.Boulders.AsNoTracking().SingleAsync(b => b.Id == w.BoulderId);
            var boulder = new Boulder { WallId = w.WallId, Name = "Greetings to Patrick", CreatedByUserId = h.Owner.Id, Generation = first.Generation };
            db.Boulders.Add(boulder);
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = brown.Id, Type = HoldType.Normal });
            var hold = new Hold
            {
                WallId = w.WallId, WallPanelId = brown.WallPanelId, Generation = brown.Generation, X = TargetX + 0.08, Y = TargetY, Radius = 0.03,
                Color = "purple",
            };
            db.Holds.Add(hold);
            await db.SaveChangesAsync();
            (second, purple) = (boulder.Id, hold.Id);
        }

        var view = Assert.Single(await Service(h).ListAsync(w.WallId));
        Assert.Equal((LookOverFindingKind.UnsureMatch, w.DetectionId), (view.Kind, view.HoldId));
        return (w, second, view, purple);
    }

    /// <summary>The detection split off as a new hold: on the photo, neither the old hold nor the purple one.</summary>
    private static async Task<Hold> BrownAsync(WallTestHarness h, LookWall w, Guid purple)
    {
        var finding = await FindingAsync(h, w.WallId);
        await using var db = h.CreateContext();
        var panel = (await db.Holds.AsNoTracking().SingleAsync(x => x.Id == finding.HoldId)).WallPanelId;
        return await db.Holds.AsNoTracking().SingleAsync(x =>
            x.WallPanelId == panel && x.Generation == finding.Generation && x.Id != finding.HoldId && x.Id != purple
            && Math.Abs(x.X - TargetX) < Tolerance && Math.Abs(x.Y - TargetY) < Tolerance);
    }
}
