// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text;
using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Re-place outlier holds in 3D" end to end on <see cref="HoldPlacementScenario"/>'s wall: photo c0 is consistent (a = 4000·x,
/// b = 3000·(1 − y) on facet 0) except one stray hold; photo c1 sees the same holds at x₁ = 1.5·x₀ − 0.05, its copies are linked to
/// c0's but were placed on the wrong facet. Only the holds' 3D fields may change.
/// </summary>
public class HoldOutlierPlacementTests
{
    /// <summary>The holds' 3D fields: everything else on a hold is panel geometry or metadata and must stay byte-identical.</summary>
    private static readonly HashSet<string> ThreeDFields =
    [
        nameof(Hold.WidthMm), nameof(Hold.HeightMm), nameof(Hold.AreaMm2), nameof(Hold.FacetId), nameof(Hold.PlaneAMm),
        nameof(Hold.PlaneBMm), nameof(Hold.FingerprintJson), nameof(Hold.MetricSource), nameof(Hold.FootprintMm),
        nameof(Hold.ProtrusionMm), nameof(Hold.VolumePlacementJson),
    ];

    [Fact]
    public async Task Replace_MovesTheOutliers_AndLeavesPanelGeometryLinksAndBouldersByteIdentical()
    {
        using var h = new WallTestHarness();
        var (s, stray, copies) = await SeedAsync(h);
        var before = await NonThreeDSnapshotAsync(h);

        var result = await s.Service().ReplaceOutliersAsync(h.WallId);

        Assert.Equal(before, await NonThreeDSnapshotAsync(h));
        var holds = await s.LoadHoldsAsync();
        Assert.Equal(copies.Count + 1, result.Placed);
        Assert.Equal(("0", HoldMetric.Consensus), (holds[stray].FacetId, holds[stray].MetricSource));
        Assert.Equal(4000 * 0.25, holds[stray].PlaneAMm!.Value, 1);
        foreach (var (copy, original) in copies)
        {
            Assert.Equal(
                (holds[original].FacetId, holds[original].PlaneAMm, holds[original].PlaneBMm),
                (holds[copy].FacetId, holds[copy].PlaneAMm, holds[copy].PlaneBMm));
            Assert.Null(holds[copy].FootprintMm);
        }

        s.Queue.Received(1).Enqueue(h.WallId, Arg.Is<IEnumerable<Guid>>(ids => ids.Count() == result.Placed));
    }

    [Fact]
    public async Task Preview_CountsWithoutWriting()
    {
        using var h = new WallTestHarness();
        var (s, _, copies) = await SeedAsync(h);
        var before = await s.LoadHoldsAsync();

        var preview = await s.Service().PreviewOutliersAsync(h.WallId);

        Assert.Equal((copies.Count, 1, copies.Count + 1), (preview.FromTwins, preview.FromNeighbours, preview.ToReplace));
        Assert.Equivalent(before, await s.LoadHoldsAsync());
        await using var db = h.CreateContext();
        Assert.False(await db.HoldPlacementRuns.AnyAsync());
    }

    [Fact]
    public async Task Revert_RestoresEveryHoldExactly()
    {
        using var h = new WallTestHarness();
        var (s, _, _) = await SeedAsync(h);
        var before = await s.LoadHoldsAsync();
        var service = s.Service();
        var run = await service.ReplaceOutliersAsync(h.WallId);

        var revert = await service.RevertAsync(h.WallId, run.RunId);

        Assert.Equal(run.Placed, revert.Reverted);
        Assert.Equivalent(before, await s.LoadHoldsAsync());
        var status = await service.GetStatusAsync(h.WallId);
        Assert.Equal((HoldPlacementTrigger.Outliers, true), (status.LatestRun!.Trigger, status.LatestRun.RevertedAt is not null));
    }

    [Fact]
    public async Task EditPath_LeavesTheMovedHoldsAlone_UntilOneIsReshaped()
    {
        using var h = new WallTestHarness();
        var (s, stray, _) = await SeedAsync(h);
        var service = s.Service();
        await service.ReplaceOutliersAsync(h.WallId);

        Assert.Empty(await service.PlaceEditedAsync(h.WallId, (await s.LoadHoldsAsync()).Keys.ToList()));

        await using (var db = h.CreateContext())
        {
            var hold = await db.Holds.SingleAsync(x => x.Id == stray);
            hold.InvalidateGlyphForEdit(moved: false, reshaped: true);
            hold.ShapePoints = ShapePoint.DefaultOctagon(0.015);
            await db.SaveChangesAsync();
        }

        Assert.Equal([stray], await service.PlaceEditedAsync(h.WallId, [stray]));
        Assert.Equal(HoldMetric.TextureRegistration, (await s.LoadHoldsAsync())[stray].MetricSource);
    }

