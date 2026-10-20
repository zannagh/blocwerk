// <copyright file="NewHoldPerBoulderScenarios.cs" company="Blocwerk">
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

/// <summary>The per-boulder scenarios, shared by the SQLite and the Postgres suites.</summary>
internal static class NewHoldPerBoulderScenarios
{
    /// <summary>The replaced hold of the fixture with a second boulder on it, marked by the update for the replacement alone too.</summary>
    public static async Task<(LookWall Wall, Guid Second, LookOverFindingView View)> TwoBouldersAsync(WallTestHarness h)
    {
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var finding = await FindingAsync(h, w.WallId);
        Guid second;
        await using (var db = h.CreateContext())
        {
            var first = await db.Boulders.AsNoTracking().SingleAsync(b => b.Id == w.BoulderId);
            var boulder = new Boulder { WallId = w.WallId, Name = "Warm-up", CreatedByUserId = h.Owner.Id, Generation = first.Generation, NeedsReview = true };
            db.Boulders.Add(boulder);
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = finding.HoldId!.Value, Type = HoldType.Top });
            var tracked = await db.WallLookOverFindings.SingleAsync(f => f.Id == finding.Id);
            tracked.MarkedBoulderIds = [w.BoulderId, boulder.Id];
            await db.SaveChangesAsync();
            second = boulder.Id;
        }

        var view = Assert.Single(await Service(h).ListAsync(w.WallId));
        Assert.Equal(2, view.Boulders.Count);
        return (w, second, view);
    }

    public static async Task AllYesAsync(WallTestHarness h)
    {
        var (w, second, view) = await TwoBouldersAsync(h);

        await Service(h).AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, [w.BoulderId, second], [second, w.BoulderId]);

        Assert.Null(await HoldAsync(h, view.HoldId));
        foreach (var id in new[] { w.BoulderId, second })
        {
            var boulder = await BoulderAsync(h, id);
            Assert.Equal((w.DetectionId, false, false), (boulder.BoulderHolds.Single().HoldId, boulder.IsHistoric, boulder.NeedsReview));
        }

        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal((LookOverAnswer?)LookOverAnswer.UseNewHold, finding.Answer);
        Assert.Equal(new[] { w.BoulderId, second }.Order(), finding.ClearedBoulderIds!.Order());
        await using var db = h.CreateContext();
        Assert.True(await db.ChangeJournalBatches.AnyAsync(b => b.Id == finding.MergeBatchId && b.Label == HoldDuplicateService.BatchLabel));
        Assert.Null(Assert.Single(await Service(h).ListAsync(w.WallId)).NewHoldBoulders);
    }

    public static async Task MixedAsync(WallTestHarness h)
    {
        var (w, second, view) = await TwoBouldersAsync(h);

        await Service(h).AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, [w.BoulderId, second], [w.BoulderId]);

        Assert.Null(await HoldAsync(h, view.HoldId));
        Assert.NotNull(await HoldAsync(h, w.DetectionId));
        var yes = await BoulderAsync(h, w.BoulderId);
        Assert.Equal((w.DetectionId, HoldType.Start, false, false), (yes.BoulderHolds.Single().HoldId, yes.BoulderHolds.Single().Type, yes.IsHistoric, yes.NeedsReview));
        var no = await BoulderAsync(h, second);
        Assert.Equal((true, 0), (no.IsHistoric, no.BoulderHolds.Count));

        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal((LookOverFindingStatus.Settled, (LookOverAnswer?)LookOverAnswer.UseNewHold, 2), (finding.Status, finding.Answer, finding.AnswerBoulders));
        Assert.Equal([w.BoulderId], finding.ClearedBoulderIds);
        await using var db = h.CreateContext();
        var batch = await db.ChangeJournalBatches.SingleAsync(b => b.Id == finding.MergeBatchId);
        Assert.Equal((HoldDuplicateService.BatchLabel, ChangeJournalScopeKind.Wall, (Guid?)w.WallId), (batch.Label, batch.ScopeKind, batch.ScopeId));
        var listed = Assert.Single(await Service(h).ListAsync(w.WallId));
        Assert.Equal((1, 1), (listed.NewHoldBoulders, listed.ClearedBoulders));
    }

    public static async Task AllNoAsync(WallTestHarness h)
    {
        var (w, second, view) = await TwoBouldersAsync(h);

        await Service(h).AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, [w.BoulderId, second], []);

        Assert.Null(await HoldAsync(h, view.HoldId));
        foreach (var id in new[] { w.BoulderId, second })
        {
            var boulder = await BoulderAsync(h, id);
            Assert.Equal((true, 0), (boulder.IsHistoric, boulder.BoulderHolds.Count));
        }

        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal(((LookOverAnswer?)LookOverAnswer.RemoveHold, (Guid?)null), (finding.Answer, finding.MergeBatchId));
    }

    /// <summary>Answers per boulder (<paramref name="mixed"/>: the second boulder yes, the first no; else both yes), then undoes it in the duplicate review.</summary>
    public static async Task UndoAsync(WallTestHarness h, bool mixed)
    {
        var (w, second, view) = await TwoBouldersAsync(h);
        var before = await SnapshotAsync(h, w.WallId);

        await Service(h).AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, [w.BoulderId, second], mixed ? [second] : null);
        var finding = await FindingAsync(h, w.WallId);
        Assert.NotEqual(before, await SnapshotAsync(h, w.WallId));

        var result = await Duplicates(h).RevertAsync(w.WallId, finding.MergeBatchId!.Value);

        Assert.True(result.Reverted, result.Error ?? string.Join("; ", result.Conflicts));
        Assert.Equal(before, await SnapshotAsync(h, w.WallId));

        // A reverted batch no longer counts towards what the answer did.
        Assert.Null(Assert.Single(await Service(h).ListAsync(w.WallId)).NewHoldBoulders);
    }

    /// <summary>The wall's holds, memberships and boulder flags, as one comparable string.</summary>
    private static async Task<string> SnapshotAsync(WallTestHarness h, Guid wallId)
    {
        await using var db = h.CreateContext();
        var holds = (await db.Holds.AsNoTracking().Where(x => x.WallId == wallId).Select(x => new { x.Id, x.NeedsReview, x.StackedOnHoldId }).ToListAsync())
            .OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.NeedsReview}:{x.StackedOnHoldId}");
        var uses = (await db.BoulderHolds.AsNoTracking().Where(x => x.Boulder.WallId == wallId).Select(x => new { x.BoulderId, x.HoldId, x.Type, x.Usage }).ToListAsync())
            .OrderBy(x => x.BoulderId).ThenBy(x => x.HoldId).Select(x => $"{x.BoulderId}>{x.HoldId}:{x.Type}:{x.Usage}");
        var boulders = (await db.Boulders.AsNoTracking().Where(x => x.WallId == wallId).Select(x => new { x.Id, x.NeedsReview, x.IsHistoric }).ToListAsync())
            .OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.NeedsReview}:{x.IsHistoric}");
        var lineage = (await db.HoldGenerationLinks.AsNoTracking().Select(x => new { x.OldHoldId, x.NewHoldId, x.Kind }).ToListAsync())
            .Select(x => $"{x.OldHoldId}>{x.NewHoldId}:{x.Kind}").Order();
        return string.Join("\n", holds.Concat(uses).Concat(boulders).Concat(lineage));
    }
}
