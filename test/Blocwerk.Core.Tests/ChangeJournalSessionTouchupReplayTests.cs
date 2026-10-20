// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The session-scoped staged edits (the update's UI and API) write inside the wall's open wall-update journal batch, like
/// the plain staged edits do: in particular the delete that releases a matched twin and removes the hold in one save,
/// which no longer goes through <c>DeleteStagedHoldAsync</c>. The wall-update batch alone then replays the whole update.
/// </summary>
public sealed class ChangeJournalSessionTouchupReplayTests : IDisposable
{
    private readonly ChangeJournalReplayFixture f = new();

    public void Dispose() => f.Dispose();

    [Fact]
    public async Task SessionScopedDeleteWithTwinRelease_LandsInTheWallUpdateBatch_AndReplaysOntoAPristineCopy()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Session);
        var wallUpdate = await f.WallUpdateBatchIdAsync(s.WallId);

        // The delete took the twin off the old hold's verdict, and no write of the touch-up opened a batch of its own.
        Assert.Equal(1, s.ReleasedTwins);
        Assert.Equal([wallUpdate], await f.AllBatchIdsAsync());
        await using (var src = f.CreateContext())
        {
            var deletes = await src.ChangeJournalEntries
                .Where(e => e.EntityType == nameof(Hold) && e.Op == ChangeJournalOp.Delete && e.KeyJson.Contains(s.DeletedId.ToString()))
                .ToListAsync();
            Assert.Equal(wallUpdate, Assert.Single(deletes).BatchId);
            Assert.False(await src.ChangeJournalEntries.AnyAsync(e => e.EntityType == nameof(WallUpdateSession)));
        }

        var report = await f.ReplayAsync(s, await f.ExportAsync([wallUpdate]), dryRun: false);

        Assert.True(report.Applied);
        Assert.DoesNotContain(report.Rows, r => r.Outcome == ChangeJournalReplayOutcome.Conflict);
        await using var source = f.CreateContext();
        await using var target = s.TargetFactory!.CreateDbContext();
        var expected = await HoldStateAsync(source, s.WallId);
        var actual = await HoldStateAsync(target, s.WallId);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain(actual, h => h.Id == s.DeletedId);
        Assert.Contains(actual, h => h.Id == s.AddedId);
        Assert.Equal(0.6, actual.Single(h => h.Id == s.TwinId).X);
    }

    [Fact]
    public async Task ARefusedSessionScopedDelete_FromAStaleTab_WritesNothing()
    {
        var s = await f.RunUpdateAsync(TouchupMode.Session);
        await using var before = f.CreateContext();
        var entries = await before.ChangeJournalEntries.CountAsync();

        // The guard runs before any batch opens: a delete naming another session is refused and journals nothing.
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() =>
            f.PanelService().DeleteStagedHoldInSessionAsync(s.WallId, Guid.NewGuid(), s.AddedId, StagedTwinDeletion.ReleaseTwin));

        await using var after = f.CreateContext();
        Assert.Equal(entries, await after.ChangeJournalEntries.CountAsync());
    }

    private static async Task<List<(Guid Id, int Generation, double X, double Y, bool NeedsReview, bool IsAutoDetected, Guid? PanelId)>> HoldStateAsync(
        DbContext db, Guid wallId)
    {
        var rows = await db.Set<Hold>().AsNoTracking().Where(h => h.WallId == wallId).ToListAsync();
        return rows
            .OrderBy(h => h.Id)
            .Select(h => (h.Id, h.Generation, h.X, h.Y, h.NeedsReview, h.IsAutoDetected, h.WallPanelId))
            .ToList();
    }
}