    [Fact]
    public async Task NonAdmin_IsRefused()
    {
        using var h = new WallTestHarness();
        var (s, _, _) = await SeedAsync(h);
        h.ActingUser = await h.AddMemberAsync("climber@test", WallRole.Moderator);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service().PreviewOutliersAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service().ReplaceOutliersAsync(h.WallId));
    }

    /// <summary>
    /// c0: a 5 × 4 grid placed consistently plus one stray hold 600 mm off; c1: linked copies of ten of them, scattered on facet 1, one
    /// on a boulder with c0's original. Every hold has an outline so a changed shape would show.
    /// </summary>
    private static async Task<(HoldPlacementScenario S, Guid Stray, List<(Guid Copy, Guid Original)> Copies)> SeedAsync(WallTestHarness h)
    {
        var s = await HoldPlacementScenario.CreateAsync(h);
        var grid = new List<(Guid Id, double X, double Y)>();
        for (var i = 0; i < 5; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                var (x, y) = (0.06 + (0.08 * i) + (0.01 * j), 0.15 + (0.2 * j));
                grid.Add((await s.AddHoldAsync(x, y, configure: Placed("0", 4000 * x, 3000 * (1 - y))), x, y));
            }
        }

        var stray = await s.AddHoldAsync(0.25, 0.42, configure: Placed("0", (4000 * 0.25) + 600, 3000 * 0.58));
        var copies = new List<(Guid Copy, Guid Original)>();
        var scatter = new Random(7);
        foreach (var (id, x, y) in grid.Where((_, i) => i % 2 == 0))
        {
            // Scattered, so c1 is clearly the less consistent photo.
            var (a, b) = (300 + (1000 * x) + scatter.Next(-150, 150), 1500 + scatter.Next(-150, 150));
            copies.Add((await s.AddHoldAsync((1.5 * x) - 0.05, y, s.PanelC1, Placed("1", a, b)), id));
        }

        await using var db = h.CreateContext();
        var boulder = new Boulder { WallId = h.WallId, Name = "Problem", CreatedByUserId = h.Owner.Id };
        db.Boulders.Add(boulder);
        db.BoulderHolds.AddRange(
            new BoulderHold { BoulderId = boulder.Id, HoldId = copies[0].Original },
            new BoulderHold { BoulderId = boulder.Id, HoldId = stray, Type = HoldType.Start });
        db.HoldLinks.AddRange(copies.Select(c => new HoldLink { WallId = h.WallId, HoldAId = c.Original, HoldBId = c.Copy, Kind = HoldLinkKind.Same }));
        await db.SaveChangesAsync();
        return (s, stray, copies);
    }

    private static Action<Hold> Placed(string facet, double a, double b) => hold =>
    {
        (hold.FacetId, hold.PlaneAMm, hold.PlaneBMm, hold.MetricSource) = (facet, a, b, HoldMetric.MultiMarker);
        (hold.WidthMm, hold.HeightMm, hold.AreaMm2) = (40, 30, 900);
        (hold.FootprintMm, hold.ProtrusionMm) = ("{\"old\":true}", "{\"old\":true}");
        hold.ShapePoints = ShapePoint.DefaultOctagon(0.01);
    };

    /// <summary>Every column of the holds except their 3D fields, and every column of panels, links, boulders and their holds.</summary>
    internal static async Task<string> NonThreeDSnapshotAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var text = new StringBuilder();
        Append(db, (await db.Holds.AsNoTracking().ToListAsync()).OrderBy(x => x.Id), text, ThreeDFields);
        Append(db, (await db.WallPanels.AsNoTracking().ToListAsync()).OrderBy(x => x.Id), text, new HashSet<string>());
        Append(db, (await db.HoldLinks.AsNoTracking().ToListAsync()).OrderBy(x => x.Id), text, new HashSet<string>());
        Append(db, (await db.Boulders.AsNoTracking().ToListAsync()).OrderBy(x => x.Id), text, new HashSet<string>());
        Append(db, (await db.BoulderHolds.AsNoTracking().ToListAsync()).OrderBy(x => x.HoldId), text, new HashSet<string>());
        return text.ToString();
    }

    private static void Append<T>(BlocwerkDbContext db, IEnumerable<T> rows, StringBuilder text, ISet<string> skip)
        where T : class
    {
        var properties = db.Model.FindEntityType(typeof(T))!.GetProperties().Where(p => p.PropertyInfo is not null && !skip.Contains(p.Name)).ToList();
        Assert.NotEmpty(properties);
        foreach (var row in rows)
        {
            foreach (var p in properties)
            {
                text.Append(p.Name).Append('=').Append(JsonSerializer.Serialize(p.PropertyInfo!.GetValue(row))).Append(';');
            }

            text.AppendLine();
        }
    }
}
