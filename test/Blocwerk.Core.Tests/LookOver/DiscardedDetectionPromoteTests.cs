// <copyright file="DiscardedDetectionPromoteTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.HoldColors;
using Blocwerk.Web.Components.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Enums.NewHoldDiscardReason;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// What a promote keeps of the detections its triage discarded: a finding each, with the outline, and no hold row.
/// They flag the wall only when one is likely a missed hold, go when the panel is photographed again, and for an
/// update applied before they were kept they come back from the change journal.
/// </summary>
public class DiscardedDetectionPromoteTests
{
    [Fact]
    public async Task DiscardedDetections_AreKeptAsFindings_AndAreNoHolds()
    {
        using var s = await DiscardedScenario.ApplyAsync(
            (HoldColorScenario.YellowSpot, 0.97, UnchangedSinceOldPhoto), (HoldColorScenario.RedSpot, 0.5, OnMarker), (HoldColorScenario.GreenSpot, 0.99, null));

        // Only the kept hold is a hold: nothing that reads the wall's holds sees a discarded detection.
        Assert.Equal([s.KeptId], (await s.HoldsAsync()).Select(h => h.Id));
        var findings = await s.FindingsAsync();
        Assert.Equal(
            [(0.97, "UnchangedSinceOldPhoto"), (0.5, "OnMarker")],
            findings.Select(f => (f.Confidence!.Value, f.DiscardReason!)));
        var kept = findings[0];
        Assert.Equal(
            (s.PanelId, 1, HoldColorScenario.YellowSpot.X, HoldColorScenario.YellowSpot.Y, 0.07, LookOverFindingStatus.Open, null),
            (kept.PanelId!.Value, kept.Generation, kept.X, kept.Y, kept.Radius, kept.Status, kept.HoldId));
        Assert.Equal(DiscardedScenario.Outline.Select(p => (p.Dx, p.Dy)), kept.ShapePoints!.Select(p => (p.Dx, p.Dy)));
    }

    [Fact]
    public async Task ASureDetectionOnNoHold_FlagsTheWall_AndIsTheOnlyOneThatCounts()
    {
        using var s = await DiscardedScenario.ApplyAsync(
            (HoldColorScenario.YellowSpot, 0.97, UnchangedSinceOldPhoto), (HoldColorScenario.RedSpot, 0.5, OnMarker), (HoldColorScenario.BlueSpot, 0.99, KnownHoldIn3D));
        var service = s.LookOver();

        var state = (await service.GetStateAsync(s.WallId))!;

        Assert.Equal((true, 1, 1, 1), (state.Pending, state.Generation, state.OpenFindings, state.TotalFindings));
        Assert.Equal(3, (await service.ListAsync(s.WallId)).Count);
        await Assert.ThrowsAsync<UserFacingException>(() => service.FinishAsync(s.WallId, leaveOpen: false));
    }

    [Fact]
    public async Task UnsureOrCoveredDetections_DoNotFlagTheWall_ButAreListed()
    {
        using var s = await DiscardedScenario.ApplyAsync((HoldColorScenario.RedSpot, 0.5, OnMarker), (HoldColorScenario.BlueSpot, 0.99, KnownHoldIn3D));
        var service = s.LookOver();

        var state = (await service.GetStateAsync(s.WallId))!;

        Assert.Equal((false, 0, 0), (state.Pending, state.OpenFindings, state.TotalFindings));
        var listed = await service.ListAsync(s.WallId);
        Assert.Equal([false, false], listed.Select(f => f.CountsAsOpen));
        Assert.Equal(1.0, listed.Single(f => f.ProbablyDuplicate).Overlap);
    }

    [Fact]
    public async Task TheNextUpdateOfThePanel_RemovesItsDiscardedDetections_AnsweredOrNot()
    {
        using var s = await DiscardedScenario.ApplyAsync((HoldColorScenario.YellowSpot, 0.97, UnchangedSinceOldPhoto), (HoldColorScenario.RedSpot, 0.95, OnMarker));
        var service = s.LookOver();
        var first = (await service.ListAsync(s.WallId))[0];
        await service.AnswerAsync(s.WallId, first.Id, LookOverAnswer.Settle);

        var panel = await s.Colors.AddPanelAsync(0, generation: 2, staged: true);
        var again = await s.Colors.AddHoldAsync(panel, HoldColorScenario.BlueSpot, generation: 2);
        var dropped = await s.Colors.AddHoldAsync(panel, HoldColorScenario.GreenSpot, generation: 2, h => h.DiscardReason = nameof(OutsideOldPhoto));
        await s.Colors.BigUpdate().PromoteAsync(s.WallId, new BigUpdateConfirmation([], [again], [dropped], []));

        var left = Assert.Single(await s.FindingsAsync());
        Assert.Equal((2, panel, "OutsideOldPhoto"), (left.Generation, left.PanelId!.Value, left.DiscardReason));
    }

