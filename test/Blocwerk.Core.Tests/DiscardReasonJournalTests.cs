// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>ResumeAsync</c> writes each suggested discard's reason onto its staged hold. That used to be a set-based update the
/// journal never saw, so a replayed wall had no reasons; it is now a tracked write inside the wall's wall-update batch.
/// </summary>
public class DiscardReasonJournalTests
{
    [Fact]
    public async Task TheSuggestedDiscardReason_IsJournalledInTheWallUpdateBatch_AndNotWrittenAgainOnAnUnchangedResume()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true, neighbour: new LookNeighbour(TargetX, TargetY));
        var journal = new ChangeJournal(() => h.CreateContext());
        var factory = new JournallingDbContextFactory(h.DbContextFactory.ConnectionString, journal);
        var service = new WallBigUpdateService(
            factory, h.CurrentUser, h.HoldDetection, new SameFrameHoldMatcher(), NullLogger<WallBigUpdateService>.Instance,
            changeJournal: journal, presenceProbe: w.Probe);

        var session = await service.ResumeAsync(w.WallId);
        var detection = w.NeighbourDetectionId!.Value;
        Assert.Equal(NewHoldDiscardReason.SeenOnNeighbourPanel, session.SuggestedNewDiscards![detection]);

        await using (var db = h.CreateContext())
        {
            Assert.Equal(nameof(NewHoldDiscardReason.SeenOnNeighbourPanel), (await db.Holds.SingleAsync(x => x.Id == detection)).DiscardReason);
            var entry = await db.ChangeJournalEntries.SingleAsync(e => e.EntityType == nameof(Hold) && e.KeyJson.Contains(detection.ToString()));
            var batch = await db.ChangeJournalBatches.SingleAsync(b => b.Id == entry.BatchId);
            Assert.Equal(ChangeJournal.WallUpdateBatchLabel, batch.Label);
            Assert.Equal(w.WallId, batch.ScopeId);
            Assert.Contains("SeenOnNeighbourPanel", entry.AfterJson);
        }

        await service.ResumeAsync(w.WallId);
        await using (var db = h.CreateContext())
        {
            Assert.Equal(1, await db.ChangeJournalEntries.CountAsync(e => e.EntityType == nameof(Hold) && e.KeyJson.Contains(detection.ToString())));
        }
    }
}
