using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Stitching;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers the stitched staging mode end to end: applying a sidecar result to the staged slot,
/// confirming it without losing boulder links, and discarding it without leaking files or holds.
/// </summary>
public class WallStitchStagingTests
{
    private static readonly byte[] DisplayOrthoBytes = [10, 11, 12];
    private static readonly byte[] DisplayAngledBytes = [20, 21, 22];

    [Fact]
    public async Task ApplyResultToStaging_CreatesStagedHolds_WithNeedsReviewPerClassification()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);
        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Ortho, [
            Reported(holds[0], 0.5, 0.5, "matched", 0.95),
            Reported(holds[1], 0.6, 0.4, "uncertain", 0.5),
            Reported(holds[2], 0.7, 0.3, "missing", 0.1),
        ]);

        await h.WallStitchService.ApplyResultToStagingAsync(job.Id, h.Owner.Id);

        await using var db = h.CreateContext();
        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.Equal(WallStagingMode.Stitched, wall.StagingMode);
        Assert.Equal(WallPhotoProjection.Ortho, wall.StagedPhotoProjection);
        Assert.Equal(DisplayOrthoBytes, wall.StagedPhoto);
        Assert.Equal(DisplayAngledBytes, wall.StagedPhotoAlternate);
        Assert.Equal("image/jpeg", wall.StagedPhotoContentType);
        Assert.Equal("image/jpeg", wall.StagedPhotoAlternateContentType);
        Assert.Equal(45.0, wall.StagedPhotoWallAngleDegrees);
        Assert.Equal(0.7071, wall.StagedPhotoVerticalScale!.Value, 4);
        Assert.NotNull(wall.StagedAt);
        Assert.True(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(wall.StagedOrthoMasterPath!)));
        Assert.True(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(wall.StagedAngledMasterPath!)));

        var staged = await db.Holds.Where(x => x.Generation == 1).ToListAsync();
        Assert.Equal(3, staged.Count);
        Assert.All(staged, s => Assert.NotNull(s.AlignmentSourceHoldId));

        var matched = staged.Single(s => s.AlignmentSourceHoldId == holds[0].Id);
        Assert.False(matched.NeedsReview);
        Assert.Equal(0.5, matched.X);
        Assert.Equal(0.95, matched.Confidence);
        Assert.True(staged.Single(s => s.AlignmentSourceHoldId == holds[1].Id).NeedsReview);
        Assert.True(staged.Single(s => s.AlignmentSourceHoldId == holds[2].Id).NeedsReview);

        // The live generation is untouched until confirm.
        Assert.Equal(3, await db.Holds.CountAsync(x => x.Generation == 0));
    }

    [Fact]
    public async Task ApplyResultToStaging_KeepsAMissingHold_AndItsBoulderLinkSurvivesConfirm()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Link me", "6A", [new BoulderHoldInput(holds[1].Id, HoldType.Start)]);

        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Angled, [
            Reported(holds[0], 0.2, 0.2, "matched", 0.9),
            Reported(holds[1], 0.8, 0.8, "missing", 0.05),
        ]);
        await h.WallStitchService.ApplyResultToStagingAsync(job.Id, h.Owner.Id);

        await using (var db = h.CreateContext())
        {
            var missing = await db.Holds.SingleAsync(x => x.Generation == 1 && x.AlignmentSourceHoldId == holds[1].Id);
            Assert.True(missing.NeedsReview);
            Assert.Equal(0.8, missing.X);
        }

        await h.WallService.ConfirmStagedPhotoAsync(h.WallId);

        await using var check = h.CreateContext();
        var link = await check.BoulderHolds.SingleAsync(bh => bh.BoulderId == boulder.Id);
        Assert.Equal(holds[1].Id, link.HoldId);
        var survivor = await check.Holds.SingleAsync(x => x.Id == holds[1].Id);
        Assert.Equal(1, survivor.Generation);
        Assert.Equal(0.8, survivor.X);
        Assert.True(survivor.NeedsReview);
        Assert.False((await check.Boulders.SingleAsync(b => b.Id == boulder.Id)).IsHistoric);
    }

    [Fact]
    public async Task ApplyResultToStaging_CarriesForwardHoldsTheSidecarNeverReported()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);

        // Only one of the three live holds comes back in the result; the other two are silent
        // drops on the sidecar's side and must survive as flagged clones.
        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Angled, [
            Reported(holds[0], 0.25, 0.25, "matched", 0.9),
        ]);
        await h.WallStitchService.ApplyResultToStagingAsync(job.Id, h.Owner.Id);

        await using var db = h.CreateContext();
        var staged = await db.Holds.Where(x => x.Generation == 1).ToListAsync();
        Assert.Equal(3, staged.Count);

        foreach (var source in new[] { holds[1], holds[2] })
        {
            var carried = staged.Single(s => s.AlignmentSourceHoldId == source.Id);
            Assert.True(carried.NeedsReview);
            Assert.Equal(source.X, carried.X);
            Assert.Equal(source.Y, carried.Y);
            Assert.NotEqual(source.Id, carried.Id);
        }
    }

    [Fact]
    public async Task ConfirmStagedPhoto_PromotesTheStitchedPair_AndDeletesTheRetiredMasters()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Keeper", "6B", [new BoulderHoldInput(holds[0].Id, HoldType.Start)]);
        var (oldOrtho, oldAngled) = await GiveWallLiveMastersAsync(h);

        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Ortho, [
            Reported(holds[0], 0.31, 0.32, "matched", 0.9),
            Reported(holds[1], 0.41, 0.42, "uncertain", 0.4),
        ]);
        await h.WallStitchService.ApplyResultToStagingAsync(job.Id, h.Owner.Id);
        await h.WallService.ConfirmStagedPhotoAsync(h.WallId);

        await using var db = h.CreateContext();
        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.Equal(1, wall.CurrentGeneration);
        Assert.Equal(WallStagingMode.None, wall.StagingMode);
        Assert.Equal(DisplayOrthoBytes, wall.Photo);
        Assert.Equal(DisplayAngledBytes, wall.PhotoAlternate);
        Assert.Equal(WallPhotoProjection.Ortho, wall.PhotoProjection);
        Assert.Equal(45.0, wall.PhotoWallAngleDegrees);
        Assert.Equal(0.7071, wall.PhotoVerticalScale!.Value, 4);
        Assert.NotNull(wall.OrthoMasterPath);
        Assert.NotNull(wall.AngledMasterPath);
        Assert.Null(wall.StagedPhoto);
        Assert.Null(wall.StagedPhotoAlternate);
        Assert.Null(wall.StagedOrthoMasterPath);
        Assert.Null(wall.StagedAngledMasterPath);
        Assert.Null(wall.StagedPhotoWallAngleDegrees);

        // The retired masters are gone, the promoted ones are still on disk.
        Assert.False(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(oldOrtho)));
        Assert.False(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(oldAngled)));
        Assert.True(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(wall.OrthoMasterPath!)));
        Assert.True(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(wall.AngledMasterPath!)));

        // Holds keep their ids, so boulder links survive; no clones are left behind.
        var live = await db.Holds.Where(x => x.WallId == h.WallId).ToListAsync();
        Assert.Equal(2, live.Count);
        Assert.All(live, x => Assert.Equal(1, x.Generation));
        Assert.Equal(0.31, live.Single(x => x.Id == holds[0].Id).X);
        Assert.True(live.Single(x => x.Id == holds[1].Id).NeedsReview);
        Assert.Equal(holds[0].Id, (await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == boulder.Id)).HoldId);

        // The retired photo is archived exactly like the older staging modes do it.
        var reset = await db.WallResets.SingleAsync(r => r.WallId == h.WallId);
        Assert.Equal(0, reset.Generation);
        Assert.Equal<byte[]>([1, 2, 3], reset.PreviousPhoto!);
    }

    [Fact]
    public async Task DiscardStagedPhoto_RemovesStitchedHolds_AndTheStagedMasterFiles()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Angled, [
            Reported(holds[0], 0.5, 0.5, "matched", 0.9),
            Reported(holds[1], 0.6, 0.6, "uncertain", 0.4),
        ]);
        await h.WallStitchService.ApplyResultToStagingAsync(job.Id, h.Owner.Id);

        string stagedOrtho;
        string stagedAngled;
        await using (var db = h.CreateContext())
        {
            var staged = await db.Walls.FirstAsync(w => w.Id == h.WallId);
            stagedOrtho = staged.StagedOrthoMasterPath!;
            stagedAngled = staged.StagedAngledMasterPath!;
        }

        await h.WallService.DiscardStagedPhotoAsync(h.WallId);

        await using var check = h.CreateContext();
        var wall = await check.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.Equal(WallStagingMode.None, wall.StagingMode);
        Assert.Null(wall.StagedPhoto);
        Assert.Null(wall.StagedPhotoAlternate);
        Assert.Null(wall.StagedOrthoMasterPath);
        Assert.Null(wall.StagedAngledMasterPath);
        Assert.Null(wall.StagedPhotoVerticalScale);
        Assert.Equal(0, wall.CurrentGeneration);

        Assert.Empty(await check.Holds.Where(x => x.Generation == 1).ToListAsync());
        Assert.Equal(2, await check.Holds.CountAsync(x => x.Generation == 0));
        Assert.False(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(stagedOrtho)));
        Assert.False(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(stagedAngled)));
    }

    [Fact]
    public async Task ApplyResultToStaging_IsRejected_ForANonAdminAndForANonSucceededJob()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Angled, [
            Reported(holds[0], 0.5, 0.5, "matched", 0.9),
        ]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            h.WallStitchService.ApplyResultToStagingAsync(job.Id, member.Id));

        await using (var db = h.CreateContext())
        {
            var stored = await db.WallStitchJobs.SingleAsync();
            stored.Status = WallStitchJobStatus.Running;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.WallStitchService.ApplyResultToStagingAsync(job.Id, h.Owner.Id));

        await using var check = h.CreateContext();
        var wall = await check.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.Equal(WallStagingMode.None, wall.StagingMode);
        Assert.Null(wall.StagedPhoto);
        Assert.Empty(await check.Holds.Where(x => x.Generation == 1).ToListAsync());
    }

    private static StitchResultHold Reported(Hold source, double x, double y, string classification, double confidence) =>
        new(source.Id, x, y, 0.02, null, classification, confidence);

    /// <summary>Puts real master files on the wall so a confirm has something to retire.</summary>
    private static async Task<(string Ortho, string Angled)> GiveWallLiveMastersAsync(WallTestHarness h)
    {
        var ortho = CommitMaster(h.WallPhotoMasterStorage);
        var angled = CommitMaster(h.WallPhotoMasterStorage);

        await using var db = h.CreateContext();
        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        wall.OrthoMasterPath = ortho;
        wall.AngledMasterPath = angled;
        await db.SaveChangesAsync();
        return (ortho, angled);
    }

    private static string CommitMaster(IWallPhotoMasterStorage storage)
    {
        var temp = storage.CreateTempPath(".png");
        File.WriteAllBytes(temp, [7, 7, 7]);
        return storage.Commit(temp, ".png");
    }
}

