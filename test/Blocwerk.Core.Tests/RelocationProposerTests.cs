// <copyright file="RelocationProposerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The rules around the relocation matcher — thresholds per wall type, the marker-wall plausibility cut —
/// and the promote side: how accepted suggestions fold into the payload and that they apply exactly once.
/// </summary>
public class RelocationProposerTests
{
    // Two DIFFERENT green holds without millimetres: a same-hue look-alike scores in the band that fooled
    // the fingerprint in the field (0.75..0.90). The matcher alone would propose it; the proposer must not.
    [Fact]
    public void LookAlikeWithoutMm_IsBelowThresholdOnANonMarkerWall()
    {
        var (old, appeared, fingerprints) = Pair(RelocationScenario.Green(), LookAlike());
        var score = HoldFingerprint.Similarity(fingerprints[old.Id], fingerprints[appeared.Id]);
        Assert.InRange(score, HoldRelocationMatcher.DefaultMinScore, HoldRelocationProposer.LookAlikeMinScore - 0.001);

        Assert.Empty(HoldRelocationProposer.Propose([old], [appeared], fingerprints, markerWall: false));

        // Same pair on a marker wall but still without sizes: still the strict, size-free tier.
        Assert.Empty(HoldRelocationProposer.Propose([old], [appeared], fingerprints, markerWall: true));
    }

    // With millimetres on a marker wall, size does the separating: the same-size pair is suggested (as
    // metric), while a look-alike of a different size falls away.
    [Fact]
    public void MarkerWallWithMm_SizeSeparatesLookAlikes()
    {
        var (old, same, fingerprints) = Pair(RelocationScenario.Green(40), LookAlike(40));
        var metric = Assert.Single(HoldRelocationProposer.Propose([old], [same], fingerprints, markerWall: true));
        Assert.True(metric.Metric);

        var (old2, bigger, fingerprints2) = Pair(RelocationScenario.Green(40), LookAlike(60));
        Assert.Empty(HoldRelocationProposer.Propose([old2], [bigger], fingerprints2, markerWall: true));
    }

    // On a marker wall a staged hold the marker pass measured but placed on no facet (the crash mat) is
    // never a destination, however alike; unmeasured holds and on-facet holds still are. Long moves are fine.
    [Fact]
    public void MarkerWall_OffFacetDestination_IsNeverSuggested()
    {
        var (old, mat, fingerprints) = Pair(RelocationScenario.Green(40), RelocationScenario.Green(40));
        old.FacetId = "0";
        mat.MetricSource = "glyph";
        Assert.Empty(HoldRelocationProposer.Propose([old], [mat], fingerprints, markerWall: true));

        mat.FacetId = "5";
        mat.X = 0.99;
        Assert.Single(HoldRelocationProposer.Propose([old], [mat], fingerprints, markerWall: true));
    }

    [Fact]
    public void Fold_AcceptedPairBecomesAChangedVerdict_ButALaterDeliberateVerdictWins()
    {
        Guid oldM = Guid.NewGuid(), newM = Guid.NewGuid(), other = Guid.NewGuid();

        var blind = Fold(new CarryoverDecision(oldM, CarryKind.Carried, null));
        Assert.Equal((CarryKind.Changed, (Guid?)newM), (blind.Kind, blind.NewHoldId));
        Assert.Equal(CarryKind.Changed, Assert.Single(RelocationFold.Apply(Confirmation(), [(oldM, newM, CarryKind.Changed)]).Carryover).Kind);

        Assert.Equal(CarryKind.Removed, Fold(new CarryoverDecision(oldM, CarryKind.Removed, null)).Kind);
        Assert.Equal(other, Fold(new CarryoverDecision(oldM, CarryKind.Carried, other)).NewHoldId);

        // New hold already another old hold's successor → no silent merge.
        var claimed = RelocationFold.Apply(
            Confirmation(new CarryoverDecision(oldM, CarryKind.Carried, null), new CarryoverDecision(other, CarryKind.Carried, newM)),
            [(oldM, newM, CarryKind.Changed)]);
        Assert.Null(claimed.Carryover.Single(d => d.OldHoldId == oldM).NewHoldId);

        CarryoverDecision Fold(CarryoverDecision current) =>
            RelocationFold.Apply(Confirmation(current), [(oldM, newM, CarryKind.Changed)]).Carryover.Single(d => d.OldHoldId == oldM);
    }

    // The promote honours an accept even when the payload lost the verdict (a stale bulk save), exactly
    // once: one Changed link, and a second promote of the same update is refused and changes nothing.
    [Fact]
    public async Task Promote_HonoursAnAcceptOnce_EvenFromAnEmptyPayload()
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h);
        await s.BigUpdate().ResumeAsync(s.WallId);
        var suggestion = Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));
        await s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, RelocationDecision.Moved);

        await s.BigUpdate().PromoteAsync(s.WallId, Confirmation(), s.SessionId);
        await Assert.ThrowsAnyAsync<Exception>(() => s.BigUpdate().PromoteAsync(s.WallId, Confirmation(), s.SessionId));

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldM);
        Assert.Equal((s.NewM, HoldGenerationLinkKind.Changed), (link.NewHoldId, link.Kind));
        Assert.Equal(1, await db.HoldGenerationLinks.CountAsync(l => l.NewHoldId == s.NewM));
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Equal(3, (await db.Walls.SingleAsync(w => w.Id == s.WallId)).CurrentGeneration);
    }

    /// <summary>Same hue family as <see cref="RelocationScenario.Green"/>, a visibly different shade.</summary>
    private static HoldFingerprint LookAlike(double? widthMm = null) =>
        RelocationScenario.Green(widthMm) with { A = 100, B = 150 };

    private static (Hold Old, Hold New, Dictionary<Guid, HoldFingerprint> Fingerprints) Pair(
        HoldFingerprint oldFingerprint, HoldFingerprint newFingerprint)
    {
        var old = new Hold { X = 0.1, Y = 0.1, Radius = 0.02 };
        var appeared = new Hold { X = 0.9, Y = 0.9, Radius = 0.02 };
        return (old, appeared, new Dictionary<Guid, HoldFingerprint>
        {
            [old.Id] = oldFingerprint,
            [appeared.Id] = newFingerprint,
        });
    }

    private static BigUpdateConfirmation Confirmation(params CarryoverDecision[] carryover) =>
        new(carryover.ToList(), [], [], []);
}
