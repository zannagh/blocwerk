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

    /// <summary>
    /// The generation-aware overload is what lets a historic boulder be drawn on the photos it was
    /// actually set on: for generation N it must resolve each cell to the newest COMMITTED row at or
    /// below N — the old image — and only for the current generation return today's rows.
    /// </summary>
    [Fact]
    public async Task GetPanelsAtGeneration_ResolvesEachCellToThatGenerationsPanel()
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

        // Generation N-1: (0,0) resolves to the SUPERSEDED row, never the current one. The neighbour
        // at (1,0) only exists at the current generation, so it has no row at or below N-1.
        var then = await service.GetPanelsAsync(h.WallId, generation - 1);
        var thenCell = Assert.Single(then);
        Assert.Equal(staleId, thenCell.Id);
        Assert.Equal(generation - 1, thenCell.Generation);
        Assert.True(thenCell.IsLive);

        // Generation N: today's wall — the current centre row plus its neighbour.
        var now = await service.GetPanelsAsync(h.WallId, generation);
        Assert.Equal(2, now.Count);
        Assert.Contains(now, p => p.Id == currentId);
        Assert.Contains(now, p => p.Id == neighbourId);
        Assert.DoesNotContain(now, p => p.Id == staleId);
    }

    /// <summary>
    /// An in-flight update belongs to no past generation: a staged row (no committed photo) must
    /// never stand in for a historic view, or the viewer would be shown an unpromoted capture.
    /// Seeded the way production stages — same cell as the live row, at CurrentGeneration + 1 (see
    /// WallBigUpdateService.StageAndDetectAsync) — and queried AT that staged generation, so the
    /// exclusion has to come from the committed-photo filter and not merely from the generation
    /// ceiling.
    /// </summary>
    [Fact]
    public async Task GetPanelsAtGeneration_IgnoresStagedRows()
    {
        using var h = new WallTestHarness();
        const int generation = 1;
        await h.SeedWallAsync(holdCount: 0, generation: generation);

        Guid liveId;
        await using (var db = h.CreateContext())
        {
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

        // At the staged generation the staged row is in range and would win (0,0) on generation
        // alone; only the committed-photo filter keeps the live row.
        var atStagedGen = await service.GetPanelsAsync(h.WallId, generation + 1);
        Assert.Equal(liveId, Assert.Single(atStagedGen).Id);

        var panels = await service.GetPanelsAsync(h.WallId, generation);
        var only = Assert.Single(panels);
        Assert.Equal(liveId, only.Id);
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
