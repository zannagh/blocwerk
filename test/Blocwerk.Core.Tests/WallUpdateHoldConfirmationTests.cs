// <copyright file="WallUpdateHoldConfirmationTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the human-confirmation fact on a carry decision: the row that says "a person looked at this
/// hold", as opposed to the matcher default the review seeds for every old hold. The two used to be the
/// same row, which is why the review queue could never shrink.
/// </summary>
public class WallUpdateHoldConfirmationTests
{
    [Fact]
    public async Task ASeededVerdict_IsNotConfirmed()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (sessions, twin) = await OpenSessionAsync(h);

        // Exactly what the review seeds: the matcher default for every old hold, nobody's sign-off.
        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old.Id, CarryKind.Carried, twin));

        var read = await sessions.GetDecisionsAsync(h.WallId);
        Assert.False(Assert.Single(read.Carryover).Confirmed);
        Assert.Empty(await sessions.GetCarryConfirmationsAsync(h.WallId));
    }

    [Fact]
    public async Task ADeliberateConfirmation_PersistsWithWhoAndWhen_AndSurvivesABulkRewrite()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (sessions, twin) = await OpenSessionAsync(h);

        await sessions.SaveCarryDecisionAsync(
            h.WallId, new CarryoverDecision(old.Id, CarryKind.Carried, twin, Confirmed: true));

        var confirmation = Assert.Single(await sessions.GetCarryConfirmationsAsync(h.WallId));
        Assert.Equal(old.Id, confirmation.OldHoldId);
        Assert.Equal(h.Owner.Id, confirmation.ConfirmedByUserId);
        Assert.Equal(h.Owner.Name, confirmation.ConfirmedByName);

        // The bulk save replaces the whole carryover half and re-states the seeded (unconfirmed) verdict —
        // leaving the carryover step must not wipe what the user reviewed on it.
        await sessions.SaveCarryOutcomeAsync(
            h.WallId, [new CarryoverDecision(old.Id, CarryKind.Carried, twin)], [], []);

        var read = await sessions.GetDecisionsAsync(h.WallId);
        Assert.True(Assert.Single(read.Carryover).Confirmed);
        Assert.Equal(h.Owner.Id, Assert.Single(await sessions.GetCarryConfirmationsAsync(h.WallId)).ConfirmedByUserId);
    }

    [Fact]
    public async Task ABulkSave_NeverConfirms_EvenWhenTheDecisionItReplaysSaysConfirmed()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (sessions, twin) = await OpenSessionAsync(h);

        // The matcher default: nobody has signed this off.
        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old.Id, CarryKind.Carried, twin));

        // Leaving the carryover step ships the whole in-memory half back — including a Confirmed the
        // browser read out of the session in the first place. That echo is not an intent and must not
        // register a confirmation, least of all against whoever happened to press Continue.
        await sessions.SaveCarryOutcomeAsync(
            h.WallId, [new CarryoverDecision(old.Id, CarryKind.Carried, twin, Confirmed: true)], [], []);

        var read = Assert.Single((await sessions.GetDecisionsAsync(h.WallId)).Carryover);
        Assert.Equal(CarryKind.Carried, read.Kind);
        Assert.False(read.Confirmed);
        Assert.Empty(await sessions.GetCarryConfirmationsAsync(h.WallId));
    }

    [Fact]
    public async Task ABulkSave_DoesNotResurrectAConfirmationThatWasCleared()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (sessions, twin) = await OpenSessionAsync(h);

        // One admin confirms, another un-reviews it. A browser that loaded before the un-review still
        // holds Confirmed: true in memory — confirmations do not arrive while you work.
        await sessions.SaveCarryDecisionAsync(
            h.WallId, new CarryoverDecision(old.Id, CarryKind.Carried, twin, Confirmed: true));
        await sessions.ClearCarryConfirmationAsync(h.WallId, old.Id);

        await sessions.SaveCarryOutcomeAsync(
            h.WallId, [new CarryoverDecision(old.Id, CarryKind.Carried, twin, Confirmed: true)], [], []);

        Assert.False(Assert.Single((await sessions.GetDecisionsAsync(h.WallId)).Carryover).Confirmed);
        Assert.Empty(await sessions.GetCarryConfirmationsAsync(h.WallId));
    }

    [Fact]
    public async Task LosingTheTwin_DropsTheSignOff_SoTheHoldComesBackForReview()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (sessions, twin) = await OpenSessionAsync(h);
        await sessions.SaveCarryDecisionAsync(
            h.WallId, new CarryoverDecision(old.Id, CarryKind.Carried, twin, Confirmed: true));

        // Exactly the write the review's two automatic rewrites make — the bin tool deleting the staged
        // twin, and "break match" — leaving the hold carried BLIND at its old position. Nobody reviewed
        // THAT, so the write is unconfirmed and the earlier sign-off goes with the match it was about.
        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old.Id, CarryKind.Carried, null));

        var read = Assert.Single((await sessions.GetDecisionsAsync(h.WallId)).Carryover);
        Assert.Equal(CarryKind.Carried, read.Kind);
        Assert.Null(read.NewHoldId);
        Assert.False(read.Confirmed);
        Assert.Empty(await sessions.GetCarryConfirmationsAsync(h.WallId));
    }

    [Fact]
    public async Task ReConfirmingAnUnchangedVerdict_IsIdempotent()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (sessions, twin) = await OpenSessionAsync(h);
        var confirmed = new CarryoverDecision(old.Id, CarryKind.Carried, twin, Confirmed: true);

        await sessions.SaveCarryDecisionAsync(h.WallId, confirmed);
        var first = Assert.Single(await sessions.GetCarryConfirmationsAsync(h.WallId)).ConfirmedAt;
        await sessions.SaveCarryDecisionAsync(h.WallId, confirmed);

        var again = Assert.Single(await sessions.GetCarryConfirmationsAsync(h.WallId));
        Assert.Equal(first, again.ConfirmedAt);

        await using var db = h.CreateContext();
        Assert.Equal(1, await db.WallUpdateHoldDecisions.CountAsync(d => d.HoldId == old.Id));
    }

    [Fact]
    public async Task ChangingAVerdictDeliberately_Confirms_AndChangingItWithoutOne_ClearsTheConfirmation()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (sessions, twin) = await OpenSessionAsync(h);

        // Carried -> Changed -> Removed, each one a deliberate action: all confirmed.
        await sessions.SaveCarryDecisionAsync(
            h.WallId, new CarryoverDecision(old.Id, CarryKind.Changed, twin, Confirmed: true));
        Assert.True(Assert.Single((await sessions.GetDecisionsAsync(h.WallId)).Carryover).Confirmed);
        await sessions.SaveCarryDecisionAsync(
            h.WallId, new CarryoverDecision(old.Id, CarryKind.Removed, null, Confirmed: true));
        Assert.True(Assert.Single((await sessions.GetDecisionsAsync(h.WallId)).Carryover).Confirmed);

        // An unconfirmed write that MOVES the verdict is a reset, not review work: the sign-off went with
        // the verdict it was about.
        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old.Id, CarryKind.Carried, twin));

        var read = Assert.Single((await sessions.GetDecisionsAsync(h.WallId)).Carryover);
        Assert.Equal(CarryKind.Carried, read.Kind);
        Assert.False(read.Confirmed);
    }

    [Fact]
    public async Task ClearingAConfirmation_LeavesTheVerdictAlone()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (sessions, twin) = await OpenSessionAsync(h);
        await sessions.SaveCarryDecisionAsync(
            h.WallId, new CarryoverDecision(old.Id, CarryKind.Removed, null, Confirmed: true));

        await sessions.ClearCarryConfirmationAsync(h.WallId, old.Id);

        var read = Assert.Single((await sessions.GetDecisionsAsync(h.WallId)).Carryover);
        Assert.Equal(CarryKind.Removed, read.Kind);
        Assert.False(read.Confirmed);
        Assert.Empty(await sessions.GetCarryConfirmationsAsync(h.WallId));
        Assert.NotNull(twin);
    }

    [Fact]
    public async Task ConfirmationIsInvisibleToThePromote()
    {
        using var confirmedHarness = new WallTestHarness();
        using var plainHarness = new WallTestHarness();

        var confirmed = await PromoteOneCarryAsync(confirmedHarness, confirm: true);
        var plain = await PromoteOneCarryAsync(plainHarness, confirm: false);

        Assert.Equal(plain.Generation, confirmed.Generation);
        Assert.Equal(plain.HoldCount, confirmed.HoldCount);
        Assert.Equal(plain.LiveHoldCount, confirmed.LiveHoldCount);
    }

    /// <summary>Promotes a single confirmed-or-not carry verdict and reports the shape of the result.</summary>
    private static async Task<(int Generation, int HoldCount, int LiveHoldCount)> PromoteOneCarryAsync(
        WallTestHarness h, bool confirm)
    {
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (sessions, twin) = await OpenSessionAsync(h);
        await sessions.SaveCarryDecisionAsync(
            h.WallId, new CarryoverDecision(old.Id, CarryKind.Carried, twin, confirm));

        var decisions = await sessions.GetDecisionsAsync(h.WallId);
        await WallUpdateSessionFixture.BigUpdate(h).PromoteAsync(h.WallId, decisions);

        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        var holds = await db.Holds.Where(x => x.WallId == h.WallId).ToListAsync();
        return (wall.CurrentGeneration, holds.Count, holds.Count(x => x.Generation == wall.CurrentGeneration));
    }

    /// <summary>Stages a centre-only update with one staged twin and returns the session service.</summary>
    private static async Task<(WallUpdateSessionService Sessions, Guid Twin)> OpenSessionAsync(WallTestHarness h)
    {
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var centreId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var twin = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1);
        return (WallUpdateSessionFixture.Sessions(h), twin);
    }
}
