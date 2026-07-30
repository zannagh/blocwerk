using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Segments replace the single wall border once a wall has any, so the cleanup tool must
/// treat them as a union — deleting a hold that sits in a valid segment would destroy
/// real wall data.
/// </summary>
public class WallSegmentTests
{
    [Fact]
    public async Task ReplaceSegments_StoresTheWholeSetAndClampsTheAngle()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);

        await h.SegmentService.ReplaceSegmentsAsync(
            h.WallId,
            [
                new WallSegmentInput("Slab", 200, Box(0.0, 0.0, 1.0, 0.5), SortOrder: 0),
                new WallSegmentInput("Overhang", 35, Box(0.0, 0.5, 1.0, 1.0), SortOrder: 1),
            ]);

        var stored = await h.SegmentService.GetSegmentsAsync(h.WallId);

        Assert.Equal(2, stored.Count);
        Assert.Equal("Slab", stored[0].Name);
        Assert.Equal(90, stored[0].Angle);
        Assert.Equal(35, stored[1].Angle);
        Assert.Equal(4, stored[0].Points.Count);

        // Replacing again wipes the previous set rather than appending to it.
        await h.SegmentService.ReplaceSegmentsAsync(
            h.WallId, [new WallSegmentInput("Only", 10, Box(0.0, 0.0, 1.0, 1.0))]);

        var replaced = await h.SegmentService.GetSegmentsAsync(h.WallId);
        Assert.Equal("Only", Assert.Single(replaced).Name);
    }

    [Fact]
    public async Task ReplaceSegments_RejectsADegeneratePolygon()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.SegmentService.ReplaceSegmentsAsync(
            h.WallId,
            [new WallSegmentInput("Bad", 10, [new ShapePoint { Dx = 0, Dy = 0 }, new ShapePoint { Dx = 1, Dy = 1 }])]));
    }

    [Fact]
    public async Task DeleteSegment_RemovesOnlyThatSegment()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);

        await h.SegmentService.ReplaceSegmentsAsync(
            h.WallId,
            [
                new WallSegmentInput("A", 10, Box(0.0, 0.0, 1.0, 0.5)),
                new WallSegmentInput("B", 20, Box(0.0, 0.5, 1.0, 1.0), SortOrder: 1),
            ]);

        var stored = await h.SegmentService.GetSegmentsAsync(h.WallId);
        await h.SegmentService.DeleteSegmentAsync(stored[0].Id);

        var left = await h.SegmentService.GetSegmentsAsync(h.WallId);
        Assert.Equal("B", Assert.Single(left).Name);
    }

    [Fact]
    public async Task DeletingTheWall_CascadesToItsSegments()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        await h.SegmentService.ReplaceSegmentsAsync(
            h.WallId, [new WallSegmentInput("A", 10, Box(0.0, 0.0, 1.0, 1.0))]);

        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
            db.Walls.Remove(wall);
            await db.SaveChangesAsync();
        }

        await using var check = h.CreateContext();
        Assert.Empty(await check.WallSegments.Where(s => s.WallId == h.WallId).ToListAsync());
    }

    [Fact]
    public async Task CleanOutsideBorder_KeepsHoldsInsideAnySegment()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        // Two disjoint columns with a gap in the middle.
        await h.SegmentService.ReplaceSegmentsAsync(
            h.WallId,
            [
                new WallSegmentInput("Left", 10, Box(0.0, 0.0, 0.4, 1.0)),
                new WallSegmentInput("Right", 20, Box(0.6, 0.0, 1.0, 1.0), SortOrder: 1),
            ]);

        var inLeft = await AddHoldAsync(h, 0.2, 0.5);
        var inRight = await AddHoldAsync(h, 0.8, 0.5);
        var inGap = await AddHoldAsync(h, 0.5, 0.5);

        var removed = await h.WallService.CleanOutsideBorderAsync(h.WallId);

        Assert.Equal(1, removed);

        await using var check = h.CreateContext();
        var remaining = await check.Holds.Where(x => x.WallId == h.WallId).Select(x => x.Id).ToListAsync();
        Assert.Contains(inLeft, remaining);
        Assert.Contains(inRight, remaining);
        Assert.DoesNotContain(inGap, remaining);
    }

    [Fact]
    public async Task CleanOutsideBorder_FallsBackToBorderPoints_WhenThereAreNoSegments()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
            wall.BorderPoints = Box(0.0, 0.0, 0.5, 1.0);
            await db.SaveChangesAsync();
        }

        var inside = await AddHoldAsync(h, 0.2, 0.5);
        var outside = await AddHoldAsync(h, 0.8, 0.5);

        var removed = await h.WallService.CleanOutsideBorderAsync(h.WallId);

        Assert.Equal(1, removed);

        await using var check = h.CreateContext();
        var remaining = await check.Holds.Where(x => x.WallId == h.WallId).Select(x => x.Id).ToListAsync();
        Assert.Contains(inside, remaining);
        Assert.DoesNotContain(outside, remaining);
    }

    private static async Task<Guid> AddHoldAsync(WallTestHarness h, double x, double y)
    {
        await using var db = h.CreateContext();
        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        var hold = new Hold
        {
            WallId = h.WallId,
            X = x,
            Y = y,
            Radius = 0.02,
            Generation = wall.CurrentGeneration,
        };

        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    private static List<ShapePoint> Box(double x0, double y0, double x1, double y1) =>
    [
        new() { Dx = x0, Dy = y0 },
        new() { Dx = x1, Dy = y0 },
        new() { Dx = x1, Dy = y1 },
        new() { Dx = x0, Dy = y1 },
    ];
}
