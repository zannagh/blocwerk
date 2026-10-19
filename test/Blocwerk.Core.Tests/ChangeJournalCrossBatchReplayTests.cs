// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Review finding C7 (2026-10-19), fixed: the exporter writes a package in the source's write order (each batch's Seq
/// order, merged across batches by time), the replayer evaluates every key in that package order, and the staged-hold
/// touch-ups are written into the wall's open wall-update batch. These were the verifier's pinning tests of the buggy
/// behaviour; each now asserts the correct one, against the real interceptor, promote, exporter and replayer.
/// </summary>
public sealed class ChangeJournalCrossBatchReplayTests : IDisposable
{
    private readonly ChangeJournalReplayFixture f = new();

    public void Dispose() => f.Dispose();

    [Fact]
    public async Task LegacyAdhocTouchup_ExportedWithTheWallUpdate_ReplaysToTheSourceState()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Adhoc);
        var package = await f.ExportAsync(await f.AllBatchIdsAsync());

        var dry = await f.ReplayAsync(s, package, dryRun: true);
        Assert.DoesNotContain(dry.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        await using (var untouched = s.TargetFactory!.CreateDbContext())
        {
            // The dry run wrote nothing: no staged hold, no replay batch.
            Assert.False(await untouched.Holds.AnyAsync(h => h.Generation == 1));
            Assert.False(await untouched.ChangeJournalBatches.AnyAsync(b => b.Label == "replay"));
        }

        var real = await f.ReplayAsync(s, package, dryRun: false);
        Assert.True(real.Applied);
        await AssertTargetMatchesSourceAsync(s);
    }

    [Fact]
    public async Task LegacyAdhocAddedTwin_ExportedWithTheWallUpdate_PromoteUpdateLandsOnTheAddedHold()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Adhoc, addedIsTwin: true);
        var package = await f.ExportAsync(await f.AllBatchIdsAsync());

        // The adhoc INSERT was written before the promote's UPDATE, and the package now says so.
        var added = package.Entries.Where(e => e.KeyJson.Contains(s.AddedId.ToString()) && e.EntityType == nameof(Hold)).ToList();
        Assert.Equal([ChangeJournalOp.Insert, ChangeJournalOp.Update], added.Select(e => e.Op));

        var real = await f.ReplayAsync(s, package, dryRun: false);

        Assert.True(real.Applied);
        Assert.DoesNotContain(real.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        await AssertTargetMatchesSourceAsync(s);
    }

    [Fact]
    public async Task ServiceTouchup_LandsInTheWallUpdateBatch_WhichAloneReplaysTheWholeUpdate()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Service);
        var wallUpdate = await f.WallUpdateBatchIdAsync(s.WallId);

        // No adhoc batch: every touch-up is in the wall-update batch.
        Assert.Equal([wallUpdate], await f.AllBatchIdsAsync());

        var report = await f.ReplayAsync(s, await f.ExportAsync([wallUpdate]), dryRun: false);

        Assert.True(report.Applied);
        Assert.DoesNotContain(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        await AssertTargetMatchesSourceAsync(s);
    }

    [Fact]
    public async Task ServiceTouchupAddedTwin_WallUpdateBatchOnly_ReplaysCleanly()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Service, addedIsTwin: true);

        var report = await f.ReplayAsync(s, await f.ExportAsync([await f.WallUpdateBatchIdAsync(s.WallId)]), dryRun: false);

        Assert.True(report.Applied);
        Assert.DoesNotContain(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        await AssertTargetMatchesSourceAsync(s);
    }

    [Fact]
    public async Task DefaultsOnly_WallUpdateBatchAlone_ReplaysCleanly()
    {
        var s = await f.RunUpdateAsync(TouchupMode.None);
        var package = await f.ExportAsync(await f.AllBatchIdsAsync());
        Assert.Single(package.Batches);

        var report = await f.ReplayAsync(s, package, dryRun: false);

        Assert.True(report.Applied);
        Assert.DoesNotContain(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
    }

    [Fact]
    public async Task DefaultsOnly_PlusALaterScopedBatchOnTheNewHolds_InOnePackage_ReplaysCleanly()
    {
        var s = await f.RunUpdateAsync(TouchupMode.None);
        await AnswerLookOverAsync(s);

        var package = await f.ExportAsync(await f.AllBatchIdsAsync());
        var report = await f.ReplayAsync(s, package, dryRun: false);

        // The look-over's Seq 0 update no longer sorts before the wall update's insert of the same hold.
        Assert.True(report.Applied);
        Assert.DoesNotContain(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        await using var dst = s.TargetFactory!.CreateDbContext();
        Assert.Equal("answered in look-over", (await dst.Holds.SingleAsync(h => h.Id == s.TwinId)).Name);
    }

    [Fact]
    public async Task DefaultsOnly_LaterBatchReplayedAsItsOwnPackageAfterTheUpdate_ReplaysCleanly()
    {
        var s = await f.RunUpdateAsync(TouchupMode.None);
        var wallUpdate = await f.WallUpdateBatchIdAsync(s.WallId);
        await AnswerLookOverAsync(s);
        var lookOver = (await f.AllBatchIdsAsync()).Single(id => id != wallUpdate);

        var first = await f.ReplayAsync(s, await f.ExportAsync([wallUpdate]), dryRun: false);
        Assert.True(first.Applied);
        var second = await f.ReplayAsync(s, await f.ExportAsync([lookOver]), dryRun: false);
        Assert.True(second.Applied);
        Assert.DoesNotContain(second.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);

        await using var dst = s.TargetFactory!.CreateDbContext();
        Assert.Equal("answered in look-over", (await dst.Holds.SingleAsync(h => h.Id == s.TwinId)).Name);
    }

    [Fact]
    public async Task ReplayingTheSamePackageTwice_IsAnIdempotentNoOp()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Service, addedIsTwin: true);
        var package = await f.ExportAsync([await f.WallUpdateBatchIdAsync(s.WallId)]);
        Assert.True((await f.ReplayAsync(s, package, dryRun: false)).Applied);

        var again = await f.ReplayAsync(s, package, dryRun: true);

        Assert.All(again.Rows, r => Assert.Equal(ChangeJournalReplayOutcome.SkippedAlreadyApplied, r.Outcome));
    }

    [Fact]
    public async Task ReportRows_NameTheirBatch()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Service);
        var wallUpdate = await f.WallUpdateBatchIdAsync(s.WallId);

        var report = await f.ReplayAsync(s, await f.ExportAsync([wallUpdate]), dryRun: true);

        Assert.All(report.Rows, r => Assert.Equal(wallUpdate, r.BatchId));
    }

    private async Task AnswerLookOverAsync(ChangeJournalReplayScenario s)
    {
        await Task.Delay(20);
        using (f.Journal.BeginBatch("look-over", ChangeJournalScopeKind.Wall, s.WallId))
        {
            await using var db = f.CreateContext();
            (await db.Holds.SingleAsync(h => h.Id == s.TwinId)).Name = "answered in look-over";
            await db.SaveChangesAsync();
        }
    }

    /// <summary>The wall's holds on the target equal the source's in every column the update and touch-up wrote.</summary>
    private async Task AssertTargetMatchesSourceAsync(ChangeJournalReplayScenario s)
    {
        await using var src = f.CreateContext();
        await using var dst = s.TargetFactory!.CreateDbContext();
        var expected = await HoldStateAsync(src, s.WallId);
        var actual = await HoldStateAsync(dst, s.WallId);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(actual, h => h.Id == s.DeletedId);
        Assert.Contains(actual, h => h.Id == s.AddedId);

        // The moved detection (kept unless the added hold took its place as the twin) sits where the touch-up put it.
        Assert.All(actual.Where(h => h.Id == s.TwinId), h => Assert.Equal(0.6, h.X));
    }

    private static async Task<List<(Guid Id, int Generation, double X, double Y, bool NeedsReview, bool IsAutoDetected, Guid? PanelId)>> HoldStateAsync(
        Microsoft.EntityFrameworkCore.DbContext db, Guid wallId)
    {
        var rows = await db.Set<Hold>().AsNoTracking().Where(h => h.WallId == wallId).ToListAsync();
        return rows
            .OrderBy(h => h.Id)
            .Select(h => (h.Id, h.Generation, h.X, h.Y, h.NeedsReview, h.IsAutoDetected, h.WallPanelId))
            .ToList();
    }
}