    [Fact]
    public async Task AnUpdateAppliedBefore_GetsItsDeletedDetectionsBackFromTheJournal_Once()
    {
        using var s = await DiscardedScenario.ApplyAsync();
        await using (var db = s.Harness.CreateContext())
        {
            var batch = new ChangeJournalBatch { Label = ChangeJournal.WallUpdateBatchLabel, ScopeKind = ChangeJournalScopeKind.Wall, ScopeId = s.WallId };
            db.ChangeJournalBatches.Add(batch);
            db.ChangeJournalEntries.AddRange(
                Deleted(batch.Id, 1, s, HoldColorScenario.YellowSpot, generation: 1, withReasonField: false),
                Deleted(batch.Id, 2, s, HoldColorScenario.RedSpot, generation: 0, withReasonField: false),
                Deleted(batch.Id, 3, s, HoldColorScenario.GreenSpot, generation: 1, withReasonField: true));
            await db.SaveChangesAsync();
        }

        await DiscardedDetectionBackfill.RunIfNeededAsync(s.Harness.DbContextFactory, NullLogger.Instance);
        await DiscardedDetectionBackfill.RunIfNeededAsync(s.Harness.DbContextFactory, NullLogger.Instance);

        // Only the detection of the current generation whose row predates the recorded reason; with its outline.
        var back = Assert.Single(await s.FindingsAsync());
        Assert.Equal(
            (HoldColorScenario.YellowSpot.X, s.PanelId, 1, (double?)0.98, null),
            (back.X, back.PanelId!.Value, back.Generation, back.Confidence, back.DiscardReason));
        Assert.Equal(DiscardedScenario.Outline.Count, back.ShapePoints!.Count);
        Assert.NotNull((await LookOverFixture.WallAsync(s.Harness, s.WallId)).LookOverPendingSince);
    }

    [Fact]
    public async Task ACarriedHoldWithoutAColour_GetsASuggestionAtThePromote_AndASetColourStays()
    {
        using var s = await HoldColorScenario.CreateAsync(oldHolds: 2);
        List<Guid> old;
        await using (var db = s.Harness.CreateContext())
        {
            old = await db.Holds.Where(h => h.WallId == s.WallId && h.Generation == 0).OrderBy(h => h.X).Select(h => h.Id).ToListAsync();
        }

        await s.SetAsync(old[0], h => h.Color = "pink");
        var panel = await s.AddPanelAsync(0, generation: 1, staged: true);
        var twinOfSet = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot, generation: 1);
        var twinOfUnset = await s.AddHoldAsync(panel, HoldColorScenario.RedSpot, generation: 1);
        var confirmation = new BigUpdateConfirmation(
            [new(old[0], CarryKind.Carried, twinOfSet), new(old[1], CarryKind.Carried, twinOfUnset)], [], [], []);

        await s.BigUpdate(new HoldColorProposer()).PromoteAsync(s.WallId, confirmation);

        var (set, unset) = (await s.GetAsync(twinOfSet), await s.GetAsync(twinOfUnset));
        Assert.Equal(("pink", false), (set.Color, set.HasProposedColor));
        Assert.Equal(("red", true), (unset.Color, unset.HasProposedColor));

        // The wall editor marks exactly the hold whose colour is only suggested.
        Assert.Equal((false, true), (SuggestedColorBadge.Shows(set), SuggestedColorBadge.Shows(unset)));
        unset.ProposedColor = null;
        Assert.False(SuggestedColorBadge.Shows(unset));
    }

    private static ChangeJournalEntry Deleted(Guid batchId, int seq, DiscardedScenario s, (double X, double Y) spot, int generation, bool withReasonField)
    {
        var row = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(), ["WallId"] = s.WallId, ["WallPanelId"] = s.PanelId, ["Generation"] = generation, ["X"] = spot.X, ["Y"] = spot.Y,
            ["Radius"] = 0.04, ["Confidence"] = 0.98, ["IsAutoDetected"] = true, ["IsVirtual"] = false, ["ShapeHoles"] = null,
            ["ShapePoints"] = DiscardedScenario.Outline,
        };
        if (withReasonField)
        {
            row["DiscardReason"] = null;
        }

        return new ChangeJournalEntry
        {
            BatchId = batchId, Seq = seq, EntityType = nameof(Hold), Op = ChangeJournalOp.Delete, KeyJson = "{}", BeforeJson = JsonSerializer.Serialize(row),
        };
    }
}
