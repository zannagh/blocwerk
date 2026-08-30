using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Regression cover for the carryover "Review Moved" step: during a big-wall update the staged
/// panel and its detected holds are stamped one generation ahead of the live wall
/// (Generation == CurrentGeneration + 1). GetPanelHoldsAsync must read that staged generation
/// when includeStaged is true, otherwise the update UI gets an empty list and draws no overlay.
/// </summary>
public class PanelStagedHoldsTests
{
    [Fact]
    public async Task GetPanelHolds_IncludeStaged_ReturnsHoldsAtNextGeneration()
    {
        using var h = new WallTestHarness();
        const int generation = 3;
        await h.SeedWallAsync(holdCount: 0, generation: generation);

        var service = CreateService(h);

        var (livePanelId, stagedPanelId, liveHoldIds, stagedHoldIds) =
            await SeedPanelsAsync(h, generation);

        // The failing-then-fixed assertion: the staged view must surface the moved-to holds that
        // live at CurrentGeneration + 1. Pre-fix this always read CurrentGeneration and returned [].
        var staged = await service.GetPanelHoldsAsync(h.WallId, stagedPanelId, includeStaged: true);

        Assert.Equal(2, staged.Count);
        Assert.Equal(stagedHoldIds.OrderBy(x => x), staged.Select(p => p.Id).OrderBy(x => x));

        // The non-staged path is unchanged: a live panel yields the live-generation holds.
        var live = await service.GetPanelHoldsAsync(h.WallId, livePanelId, includeStaged: false);

        Assert.Equal(liveHoldIds.OrderBy(x => x), live.Select(p => p.Id).OrderBy(x => x));

        // A staged panel has no live photo, so the non-staged view still returns nothing.
        var stagedWithoutStaging =
            await service.GetPanelHoldsAsync(h.WallId, stagedPanelId, includeStaged: false);

        Assert.Empty(stagedWithoutStaging);
    }

    private static WallPanelService CreateService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

    private static async Task<(Guid LivePanelId, Guid StagedPanelId, List<Guid> LiveHoldIds, List<Guid> StagedHoldIds)>
        SeedPanelsAsync(WallTestHarness h, int generation)
    {
        await using var db = h.CreateContext();

        // Live center panel with one hold at the current generation.
        var livePanel = new WallPanel
        {
            WallId = h.WallId,
            Col = 0,
            Row = 0,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
            Generation = generation,
        };

        // Staged re-shot center panel: no live photo, staged photo present, one generation ahead.
        var stagedPanel = new WallPanel
        {
            WallId = h.WallId,
            Col = 0,
            Row = 0,
            Photo = null,
            StagedPhoto = [4, 5, 6],
            StagedPhotoContentType = "image/jpeg",
            Generation = generation + 1,
        };

        db.WallPanels.Add(livePanel);
        db.WallPanels.Add(stagedPanel);

        var liveHold = NewHold(h.WallId, livePanel.Id, generation, 0.2);
        db.Holds.Add(liveHold);

        var stagedHolds = new List<Hold>
        {
            NewHold(h.WallId, stagedPanel.Id, generation + 1, 0.4),
            NewHold(h.WallId, stagedPanel.Id, generation + 1, 0.6),
        };
        db.Holds.AddRange(stagedHolds);

        await db.SaveChangesAsync();

        return (
            livePanel.Id,
            stagedPanel.Id,
            [liveHold.Id],
            stagedHolds.Select(x => x.Id).ToList());
    }

    private static Hold NewHold(Guid wallId, Guid panelId, int generation, double position) =>
        new()
        {
            WallId = wallId,
            WallPanelId = panelId,
            X = position,
            Y = position,
            Radius = 0.02,
            Generation = generation,
        };
}
