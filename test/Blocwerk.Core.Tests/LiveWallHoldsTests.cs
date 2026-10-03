using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="LiveWallHolds"/>: the live panels are the newest panel with a photo per grid cell, spanning
/// generations after a subset promote; superseded panels, their historic holds and staged rows never count.
/// The placement run uses it, so superseded panel photos are never registered or placed.
/// </summary>
public class LiveWallHoldsTests
{
    [Fact]
    public async Task LiveHolds_AreTheNewestPanelPerCell_WithoutHistoricOrStagedRows()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 2);
        await using var db = h.CreateContext();
        var superseded = Panel(h, 0, 1, photo: true);
        var reshot = Panel(h, 0, 2, photo: true);
        var untouched = Panel(h, 1, 1, photo: true);
        var staged = Panel(h, 1, 3, photo: false);
        db.WallPanels.AddRange(superseded, reshot, untouched, staged);
        var expected = new[]
        {
            AddHold(db, h, reshot, 2), AddHold(db, h, untouched, 1), AddHold(db, h, null, 2),
        };
        AddHold(db, h, superseded, 1);     // historic: its panel was re-shot
        AddHold(db, h, staged, 3);         // staged panel
        AddHold(db, h, reshot, 3);         // staged row on a live panel
        AddHold(db, h, null, 1);           // legacy centre-photo hold of an older generation
        await db.SaveChangesAsync();

        var panels = await LiveWallHolds.LoadPanelIdsAsync(db, h.WallId);
        var live = await (await LiveWallHolds.QueryAsync(db, h.WallId)).Select(x => x.Id).ToListAsync();

        Assert.Equal(new[] { reshot.Id, untouched.Id }.Order(), panels.Order());
        Assert.Equal(expected.Order(), live.Order());
    }

    [Fact]
    public async Task Placement_IgnoresSupersededPanels_AndTheirHistoricHolds()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var live = await s.AddHoldAsync(0.25, 0.5);
        Guid old;
        Guid historic;
        await using (var db = h.CreateContext())
        {
            // The c0 photo before a re-shoot: same image, so the old run registered and placed its holds too.
            var panel = new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Generation = 0, Photo = [10] };
            db.WallPanels.Add(panel);
            var hold = new Hold { WallId = h.WallId, WallPanelId = panel.Id, X = 0.3, Y = 0.5, Radius = 0.01, Generation = 0 };
            db.Holds.Add(hold);
            await db.SaveChangesAsync();
            (old, historic) = (panel.Id, hold.Id);
        }

        var result = await s.Service().PlaceAsync(h.WallId);

        Assert.Equal(1, result.Placed);
        Assert.DoesNotContain(result.Panels, p => p.PanelId == old);
        var holds = await s.LoadHoldsAsync();
        Assert.Equal("0", holds[live].FacetId);
        Assert.Null(holds[historic].FacetId);
        Assert.Null(holds[historic].PlaneAMm);
    }

    private static WallPanel Panel(WallTestHarness h, int col, int generation, bool photo) => new()
    {
        WallId = h.WallId,
        Col = col,
        Row = 0,
        Generation = generation,
        Photo = photo ? [1] : null,
        StagedPhoto = photo ? null : [2],
    };

    private static Guid AddHold(Blocwerk.Core.Data.BlocwerkDbContext db, WallTestHarness h, WallPanel? panel, int generation)
    {
        var hold = new Hold { WallId = h.WallId, WallPanelId = panel?.Id, X = 0.5, Y = 0.5, Radius = 0.01, Generation = generation };
        db.Holds.Add(hold);
        return hold.Id;
    }
}
