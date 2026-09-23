// <copyright file="RelocationSuggestionFlowTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The "this hold moved" suggestions end to end through the real services: generated once the matcher ran,
/// persisted with the session, and — only when a person accepts one — promoted exactly like a manual
/// "changed" mark. See <see cref="RelocationScenario"/> for the wall.
/// </summary>
public class RelocationSuggestionFlowTests
{
    // Only disappeared × appeared: the matched pair A↔A' is never offered even though A, A' and M all
    // carry the SAME green fingerprint — and the unlike red N' is not paired with anything.
    [Fact]
    public async Task Resume_SuggestsOnlyDisappearedTimesAppeared()
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h);

        await s.BigUpdate().ResumeAsync(s.WallId);
        var suggestions = await s.Sessions().GetRelocationSuggestionsAsync(s.WallId);

        var only = Assert.Single(suggestions);
        Assert.Equal(s.OldM, only.OldHoldId);
        Assert.Equal(s.NewM, only.NewHoldId);
        Assert.Equal(RelocationProposalStatus.Pending, only.Status);
        Assert.False(only.Metric);
        Assert.True(only.Score >= 0.9);
    }

    // A resume shows the same list — same row, same id, decisions intact — and never recomputes it.
    [Fact]
    public async Task Resume_Again_KeepsTheSameSuggestionsAndTheirVerdicts()
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h);
        await s.BigUpdate().ResumeAsync(s.WallId);
        var first = Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));
        await s.Sessions().DecideRelocationAsync(s.WallId, first.Id, RelocationDecision.Moved);

        await s.BigUpdate().ResumeAsync(s.WallId);
        await s.BigUpdate().ResumeAsync(s.WallId);

        var again = Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(RelocationProposalStatus.Accepted, again.Status);
        await using var db = h.CreateContext();
        Assert.Equal(1, await db.WallUpdateRelocationProposals.CountAsync());
        Assert.NotNull((await db.WallUpdateSessions.SingleAsync()).RelocationsProposedAt);
    }

    // Accept → promote: M' IS M, moved. Lineage (Changed), M's curation on M', the boulder advanced onto
    // M' and flagged for revision, M retained as gen-2 history, no blind clone of M anywhere.
    [Fact]
    public async Task Accept_ThenPromote_LinksCarriesCurationAndFlagsTheBoulder()
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h);
        await s.BigUpdate().ResumeAsync(s.WallId);
        var suggestion = Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));

        await s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, RelocationDecision.Moved);
        await s.ContinueAsync();
        await s.PromoteAsync();

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldM);
        Assert.Equal(s.NewM, link.NewHoldId);
        Assert.Equal(HoldGenerationLinkKind.Changed, link.Kind);

        var moved = await db.Holds.SingleAsync(x => x.Id == s.NewM);
        Assert.Equal(3, moved.Generation);
        Assert.Equal(("Green blob", "#2ecc71"), (moved.Name, moved.Color));
        Assert.Equal(HoldCategory.Foot, moved.Category);
        Assert.Equal(HoldMaterial.Wood, moved.Material);
        Assert.Equal(HoldHandType.Sloper, moved.HandType);
        Assert.Equal((0.8, 0.3), (moved.X, moved.Y));

        var boulder = await db.Boulders.SingleAsync(b => b.Id == s.BoulderId);
        Assert.True(boulder.NeedsReview);
        Assert.False(boulder.IsHistoric);
        Assert.Contains(await db.BoulderHolds.Where(bh => bh.BoulderId == s.BoulderId).Select(bh => bh.HoldId).ToListAsync(), id => id == s.NewM);

        Assert.Equal(2, (await db.Holds.SingleAsync(x => x.Id == s.OldM)).Generation);
        Assert.Equal(3, await db.Holds.CountAsync(x => x.Generation == 3)); // A', M', N' — no clone of M.
    }

    // D-A made executable: an accepted suggestion and a person marking M "changed" onto M' by hand end
    // in the SAME state — link kind, boulder flags and generation, successor curation.
    [Fact]
    public async Task Accept_IsIndistinguishableFromAManualChangedMark()
    {
        using var viaSuggestion = new WallTestHarness();
        var a = await RelocationScenario.SeedAsync(viaSuggestion);
        await a.BigUpdate().ResumeAsync(a.WallId);
        var suggestion = Assert.Single(await a.Sessions().GetRelocationSuggestionsAsync(a.WallId));
        await a.Sessions().DecideRelocationAsync(a.WallId, suggestion.Id, RelocationDecision.Moved);
        await a.ContinueAsync();
        await a.PromoteAsync();

        using var byHand = new WallTestHarness();
        var b = await RelocationScenario.SeedAsync(byHand, fingerprints: false);
        await b.BigUpdate().ResumeAsync(b.WallId);
        await b.Sessions().SaveCarryDecisionAsync(
            b.WallId, new CarryoverDecision(b.OldM, CarryKind.Changed, b.NewM, Confirmed: true));
        await b.ContinueAsync();
        await b.PromoteAsync();

        Assert.Equal(await OutcomeAsync(viaSuggestion, a), await OutcomeAsync(byHand, b));
    }

    // Dismiss → exactly today's outcome: M carried blind (a Same-linked clone), M' kept as a plain new hold
    // with no lineage, the boulder NOT flagged.
    [Fact]
    public async Task Dismiss_ThenPromote_IsTodaysOutcome()
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h);
        await s.BigUpdate().ResumeAsync(s.WallId);
        var suggestion = Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));

        await s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, RelocationDecision.Dismiss);
        await s.ContinueAsync();
        await s.PromoteAsync();

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldM);
        Assert.Equal(HoldGenerationLinkKind.Same, link.Kind);
        Assert.NotEqual(s.NewM, link.NewHoldId);
        Assert.False(await db.HoldGenerationLinks.AnyAsync(l => l.NewHoldId == s.NewM));
        Assert.Equal(3, (await db.Holds.SingleAsync(x => x.Id == s.NewM)).Generation);
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Equal(RelocationProposalStatus.Dismissed, (await db.WallUpdateRelocationProposals.SingleAsync()).Status);
    }

    // Undoing an accept restores the carried-in-place default, so the promote is today's outcome again.
    [Fact]
    public async Task UndoAccept_RestoresTheDefaultVerdict()
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h);
        await s.BigUpdate().ResumeAsync(s.WallId);
        var suggestion = Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));
        await s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, RelocationDecision.Moved);

        var accepted = Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));
        await s.Sessions().DecideRelocationAsync(s.WallId, accepted.Id, RelocationDecision.Dismiss);

        var verdict = Assert.Single((await s.Sessions().GetDecisionsAsync(s.WallId)).Carryover);
        Assert.Equal((CarryKind.Carried, (Guid?)null, false), (verdict.Kind, verdict.NewHoldId, verdict.Confirmed));
    }

    // Marker wall: sizes measured onto the HOLDS (not yet into their fingerprints) still take part, so
    // the suggestion is scored on size too and is not labelled lower-confidence.
    [Fact]
    public async Task MarkerWall_MeasuredHolds_GiveAMetricSuggestion()
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h, glyphs: true);
        await using (var db = h.CreateContext())
        {
            foreach (var hold in await db.Holds.Where(x => x.Id == s.OldM || x.Id == s.NewM).ToListAsync())
            {
                (hold.WidthMm, hold.HeightMm, hold.AreaMm2) = (30, 45, 1100);
            }

            await db.SaveChangesAsync();
        }

        await s.BigUpdate().ResumeAsync(s.WallId);

        var only = Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));
        Assert.True(only.Metric);
        Assert.Equal((s.OldM, s.NewM), (only.OldHoldId, only.NewHoldId));
    }

    // A wall whose holds carry no fingerprint (and no outliner wired): no suggestions, no error, and the
    // session still records that it looked, so a resume does not try again.
    [Fact]
    public async Task NoFingerprints_NoSuggestions_NoErrors()
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h, fingerprints: false);

        var session = await s.BigUpdate().ResumeAsync(s.WallId);

        Assert.Equal(AutoMatchStatus.Ok, session.AutoMatchStatus);
        Assert.Empty(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));
        await using var db = h.CreateContext();
        Assert.NotNull((await db.WallUpdateSessions.SingleAsync()).RelocationsProposedAt);
    }

    private static async Task<string> OutcomeAsync(WallTestHarness h, RelocationScenario s)
    {
        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldM);
        var successor = await db.Holds.SingleAsync(x => x.Id == link.NewHoldId);
        var boulder = await db.Boulders.SingleAsync(b => b.Id == s.BoulderId);
        var members = await db.BoulderHolds.CountAsync(bh => bh.BoulderId == s.BoulderId);
        return $"{link.Kind}|{link.NewHoldId == s.NewM}|{successor.Name}|{successor.Category}|{successor.NeedsReview}"
            + $"|{boulder.NeedsReview}|{boulder.IsHistoric}|{boulder.Generation}|{members}";
    }
}
