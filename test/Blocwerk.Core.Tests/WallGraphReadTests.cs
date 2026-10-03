using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Pins the object graph <c>IWallService.GetWallAsync</c> hands the pages (members, live holds, boulders with
/// their creator and holds, the back-references EF fixes up) so the read can change how it fetches the wall row
/// without changing what callers see.
/// </summary>
public class WallGraphReadTests
{
    [Fact]
    public async Task GetWall_ReturnsFullGraph_WithoutPhotos()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);
        var (activeId, archivedId) = await SeedBouldersAsync(h, holds);

        var wall = await h.WallService.GetWallAsync(h.WallId);

        Assert.NotNull(wall);
        Assert.Null(wall!.Photo);
        Assert.Null(wall.StagedPhoto);
        Assert.Equal("Test Wall", wall.Name);
        Assert.Equal("image/jpeg", wall.PhotoContentType);
        Assert.Single(wall.Members);
        Assert.Equal(3, wall.Holds.Count);

        var active = Assert.Single(wall.Boulders, b => b.Id == activeId);
        Assert.Equal(h.Owner.Id, active.CreatedBy.Id);
        Assert.Equal(2, active.BoulderHolds.Count);
        Assert.Same(wall, active.Wall);

        // Fixup: a boulder hold points at the very Hold instance the wall lists.
        Assert.All(active.BoulderHolds, bh => Assert.Same(wall.Holds.Single(x => x.Id == bh.HoldId), bh.Hold));

        // Archived boulders are not part of the wall graph.
        Assert.DoesNotContain(wall.Boulders, b => b.Id == archivedId);
        Assert.Single(wall.Boulders);
    }

    [Fact]
    public async Task GetWall_UnknownWall_ReturnsNull()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        Assert.Null(await h.WallService.GetWallAsync(Guid.NewGuid()));
    }

    private static async Task<(Guid Active, Guid Archived)> SeedBouldersAsync(WallTestHarness h, List<Hold> holds)
    {
        await using var db = h.CreateContext();
        var active = new Boulder { WallId = h.WallId, Name = "Active", CreatedByUserId = h.Owner.Id };
        var archived = new Boulder { WallId = h.WallId, Name = "Archived", CreatedByUserId = h.Owner.Id, IsArchived = true };
        db.Boulders.AddRange(active, archived);
        db.Set<BoulderHold>().AddRange(
            new BoulderHold { BoulderId = active.Id, HoldId = holds[0].Id },
            new BoulderHold { BoulderId = active.Id, HoldId = holds[1].Id },
            new BoulderHold { BoulderId = archived.Id, HoldId = holds[2].Id });
        await db.SaveChangesAsync();
        return (active.Id, archived.Id);
    }
}
