// <copyright file="RelocationSameHoldTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The "Same hold" answer to a "Possibly moved" suggestion: the photo shifted, the hold did not. It is
/// carried like a normal confirmed match — lineage Same, curation copied, boulders re-pointed, nothing
/// flagged — and it follows the same undo, fold and claim rules as "Moved here". See
/// <see cref="RelocationScenario"/> for the wall.
/// </summary>
public class RelocationSameHoldTests
{
    [Fact]
    public async Task SameHold_ThenPromote_LinksSameCopiesCurationAndFlagsNothing()
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h);
        var suggestion = await SuggestAsync(s);

        await s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, RelocationDecision.SameHold);
        await s.ContinueAsync();
        await s.PromoteAsync();

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldM);
        Assert.Equal((s.NewM, HoldGenerationLinkKind.Same), (link.NewHoldId, link.Kind));

        var successor = await db.Holds.SingleAsync(x => x.Id == s.NewM);
        Assert.Equal(3, successor.Generation);
        Assert.Equal(("Green blob", "#2ecc71"), (successor.Name, successor.Color));
        Assert.Equal(HoldCategory.Foot, successor.Category);
        Assert.Equal(HoldMaterial.Wood, successor.Material);

        var boulder = await db.Boulders.SingleAsync(b => b.Id == s.BoulderId);
        Assert.False(boulder.NeedsReview);
        Assert.False(boulder.IsHistoric);
        var members = await db.BoulderHolds.Where(bh => bh.BoulderId == s.BoulderId).Select(bh => bh.HoldId).ToListAsync();
        Assert.Contains(s.NewM, members);
        Assert.Equal(3, await db.Holds.CountAsync(x => x.Generation == 3)); // A', M', N' — no clone of M.
        Assert.Equal(RelocationProposalStatus.AcceptedAsSame, (await db.WallUpdateRelocationProposals.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(RelocationDecision.Moved)]
    [InlineData(RelocationDecision.SameHold)]
    public async Task Undo_RestoresTheDefaultVerdict(RelocationDecision answer)
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h);
        var suggestion = await SuggestAsync(s);
        await s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, answer);

        await s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, RelocationDecision.Dismiss);

        var verdict = Assert.Single((await s.Sessions().GetDecisionsAsync(s.WallId)).Carryover);
        Assert.Equal((CarryKind.Carried, (Guid?)null, false), (verdict.Kind, verdict.NewHoldId, verdict.Confirmed));
    }

    // Undo only reverts the verdict the accept wrote: a person who then marked it "changed" by hand keeps that.
    [Fact]
    public async Task UndoSameHold_LeavesALaterHandMadeVerdictAlone()
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h);
        var suggestion = await SuggestAsync(s);
        await s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, RelocationDecision.SameHold);
        await s.Sessions().SaveCarryDecisionAsync(
            s.WallId, new CarryoverDecision(s.OldM, CarryKind.Changed, s.NewM, Confirmed: true));

        await s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, RelocationDecision.Dismiss);

        var verdict = Assert.Single((await s.Sessions().GetDecisionsAsync(s.WallId)).Carryover);
        Assert.Equal((CarryKind.Changed, (Guid?)s.NewM), (verdict.Kind, verdict.NewHoldId));
    }

    // The promote honours either answer exactly once even from an empty payload (a stale bulk save).
    [Theory]
    [InlineData(RelocationDecision.Moved, HoldGenerationLinkKind.Changed, true)]
    [InlineData(RelocationDecision.SameHold, HoldGenerationLinkKind.Same, false)]
    public async Task Promote_FoldsTheAnswerOnce_EvenFromAnEmptyPayload(
        RelocationDecision answer, HoldGenerationLinkKind expected, bool flagged)
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h);
        var suggestion = await SuggestAsync(s);
        await s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, answer);

        await s.BigUpdate().PromoteAsync(s.WallId, Empty(), s.SessionId);
        await Assert.ThrowsAnyAsync<Exception>(() => s.BigUpdate().PromoteAsync(s.WallId, Empty(), s.SessionId));

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldM);
        Assert.Equal((s.NewM, expected), (link.NewHoldId, link.Kind));
        Assert.Equal(1, await db.HoldGenerationLinks.CountAsync(l => l.NewHoldId == s.NewM));
        Assert.Equal(flagged, (await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
    }

    // Another old hold already carried onto the new one: neither answer may merge them.
    [Theory]
    [InlineData(RelocationDecision.Moved)]
    [InlineData(RelocationDecision.SameHold)]
    public async Task Accept_RefusesANewHoldAnotherOldHoldClaims(RelocationDecision answer)
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h);
        var suggestion = await SuggestAsync(s);
        await s.Sessions().SaveCarryDecisionAsync(
            s.WallId, new CarryoverDecision(s.OldA, CarryKind.Carried, s.NewM, Confirmed: true));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, answer));

        var still = Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));
        Assert.Equal(RelocationProposalStatus.Pending, still.Status);
    }

    [Fact]
    public void Fold_SameHoldBecomesACarriedVerdict_ButALaterDeliberateVerdictWins()
    {
        Guid oldM = Guid.NewGuid(), newM = Guid.NewGuid(), other = Guid.NewGuid();

        var blind = Fold(new CarryoverDecision(oldM, CarryKind.Carried, null));
        Assert.Equal((CarryKind.Carried, (Guid?)newM), (blind.Kind, blind.NewHoldId));
        var missing = Assert.Single(RelocationFold.Apply(Empty(), [(oldM, newM, CarryKind.Carried)]).Carryover);
        Assert.Equal((CarryKind.Carried, (Guid?)newM, true), (missing.Kind, missing.NewHoldId, missing.Confirmed));

        Assert.Equal(CarryKind.Removed, Fold(new CarryoverDecision(oldM, CarryKind.Removed, null)).Kind);
        Assert.Equal(other, Fold(new CarryoverDecision(oldM, CarryKind.Carried, other)).NewHoldId);

        var claimed = RelocationFold.Apply(
            Empty(new CarryoverDecision(oldM, CarryKind.Carried, null), new CarryoverDecision(other, CarryKind.Carried, newM)),
            [(oldM, newM, CarryKind.Carried)]);
        Assert.Null(claimed.Carryover.Single(d => d.OldHoldId == oldM).NewHoldId);

        CarryoverDecision Fold(CarryoverDecision current) =>
            RelocationFold.Apply(Empty(current), [(oldM, newM, CarryKind.Carried)]).Carryover.Single(d => d.OldHoldId == oldM);
    }

    private static async Task<RelocationSuggestion> SuggestAsync(RelocationScenario s)
    {
        await s.BigUpdate().ResumeAsync(s.WallId);
        return Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));
    }

    private static BigUpdateConfirmation Empty(params CarryoverDecision[] carryover) =>
        new(carryover.ToList(), [], [], []);
}
