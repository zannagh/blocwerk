using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers the wall-recreation paths, which rewrite the hold model and can silently
/// destroy boulder history if they get it wrong.
/// </summary>
public class WallRecreateTests
{
    private static void StubDetection(WallTestHarness h, int count)
    {
        var detected = Enumerable.Range(0, count)
            .Select(i => new DetectedHold(0.5 + (i * 0.01), 0.5, 0.02, null, 0.9))
            .ToList();

        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(detected));
    }

    [Fact]
    public async Task ConfirmRecreate_MarksBouldersHistoric_AndKeepsAscents()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 4);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Original", "6A", [new BoulderHoldInput(holds[0].Id, HoldType.Start)]);

        await using (var db = h.CreateContext())
        {
            db.Attempts.Add(new Attempt { BoulderId = boulder.Id, UserId = h.Owner.Id, Type = AttemptType.Send });
            await db.SaveChangesAsync();
        }

        StubDetection(h, 3);
        await h.WallService.StageRecreateAsync(h.WallId, [9, 9, 9], "image/jpeg");
        var result = await h.WallService.ConfirmRecreateAsync(h.WallId);

        Assert.Equal(1, result.BouldersMadeHistoric);

        await using var check = h.CreateContext();
        var reloaded = await check.Boulders.Include(b => b.Attempts).FirstAsync(b => b.Id == boulder.Id);

        Assert.True(reloaded.IsHistoric);
        Assert.Equal("Original", reloaded.Name);
        Assert.Equal("6A", reloaded.Grade);
        Assert.Single(reloaded.Attempts);

        var wall = await check.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.Equal(1, wall.CurrentGeneration);
        Assert.Equal(WallStagingMode.None, wall.StagingMode);
        Assert.Null(wall.StagedPhoto);
    }

    [Fact]
    public async Task ConfirmRecreate_PrunesUnreferencedHolds_ButKeepsReferencedOnes()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 4);

        await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Keeper", null, [new BoulderHoldInput(holds[1].Id)]);

        StubDetection(h, 2);
        await h.WallService.StageRecreateAsync(h.WallId, [9], "image/jpeg");
        var result = await h.WallService.ConfirmRecreateAsync(h.WallId);

        // 4 seeded holds, 1 referenced by a boulder -> 3 pruned.
        Assert.Equal(3, result.HoldsPruned);

        await using var check = h.CreateContext();
        var remaining = await check.Holds.Where(x => x.WallId == h.WallId).ToListAsync();

        Assert.Contains(remaining, x => x.Id == holds[1].Id && x.Generation == 0);
        Assert.DoesNotContain(remaining, x => x.Id == holds[0].Id);
        Assert.Equal(2, remaining.Count(x => x.Generation == 1));
    }

    [Fact]
    public async Task ConfirmRecreate_ArchivesRetiredPhoto_SoHistoricViewResolves()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        StubDetection(h, 1);
        await h.WallService.StageRecreateAsync(h.WallId, [7, 7], "image/png");
        await h.WallService.ConfirmRecreateAsync(h.WallId);

        var archived = await h.WallService.GetPhotoForGenerationAsync(h.WallId, 0);
        Assert.NotNull(archived);
        Assert.Equal(new byte[] { 1, 2, 3 }, archived!.Photo);
        Assert.Equal("image/jpeg", archived.ContentType);

        var live = await h.WallService.GetPhotoForGenerationAsync(h.WallId, 1);
        Assert.NotNull(live);
        Assert.Equal(new byte[] { 7, 7 }, live!.Photo);

        var oldHolds = await h.WallService.GetHoldsForGenerationAsync(h.WallId, 0);
        Assert.Contains(oldHolds, x => x.Id == holds[0].Id);
    }

    [Fact]
    public async Task ConfirmRecreate_Twice_StillResolvesTheOldestGeneration()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        StubDetection(h, 1);
        await h.WallService.StageRecreateAsync(h.WallId, [7], "image/jpeg");
        await h.WallService.ConfirmRecreateAsync(h.WallId);

        StubDetection(h, 1);
        await h.WallService.StageRecreateAsync(h.WallId, [8], "image/jpeg");
        await h.WallService.ConfirmRecreateAsync(h.WallId);

        var genZero = await h.WallService.GetPhotoForGenerationAsync(h.WallId, 0);
        var genOne = await h.WallService.GetPhotoForGenerationAsync(h.WallId, 1);

        Assert.Equal(new byte[] { 1, 2, 3 }, genZero!.Photo);
        Assert.Equal(new byte[] { 7 }, genOne!.Photo);

        // The boulder's hold is still referenced, so it survived both prunes.
        var oldHolds = await h.WallService.GetHoldsForGenerationAsync(h.WallId, 0);
        Assert.Contains(oldHolds, x => x.Id == holds[0].Id);
    }

    [Fact]
    public async Task DiscardStagedPhoto_RescuesStagedHoldsThatABoulderAlreadyUses()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        StubDetection(h, 1);
        await h.WallService.StageRecreateAsync(h.WallId, [9], "image/jpeg");

        // Mirrors HoldPicker placing a virtual hold while a photo is staged: the hold
        // lands in the staged generation and is immediately linked to a boulder.
        var staged = await h.WallService.AddHoldAsync(h.WallId, 0.7, 0.7, 0.02, null, isVirtual: true);
        await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Uses staged hold", null, [new BoulderHoldInput(staged.Id), new BoulderHoldInput(holds[0].Id)]);

        // Previously threw DbUpdateException on the restricted BoulderHold FK.
        await h.WallService.DiscardStagedPhotoAsync(h.WallId);

        await using var check = h.CreateContext();
        var wall = await check.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.Equal(WallStagingMode.None, wall.StagingMode);
        Assert.Null(wall.StagedPhoto);

        var rescued = await check.Holds.FirstAsync(x => x.Id == staged.Id);
        Assert.Equal(wall.CurrentGeneration, rescued.Generation);

        // The detected staged hold had no links and should be gone.
        Assert.Equal(3, await check.Holds.CountAsync(x => x.WallId == h.WallId));
    }

    [Fact]
    public async Task ConfirmRecreate_Throws_WhenNotStagedAsRecreate()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);

        StubDetection(h, 1);
        await h.WallService.StagePhotoAsync(h.WallId, [9], "image/jpeg");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.WallService.ConfirmRecreateAsync(h.WallId));
    }
}
