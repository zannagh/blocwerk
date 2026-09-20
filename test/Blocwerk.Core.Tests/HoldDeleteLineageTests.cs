using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for deleting a hold that cross-generation lineage points at. Both ends of
/// <see cref="HoldGenerationLink"/> are Restrict FKs, so a delete that ignores them takes the whole
/// SaveChanges (the change journal included) down with it — which is exactly what happened in
/// production on every generation-updated wall. The lineage row is now TOMBSTONED instead: the dying
/// end is nulled and the row survives, so the hold on the other side still records where it came
/// from. A row that loses BOTH ends is removed — it can no longer be reached from any hold.
/// </summary>
public class HoldDeleteLineageTests
{
    private static readonly DateTimeOffset LinkCreatedAt = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public async Task DeleteHold_WithLineageOnBothEnds_Succeeds_AndTombstonesTheDyingEnd()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);
        var (predecessor, victim, successor) = (holds[0], holds[1], holds[2]);
        await AddLineageAsync(h, predecessor.Id, victim.Id, HoldGenerationLinkKind.Same, 0, 1);
        await AddLineageAsync(h, victim.Id, successor.Id, HoldGenerationLinkKind.Changed, 1, 2);

        await h.WallService.DeleteHoldAsync(victim.Id);

        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.Id == victim.Id));

        var incoming = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == predecessor.Id);
        Assert.Null(incoming.NewHoldId);

        var outgoing = await db.HoldGenerationLinks.SingleAsync(l => l.NewHoldId == successor.Id);
        Assert.Null(outgoing.OldHoldId);
    }

    [Fact]
    public async Task TombstonedLineageRow_KeepsGenerationsKindAndCreatedAt()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        await AddLineageAsync(h, holds[0].Id, holds[1].Id, HoldGenerationLinkKind.Changed, 3, 4);

        await h.WallService.DeleteHoldAsync(holds[1].Id);

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync();
        Assert.Equal(holds[0].Id, link.OldHoldId);
        Assert.Null(link.NewHoldId);
        Assert.Equal(HoldGenerationLinkKind.Changed, link.Kind);
        Assert.Equal(3, link.FromGeneration);
        Assert.Equal(4, link.ToGeneration);
        Assert.Equal(LinkCreatedAt, link.CreatedAt);
        Assert.Equal(h.WallId, link.WallId);
    }

    [Fact]
    public async Task DeleteHold_WithLineage_StillFlagsLinkedBoulderHistoric()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        await AddLineageAsync(h, holds[0].Id, holds[1].Id, HoldGenerationLinkKind.Same, 0, 1);
        var boulderId = await AttachBoulderAsync(h, holds[1].Id);

        await h.WallService.DeleteHoldAsync(holds[1].Id);

        await using var db = h.CreateContext();
        var boulder = await db.Boulders.SingleAsync(b => b.Id == boulderId);
        Assert.True(boulder.IsHistoric);
        Assert.False(await db.BoulderHolds.AnyAsync(bh => bh.BoulderId == boulderId));
    }

    [Fact]
    public async Task DeletingBothEnds_RemovesTheLineageRow()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        await AddLineageAsync(h, holds[0].Id, holds[1].Id, HoldGenerationLinkKind.Same, 0, 1);

        await h.WallService.DeleteHoldAsync(holds[1].Id);
        await h.WallService.DeleteHoldAsync(holds[0].Id);

        await using var db = h.CreateContext();
        Assert.Empty(await db.HoldGenerationLinks.ToListAsync());
    }

    [Fact]
    public async Task Promote_RemovingANeighbourHoldThatHasLineage_Succeeds_AndTombstonesIt()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var centre = await SeedStagedCentreAsync(h);
        var neighbour = await SeedStagedNeighbourAsync(h);
        // The neighbour hold already carries lineage from an earlier generation update — the state that
        // made a wall's second promote throw.
        await AddLineageAsync(h, old.Id, neighbour.HoldId, HoldGenerationLinkKind.Same, 0, 1);

        var service = new WallBigUpdateService(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance);

        await service.PromoteAsync(
            h.WallId,
            new BigUpdateConfirmation(
                [new CarryoverDecision(old.Id, CarryKind.Carried, centre)],
                [],
                [],
                [new NeighbourLinkSet(neighbour.PanelId, [], [neighbour.HoldId])]));

        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.Id == neighbour.HoldId));
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == old.Id && l.NewHoldId == null);
        Assert.Equal(HoldGenerationLinkKind.Same, link.Kind);
    }

    private static async Task AddLineageAsync(
        WallTestHarness h, Guid oldHoldId, Guid newHoldId, HoldGenerationLinkKind kind, int from, int to)
    {
        await using var db = h.CreateContext();
        db.HoldGenerationLinks.Add(new HoldGenerationLink
        {
            WallId = h.WallId,
            OldHoldId = oldHoldId,
            NewHoldId = newHoldId,
            Kind = kind,
            FromGeneration = from,
            ToGeneration = to,
            CreatedAt = LinkCreatedAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AttachBoulderAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder
        {
            WallId = h.WallId,
            Name = "Active",
            CreatedByUserId = h.Owner.Id,
            Generation = 0,
        };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId });
        await db.SaveChangesAsync();
        return boulder.Id;
    }

    private static async Task<Guid> SeedStagedCentreAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel
        {
            WallId = h.WallId,
            Col = 0,
            Row = 0,
            StagedPhoto = [1, 2, 3],
            StagedPhotoContentType = "image/jpeg",
            Generation = 1,
        };
        db.WallPanels.Add(panel);

        var staged = new Hold
        {
            WallId = h.WallId,
            WallPanelId = panel.Id,
            X = 0.5,
            Y = 0.5,
            Radius = 0.02,
            Generation = 1,
            IsAutoDetected = true,
        };
        db.Holds.Add(staged);
        await db.SaveChangesAsync();
        return staged.Id;
    }

    private static async Task<(Guid PanelId, Guid HoldId)> SeedStagedNeighbourAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel
        {
            WallId = h.WallId,
            Col = 1,
            Row = 0,
            StagedPhoto = [4, 5, 6],
            StagedPhotoContentType = "image/jpeg",
            Generation = 1,
        };
        db.WallPanels.Add(panel);

        var hold = new Hold
        {
            WallId = h.WallId,
            WallPanelId = panel.Id,
            X = 0.2,
            Y = 0.2,
            Radius = 0.02,
            Generation = 1,
            IsAutoDetected = true,
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return (panel.Id, hold.Id);
    }
}