/// <summary>
/// Drives the stubbed sidecar client through a full successful run so the staging tests can start
/// from a persisted, succeeded <see cref="WallStitchJob"/> with downloadable artifacts.
/// </summary>
internal static class StitchHarness
{
    public static async Task<WallStitchJob> RunSucceededJobAsync(
        WallTestHarness h,
        WallPhotoProjection projection,
        IReadOnlyList<StitchResultHold> holds)
    {
        h.StitchClient
            .CreateJobAsync(Arg.Any<IReadOnlyList<StitchPhotoUpload>>(), Arg.Any<StitchJobOptions>(), Arg.Any<StitchPhotoUpload?>(), Arg.Any<CancellationToken>())
            .Returns(new StitchJobCreationResult("sidecar-staging", "queued"));

        var options = new WallStitchStartOptions(WallAngleDegrees: 45.0, projection, TransferHolds: true);
        var photos = Enumerable.Range(0, 3)
            .Select(i => new StitchPhotoUpload($"{i}.jpeg", "image/jpeg", [(byte)i, 1, 2]))
            .ToList();
        var job = await h.WallStitchService.StartJobAsync(h.WallId, h.Owner.Id, photos, options);

        var result = new StitchJobResult(
            new StitchArtifactRef("ortho.png", 7648, 4864),
            new StitchArtifactRef("angled.png", 7648, 3439),
            "display-ortho.jpg",
            "display-angled.jpg",
            45.0,
            0.7071,
            null,
            holds);

        h.StitchClient.GetJobAsync("sidecar-staging", Arg.Any<CancellationToken>())
            .Returns(new StitchJobState("sidecar-staging", "succeeded", 1.0, "done", null, result));
        h.StitchClient
            .DownloadArtifactAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ci => WriteArtifactAsync(ci.ArgAt<string>(1), ci.ArgAt<Stream>(2)));

        await h.WallStitchService.RefreshJobAsync(job.Id);
        return job;
    }

    private static async Task WriteArtifactAsync(string artifact, Stream destination)
    {
        byte[] payload = artifact switch
        {
            "display-ortho.jpg" => [10, 11, 12],
            "display-angled.jpg" => [20, 21, 22],
            "ortho.png" => [1, 1, 1, 1],
            _ => [2, 2, 2, 2],
        };

        await destination.WriteAsync(payload);
    }
}
