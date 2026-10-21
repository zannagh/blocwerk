using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="IBoulderService.GetRevisePrefillAsync"/> through a real provider: a frozen boulder on a two-panel
/// wall, with a gen-6 neighbour sitting exactly on its old coordinates on the OTHER panel (the prod trap), a
/// two-hop chain and a deleted successor.
/// </summary>
public class RevisePrefillServiceTests
{
    [Fact]
    public Task FrozenBoulder_PreSelectsItsLineage_OnSqlite() => RunAsync(null);

    [PostgresFact]
    public async Task FrozenBoulder_PreSelectsItsLineage_OnPostgres()
    {
        using var pg = PostgresTestDatabase.Create();
        await RunAsync(pg);
    }

    [Fact]
    public async Task UnreadableBoulder_IsNull()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 6);

        Assert.Null(await h.BoulderService.GetRevisePrefillAsync(Guid.NewGuid(), [], []));
    }

    [Fact]
    public async Task FrozenBoulderOnAHoldSplitAfterTheUpdate_ReportsTheSplit()
    {
        // The split tool keeps the old hold's lineage on the larger part and gives the new part none, so only the
        // larger part is ticked; the journal says it was split, which the editor turns into "check both parts".
        using var h = new WallTestHarness();
        var s = await HoldDuplicateScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.30, 0.30, 0.04, auto: true, shape: HoldToolSeed.Octagon(0.04));
        var old = await HoldToolSeed.OldHoldAsync(h, 0.30, 0.30);
        await HoldToolSeed.LineageAsync(h, old, hold);
        var boulderId = await s.AddBoulderAsync("Frozen", (old, HoldType.Start));
        var split = await s.MergeSplit().SplitAsync(h.WallId, hold, [new(0.31, 0.20), new(0.31, 0.40)], []);

        var live = (await s.HoldsAsync()).Values.Where(x => x.Generation == 1).ToList();
        var panel = new WallPanelInfo(s.PanelId, 0, 0, IsLive: true, HasStaged: false, 1, IsOutdated: false);
        var result = await h.BoulderService.GetRevisePrefillAsync(boulderId, live, [panel]);

        Assert.NotNull(result);
        Assert.Equal(hold, Assert.Single(result!.Picks).HoldId);
        Assert.Contains(live, x => x.Id == split.OtherHoldId);
        Assert.Equal(hold, Assert.Single(result.SplitHoldIds));
    }

    private static async Task RunAsync(PostgresTestDatabase? pg)
    {
        using var h = new WallTestHarness(pg);
        await h.SeedWallAsync(holdCount: 0, generation: 6);
        await using var db = h.CreateContext();

        var oldCentre = Panel(h, 0, 0, 5);
        var oldRight = Panel(h, 1, 0, 5);
        var liveCentre = Panel(h, 0, 0, 6);
        var liveRight = Panel(h, 1, 0, 6);
        db.WallPanels.AddRange(oldCentre, oldRight, liveCentre, liveRight);

        var g4 = Hold(h, oldCentre.Id, 0.30, 0.55, 4);
        var g5Mid = Hold(h, oldCentre.Id, 0.30, 0.55, 5);
        var g5Start = Hold(h, oldCentre.Id, 0.40, 0.60, 5);
        var g5Gone = Hold(h, oldCentre.Id, 0.70, 0.30, 5);
        var successorStart = Hold(h, liveCentre.Id, 0.42, 0.58, 6);
        var successorChain = Hold(h, liveCentre.Id, 0.31, 0.53, 6);
        var trapOnRight = Hold(h, liveRight.Id, 0.40, 0.60, 6);
        db.Holds.AddRange(g4, g5Mid, g5Start, g5Gone, successorStart, successorChain, trapOnRight);
        db.HoldGenerationLinks.AddRange(
            Link(h, g4.Id, g5Mid.Id, 4, 5),
            Link(h, g5Mid.Id, successorChain.Id, 5, 6),
            Link(h, g5Start.Id, successorStart.Id, 5, 6),
            Link(h, g5Gone.Id, null, 5, 6));

        var boulder = new Boulder { WallId = h.WallId, Name = "Frozen", CreatedByUserId = h.Owner.Id, Generation = 5, IsHistoric = true };
        db.Boulders.Add(boulder);
        db.BoulderHolds.AddRange(
            new BoulderHold { BoulderId = boulder.Id, HoldId = g5Start.Id, Type = HoldType.Start },
            new BoulderHold { BoulderId = boulder.Id, HoldId = g4.Id, Type = HoldType.Top, Usage = HoldUsage.HandOnly },
            new BoulderHold { BoulderId = boulder.Id, HoldId = g5Gone.Id });
        await db.SaveChangesAsync();

        var livePanels = new[] { Info(liveCentre), Info(liveRight) };
        var result = await h.BoulderService.GetRevisePrefillAsync(boulder.Id, [successorStart, successorChain, trapOnRight], livePanels);

        Assert.NotNull(result);
        Assert.Equal(
            new[] { (successorChain.Id, HoldType.Top, HoldUsage.HandOnly), (successorStart.Id, HoldType.Start, HoldUsage.HandAndFoot) },
            result!.Picks.Select(p => (p.HoldId, p.Type, p.Usage)));
        Assert.Equal(g5Gone.Id, Assert.Single(result.UnmappedHoldIds));
        Assert.Equal(liveCentre.Id, result.StartPanelId);
    }

    private static WallPanel Panel(WallTestHarness h, int col, int row, int generation) =>
        new() { WallId = h.WallId, Col = col, Row = row, Generation = generation, Photo = [1], PhotoContentType = "image/jpeg" };

    private static WallPanelInfo Info(WallPanel p) => new(p.Id, p.Col, p.Row, IsLive: true, HasStaged: false, p.Generation, IsOutdated: false);

    private static Hold Hold(WallTestHarness h, Guid panel, double x, double y, int generation) =>
        new() { WallId = h.WallId, WallPanelId = panel, X = x, Y = y, Generation = generation };

    private static HoldGenerationLink Link(WallTestHarness h, Guid old, Guid? successor, int from, int to) =>
        new() { WallId = h.WallId, OldHoldId = old, NewHoldId = successor, FromGeneration = from, ToGeneration = to };
}
