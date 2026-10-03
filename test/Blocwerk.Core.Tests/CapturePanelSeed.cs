using System.Globalization;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A two-panel grid (live centre and right neighbour) with holds on each and a boulder over them, plus
/// a column-by-column snapshot of every panel, hold, boulder and boulder-hold row for byte comparisons.
/// </summary>
internal static class CapturePanelSeed
{
    public static async Task SeedGridWithBoulderAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var centre = new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg" };
        var right = new WallPanel { WallId = h.WallId, Col = 1, Row = 0, Photo = [4, 5, 6], PhotoContentType = "image/png" };
        db.WallPanels.AddRange(centre, right);
        var holds = new List<Hold>
        {
            new() { WallId = h.WallId, WallPanelId = centre.Id, X = 0.2, Y = 0.3, Radius = 0.02, Color = "red" },
            new() { WallId = h.WallId, WallPanelId = centre.Id, X = 0.6, Y = 0.4, Radius = 0.03 },
            new() { WallId = h.WallId, WallPanelId = right.Id, X = 0.1, Y = 0.8, Radius = 0.025, Color = "blue" },
        };
        db.Holds.AddRange(holds);
        var boulder = new Boulder { WallId = h.WallId, Name = "Arête", Grade = "6a", CreatedByUserId = h.Owner.Id };
        db.Boulders.Add(boulder);
        db.BoulderHolds.AddRange(holds.Select(x => new BoulderHold { BoulderId = boulder.Id, HoldId = x.Id }));
        await db.SaveChangesAsync();
    }

    /// <summary>Every mapped column of every panel, hold, boulder and boulder-hold row, in a stable order.</summary>
    public static async Task<string> SnapshotAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var panels = await db.WallPanels.IgnoreQueryFilters().AsNoTracking().OrderBy(p => p.Col).ThenBy(p => p.Row).ToListAsync();
        var holds = await db.Holds.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.X).ToListAsync();
        var boulders = await db.Boulders.IgnoreQueryFilters().AsNoTracking().OrderBy(b => b.Name).ToListAsync();
        var links = (await db.BoulderHolds.IgnoreQueryFilters().AsNoTracking().ToListAsync())
            .OrderBy(l => l.HoldId).ToList();
        Assert.Equal(2, panels.Count);
        Assert.Equal(3, holds.Count);
        return string.Join(
            "\n",
            panels.Select(p => Row(db, p)).Concat(holds.Select(x => Row(db, x)))
                .Concat(boulders.Select(b => Row(db, b))).Concat(links.Select(l => Row(db, l))));
    }

    /// <summary>One entity as "Column=value" pairs over the EF model's mapped properties.</summary>
    public static string Row<T>(BlocwerkDbContext db, T entity, params string[] skip)
        where T : class
    {
        var properties = db.Model.FindEntityType(typeof(T))!.GetProperties()
            .Where(p => p.PropertyInfo is not null && !skip.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal);
        return typeof(T).Name + ": " + string.Join(
            "|", properties.Select(p => $"{p.Name}={Format(p.PropertyInfo!.GetValue(entity))}"));
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
