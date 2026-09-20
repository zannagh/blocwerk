using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// That an INTERRUPTED update promotes to the same live wall an uninterrupted one does, and that a
/// failed bulk carryover save never quietly softens the verdicts already recorded.
/// </summary>
public class WallUpdateSessionDurabilityTests
{
    [Fact]
    public async Task AFailedBulkCarrySave_LeavesTheRecordedRemovedAndChangedVerdictsIntact()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var (removedOld, changedOld) = (holds[0], holds[1]);
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var centreId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var twin = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1, 0.4, 0.4);
        var sessions = WallUpdateSessionFixture.Sessions(h);

        // As the user decides, each verdict is written through one at a time.
        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(removedOld.Id, CarryKind.Removed, null));
        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(changedOld.Id, CarryKind.Changed, twin));

        // Leaving the step rewrites the whole carryover half in one go — and that save fails (here:
        // two verdicts for one hold, which the upsert key rejects).
        await Assert.ThrowsAsync<DbUpdateException>(() => sessions.SaveCarryOutcomeAsync(
            h.WallId,
            [
                new CarryoverDecision(removedOld.Id, CarryKind.Removed, null),
                new CarryoverDecision(removedOld.Id, CarryKind.Carried, null),
            ],
            [],
            []));

        // The failed rewrite rolled back whole: nothing was deleted and nothing was softened.
        var read = await sessions.GetDecisionsAsync(h.WallId);
        Assert.Equal(CarryKind.Removed, read.Carryover.Single(d => d.OldHoldId == removedOld.Id).Kind);
        Assert.Equal(CarryKind.Changed, read.Carryover.Single(d => d.OldHoldId == changedOld.Id).Kind);

        // And promoting what the session holds honours them: Removed gets no successor and freezes its
        // boulder, Changed advances as Changed. Default-carry would have resurrected and un-frozen both.
        var boulderId = await AttachBoulderAsync(h, removedOld.Id);
        await WallUpdateSessionFixture.BigUpdate(h).PromoteAsync(h.WallId, read);

        await using var db = h.CreateContext();
        Assert.False(await db.HoldGenerationLinks.AnyAsync(l => l.OldHoldId == removedOld.Id));
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == boulderId)).IsHistoric);
        Assert.Equal(
            HoldGenerationLinkKind.Changed,
            (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == changedOld.Id)).Kind);
    }

    [Fact]
    public async Task SavingTheCarryOutcomeTwice_RewritesTheSameRowsRatherThanDuplicatingThem()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var centreId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var twin = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1, 0.4, 0.4);
        var kept = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1, 0.6, 0.6);
        var sessions = WallUpdateSessionFixture.Sessions(h);

        // Stepping back into the carryover and continuing again replays the same bulk save. The delete
        // and the re-insert of the same (SessionId, Kind, HoldId) land in ONE SaveChanges, so the
        // unique upsert key is momentarily claimed twice — this is the test that it self-heals.
        for (var i = 0; i < 2; i++)
        {
            await sessions.SaveCarryOutcomeAsync(
                h.WallId, [new CarryoverDecision(old.Id, CarryKind.Changed, twin)], [kept], []);
        }

        var read = await sessions.GetDecisionsAsync(h.WallId);
        Assert.Equal(CarryKind.Changed, Assert.Single(read.Carryover).Kind);
        Assert.Equal(kept, Assert.Single(read.AcceptedNewCenterHoldIds));

        await using var db = h.CreateContext();
        Assert.Equal(2, await db.WallUpdateHoldDecisions.CountAsync());
    }

    [Fact]
    public async Task AResumedPromote_ProducesTheSameLiveWallAsAnUninterruptedOne()
    {
        // Two identical walls. One promotes straight through with the confirmation in memory; the other
        // writes every decision to its session, throws the in-memory state away, reads the session back
        // and promotes THAT. Requirement (a): the two live walls must be indistinguishable.
        var direct = await RunAsync(resumed: false);
        var resumed = await RunAsync(resumed: true);

        Assert.Equal(direct, resumed);
        Assert.NotEmpty(direct);
    }

    /// <summary>
    /// Stages one centre + one neighbour, decides the same things either in memory or through the
    /// session, promotes, and returns a comparable description of the resulting live wall.
    /// </summary>
    private static async Task<List<string>> RunAsync(bool resumed)
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentreAndNeighbour());
        var centreId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var neighbourId = await WallUpdateSessionFixture.PanelIdAsync(h, 1, 0);
        var twin = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1, 0.4, 0.4);
        var brandNew = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1, 0.6, 0.6);
        var dropped = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1, 0.7, 0.7);
        var neighbourHold = await WallUpdateSessionFixture.AddStagedHoldAsync(h, neighbourId, 1, 0.2, 0.2);

        var carryover = new List<CarryoverDecision>
        {
            new(holds[0].Id, CarryKind.Changed, twin),
            new(holds[1].Id, CarryKind.Removed, null),
            new(holds[2].Id, CarryKind.Carried, null),
        };
        var links = new NeighbourLinkSet(
            neighbourId, [new ConfirmedLink(twin, neighbourHold, Moved: true)], []);
        var confirmation = new BigUpdateConfirmation(carryover, [brandNew], [dropped], [links]);

        if (resumed)
        {
            var sessions = WallUpdateSessionFixture.Sessions(h);
            await sessions.SaveCarryOutcomeAsync(h.WallId, carryover, [brandNew], [dropped]);
            await sessions.SaveNeighbourLinkSetAsync(h.WallId, links);

            // The circuit is gone; the session is all there is. (The warp dictionaries are matcher
            // output, deliberately not persisted, and null on both sides of this comparison.)
            confirmation = await sessions.GetDecisionsAsync(h.WallId);
        }

        await WallUpdateSessionFixture.BigUpdate(h).PromoteAsync(h.WallId, confirmation);
        return await DescribeLiveWallAsync(h);
    }

    /// <summary>
    /// Everything about the promoted wall that a user would notice, as sorted, id-free text so two
    /// independently seeded walls can be compared directly.
    /// </summary>
    private static async Task<List<string>> DescribeLiveWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        var lines = new List<string> { $"gen={wall.CurrentGeneration}" };

        var holdById = await db.Holds.Where(x => x.WallId == h.WallId).ToDictionaryAsync(x => x.Id);
        foreach (var hold in holdById.Values)
        {
            lines.Add($"hold gen={hold.Generation} x={hold.X:F4} y={hold.Y:F4} r={hold.Radius:F4} "
                + $"panel={PanelKey(hold.WallPanelId)} review={hold.NeedsReview}");
        }

        foreach (var link in await db.HoldGenerationLinks.Where(l => l.WallId == h.WallId).ToListAsync())
        {
            lines.Add($"lineage {link.Kind} {link.FromGeneration}->{link.ToGeneration} "
                + $"{Where(link.OldHoldId)} => {Where(link.NewHoldId)}");
        }

        foreach (var link in await db.HoldLinks.Where(l => l.WallId == h.WallId).ToListAsync())
        {
            lines.Add($"holdlink {link.Kind} {Where(link.HoldAId)} => {Where(link.HoldBId)}");
        }

        foreach (var panel in await db.WallPanels.Where(p => p.WallId == h.WallId).ToListAsync())
        {
            lines.Add($"panel ({panel.Col},{panel.Row}) gen={panel.Generation} "
                + $"live={panel.Photo is not null} staged={panel.StagedPhoto is not null}");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;

        // A hold is identified by WHERE it is, never by its id: the two runs have different guids.
        string Where(Guid? id) => id is { } key && holdById.TryGetValue(key, out var x)
            ? $"[gen{x.Generation} {x.X:F4},{x.Y:F4}]"
            : "[gone]";

        string PanelKey(Guid? panelId) => panelId is { } p
            ? db.WallPanels.Where(w => w.Id == p).Select(w => $"({w.Col},{w.Row})").Single()
            : "none";
    }

    private static async Task<Guid> AttachBoulderAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder
        {
            WallId = h.WallId,
            Name = "B",
            CreatedByUserId = h.Owner.Id,
            Generation = 0,
        };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId });
        await db.SaveChangesAsync();
        return boulder.Id;
    }
}
