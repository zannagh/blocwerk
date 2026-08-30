using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Regression cover for the wall display after a centre update: a centre re-shoot adds a NEW panel
/// row at the next generation and promotes it, but the superseded row keeps its Photo. GetPanelsAsync
/// must return only the latest-generation live panel per (Col,Row) position — otherwise the stale
/// panel (old image, no current-generation holds) can win and the wall shows no holds.
/// </summary>
public class PanelDedupTests
{
    [Fact]
    public async Task GetPanels_TwoLivePanelsSamePosition_ReturnsOnlyLatestGeneration()
    {
        using var h = new WallTestHarness();
        const int generation = 2;
        await h.SeedWallAsync(holdCount: 0, generation: generation);

        var (staleId, currentId, neighbourId) = await SeedPanelsAsync(h, generation);

        var service = new WallPanelService(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

        var panels = await service.GetPanelsAsync(h.WallId);

        // The superseded (0,0) panel must not appear; the current one wins its position.
        Assert.DoesNotContain(panels, p => p.Id == staleId);
        Assert.Contains(panels, p => p.Id == currentId);

        // A neighbour at another position is the latest for its cell, so it is preserved.
        Assert.Contains(panels, p => p.Id == neighbourId);

        // Exactly one panel per occupied position: (0,0) and (1,0).
        Assert.Equal(2, panels.Count);
    }

    [Fact]
    public async Task GetPanels_LivePanelWithStagedTwin_KeepsLiveCellDuringUpdate()
    {
        using var h = new WallTestHarness();
        const int generation = 2;
        await h.SeedWallAsync(holdCount: 0, generation: generation);

        Guid liveId;
        await using (var db = h.CreateContext())
        {
            // A cell mid-update: the live panel (gen N) plus its staged twin one generation ahead
            // (Photo == null). The staged row must NOT suppress the live one for the live viewers.
            var live = NewPanel(h.WallId, col: 0, row: 0, generation: generation);
            var staged = new WallPanel
            {
                WallId = h.WallId,
                Col = 0,
                Row = 0,
                Photo = null,
                StagedPhoto = [9, 9, 9],
                StagedPhotoContentType = "image/jpeg",
                Generation = generation + 1,
            };
            db.WallPanels.AddRange(live, staged);
            await db.SaveChangesAsync();
            liveId = live.Id;
        }

        var service = new WallPanelService(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

        var panels = await service.GetPanelsAsync(h.WallId);
        var cell = Assert.Single(panels);

        // The winning row for (0,0) is the LIVE one, so IsLive stays true and live viewers keep it.
        Assert.Equal(liveId, cell.Id);
        Assert.True(cell.IsLive);
    }

    private static async Task<(Guid StaleId, Guid CurrentId, Guid NeighbourId)> SeedPanelsAsync(
        WallTestHarness h, int generation)
    {
        await using var db = h.CreateContext();

        // Superseded centre panel: still has a live Photo (promote never cleared it), older generation.
        var stale = NewPanel(h.WallId, col: 0, row: 0, generation: generation - 1);

        // Current centre panel at (0,0), latest generation.
        var current = NewPanel(h.WallId, col: 0, row: 0, generation: generation);

        // A neighbour at (1,0) at the current generation — must survive the dedup.
        var neighbour = NewPanel(h.WallId, col: 1, row: 0, generation: generation);

        db.WallPanels.AddRange(stale, current, neighbour);
        await db.SaveChangesAsync();

        return (stale.Id, current.Id, neighbour.Id);
    }

    private static WallPanel NewPanel(Guid wallId, int col, int row, int generation) =>
        new()
        {
            WallId = wallId,
            Col = col,
            Row = row,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
            Generation = generation,
        };
}
