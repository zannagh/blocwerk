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
    /// <summary>The pipeline's cameras.json, stored verbatim on the wall.</summary>
    internal const string CamerasJson = """{"schema":"blocwerk.wall-pipeline/1","frames":[]}""";

    /// <summary>Physical wall size the stubbed job reports; the pipeline is metric, not angle-based.</summary>
    internal const double WallWidthM = 3.5;
    internal const double WallHeightM = 4.2;

    /// <summary>
    /// Longer side of the stubbed flat master in pixels. The applier turns a carried-over hold's
    /// <c>matchDistancePx</c> into a fraction of its radius against this, so <see cref="Reported"/>
    /// scales a wanted radii distance back into pixels with the same number.
    /// </summary>
    internal const int FlatLongerSidePx = 7648;

    /// <summary>Normalised radius every seeded/reported hold uses; matches <see cref="FlatLongerSidePx"/>.</summary>
    private const double HoldRadius = 0.02;

    private static readonly byte[] DisplayFlatBytes = [10, 11, 12];
    private static readonly byte[] DisplayNaturalBytes = [20, 21, 22];

    [Fact]
    public async Task ApplyResultToStaging_CreatesStagedHolds_WithNeedsReviewPerClassification()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);
        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Flat, [
            Reported(holds[0], 0.5, 0.5, "carried_over", 0.1),
            Reported(holds[1], 0.6, 0.4, "carried_over", 1.5),
            Reported(holds[2], 0.7, 0.3, "missing"),
        ]);

        await h.WallStitchService.ApplyResultToStagingAsync(job.Id, h.Owner.Id);

        await using var db = h.CreateContext();
        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.Equal(WallStagingMode.Stitched, wall.StagingMode);
        Assert.Equal(WallPhotoProjection.Flat, wall.StagedPhotoProjection);
        Assert.Equal(DisplayFlatBytes, wall.StagedPhoto);
        Assert.Equal(DisplayNaturalBytes, wall.StagedPhotoAlternate);
        Assert.Equal("image/jpeg", wall.StagedPhotoContentType);
        Assert.Equal("image/jpeg", wall.StagedPhotoAlternateContentType);
        Assert.Equal(WallWidthM, wall.StagedPhotoWallWidthM);
        Assert.Equal(WallHeightM, wall.StagedPhotoWallHeightM);
        Assert.NotNull(wall.StagedPhotoCurvatureJson);
        Assert.Equal(CamerasJson, wall.StagedCamerasJson);
        Assert.NotNull(wall.StagedAt);
        Assert.True(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(wall.StagedFlatMasterPath!)));
        Assert.True(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(wall.StagedNaturalMasterPath!)));

        var staged = await db.Holds.Where(x => x.Generation == 1).ToListAsync();
        Assert.Equal(3, staged.Count);
        Assert.All(staged, s => Assert.NotNull(s.AlignmentSourceHoldId));

        // Confidence comes from the match distance, not from the pipeline's own score: 0.1 radii
        // off is a clean carry-over, 1.5 radii off is not.
        var carried = staged.Single(s => s.AlignmentSourceHoldId == holds[0].Id);
        Assert.False(carried.NeedsReview);
        Assert.Equal(0.5, carried.X);
        Assert.Equal(0.95, carried.Confidence);
        Assert.Equal(0.25, staged.Single(s => s.AlignmentSourceHoldId == holds[1].Id).Confidence, 4);
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

        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Natural, [
            Reported(holds[0], 0.2, 0.2, "carried_over", 0.1),
            Reported(holds[1], 0.8, 0.8, "missing"),
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
        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Natural, [
            Reported(holds[0], 0.25, 0.25, "carried_over", 0.1),
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
    public async Task ApplyResultToStaging_AddsNewlyDetectedHolds_Flagged_AndWithoutASourceHold()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Flat, [
            Reported(holds[0], 0.2, 0.2, "carried_over", 0.1),
            Detected(0.75, 0.65, 0.82),
        ]);

        await h.WallStitchService.ApplyResultToStagingAsync(job.Id, h.Owner.Id);

        await using var db = h.CreateContext();
        var staged = await db.Holds.Where(x => x.Generation == 1).ToListAsync();
        Assert.Equal(2, staged.Count);

        var added = staged.Single(x => x.AlignmentSourceHoldId is null);
        Assert.True(added.IsAutoDetected);
        Assert.True(added.NeedsReview);
        Assert.Equal(0.75, added.X);
        Assert.Equal(0.82, added.Confidence);

        // A new hold has no history, so it must not inherit anything from the old set.
        Assert.Null(added.Name);
        Assert.Null(added.Color);
        Assert.False(added.IsVirtual);
    }

    [Fact]
    public async Task ConfirmStagedPhoto_PromotesTheStitchedPair_AndDeletesTheRetiredMasters()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Keeper", "6B", [new BoulderHoldInput(holds[0].Id, HoldType.Start)]);
        var (oldFlat, oldNatural) = await GiveWallLiveMastersAsync(h);

        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Flat, [
            Reported(holds[0], 0.31, 0.32, "carried_over", 0.1),
            Reported(holds[1], 0.41, 0.42, "carried_over", 1.5),
        ]);
        await h.WallStitchService.ApplyResultToStagingAsync(job.Id, h.Owner.Id);
        await h.WallService.ConfirmStagedPhotoAsync(h.WallId);

        await using var db = h.CreateContext();
        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.Equal(1, wall.CurrentGeneration);
        Assert.Equal(WallStagingMode.None, wall.StagingMode);
        Assert.Equal(DisplayFlatBytes, wall.Photo);
        Assert.Equal(DisplayNaturalBytes, wall.PhotoAlternate);
        Assert.Equal(WallPhotoProjection.Flat, wall.PhotoProjection);
        Assert.Equal(WallWidthM, wall.PhotoWallWidthM);
        Assert.Equal(WallHeightM, wall.PhotoWallHeightM);
        Assert.NotNull(wall.PhotoCurvatureJson);
        Assert.Equal(CamerasJson, wall.CamerasJson);
        Assert.NotNull(wall.FlatMasterPath);
        Assert.NotNull(wall.NaturalMasterPath);
        Assert.Null(wall.StagedPhoto);
        Assert.Null(wall.StagedPhotoAlternate);
        Assert.Null(wall.StagedFlatMasterPath);
        Assert.Null(wall.StagedNaturalMasterPath);
        Assert.Null(wall.StagedPhotoWallWidthM);
        Assert.Null(wall.StagedPhotoWallHeightM);

        // The retired masters are gone, the promoted ones are still on disk.
        Assert.False(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(oldFlat)));
        Assert.False(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(oldNatural)));
        Assert.True(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(wall.FlatMasterPath!)));
        Assert.True(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(wall.NaturalMasterPath!)));

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
        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Natural, [
            Reported(holds[0], 0.5, 0.5, "carried_over", 0.1),
            Reported(holds[1], 0.6, 0.6, "carried_over", 1.5),
        ]);
        await h.WallStitchService.ApplyResultToStagingAsync(job.Id, h.Owner.Id);

        string stagedFlat;
        string stagedNatural;
        await using (var db = h.CreateContext())
        {
            var staged = await db.Walls.FirstAsync(w => w.Id == h.WallId);
            stagedFlat = staged.StagedFlatMasterPath!;
            stagedNatural = staged.StagedNaturalMasterPath!;
        }

        await h.WallService.DiscardStagedPhotoAsync(h.WallId);

        await using var check = h.CreateContext();
        var wall = await check.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.Equal(WallStagingMode.None, wall.StagingMode);
        Assert.Null(wall.StagedPhoto);
        Assert.Null(wall.StagedPhotoAlternate);
        Assert.Null(wall.StagedFlatMasterPath);
        Assert.Null(wall.StagedNaturalMasterPath);
        Assert.Null(wall.StagedPhotoCurvatureJson);
        Assert.Null(wall.StagedCamerasJson);
        Assert.Equal(0, wall.CurrentGeneration);

        Assert.Empty(await check.Holds.Where(x => x.Generation == 1).ToListAsync());
        Assert.Equal(2, await check.Holds.CountAsync(x => x.Generation == 0));
        Assert.False(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(stagedFlat)));
        Assert.False(File.Exists(h.WallPhotoMasterStorage.ResolvePhysicalPath(stagedNatural)));
    }

    [Fact]
    public async Task ApplyResultToStaging_IsRejected_ForANonAdminAndForANonSucceededJob()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var job = await StitchHarness.RunSucceededJobAsync(h, WallPhotoProjection.Natural, [
            Reported(holds[0], 0.5, 0.5, "carried_over", 0.1),
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

    /// <summary>
    /// A carryover entry for one of the wall's existing holds, for either the <c>carried</c> or the
    /// <c>missing</c> list (chosen by <paramref name="classification"/>). The applier derives
    /// <see cref="Hold.Confidence"/> from <see cref="StitchCarriedHold.MatchDistancePx"/>, so
    /// <paramref name="matchDistanceRadii"/> — how far off in radii we want the match to read — is
    /// scaled back into flat-master pixels with the same radius and longer-side the applier divides
    /// by, and it is what decides whether a carried-over hold gets flagged.
    /// </summary>
    private static StitchCarriedHold Reported(
        Hold source,
        double x,
        double y,
        string classification,
        double? matchDistanceRadii = null) =>
        new(
            source.Id,
            x,
            y,
            HoldRadius,
            ShapePoints: null,
            classification,
            MatchedDetectionId: null,
            MatchDistancePx: matchDistanceRadii is { } radii ? radii * HoldRadius * FlatLongerSidePx : null,
            ColourAgrees: null,
            BoulderLinkCount: 0,
            InFrame: true,
            Reason: null);

    /// <summary>A hold the pipeline detected that matches nothing in the old set.</summary>
    private static StitchNewHold Detected(double x, double y, double confidence) =>
        new($"det-{Guid.NewGuid():N}", x, y, HoldRadius, confidence, LikelyDuplicate: false);

    /// <summary>Puts real master files on the wall so a confirm has something to retire.</summary>
    private static async Task<(string Flat, string Natural)> GiveWallLiveMastersAsync(WallTestHarness h)
    {
        var flat = CommitMaster(h.WallPhotoMasterStorage);
        var natural = CommitMaster(h.WallPhotoMasterStorage);

        await using var db = h.CreateContext();
        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        wall.FlatMasterPath = flat;
        wall.NaturalMasterPath = natural;
        await db.SaveChangesAsync();
        return (flat, natural);
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
    /// <summary>
    /// Drives a successful job whose carryover is built from a mixed list of <see cref="StitchCarriedHold"/>
    /// (routed to the <c>carried</c>/<c>missing</c> lists by their classification) and
    /// <see cref="StitchNewHold"/> entries. <paramref name="blocker"/> sets the pipeline's standing
    /// caveat, for the "must review before an unattended live apply" path.
    /// </summary>
    public static async Task<WallStitchJob> RunSucceededJobAsync(
        WallTestHarness h,
        WallPhotoProjection projection,
        IReadOnlyList<object> reported,
        string? blocker = null)
    {
        h.StitchClient
            .CreateJobAsync(Arg.Any<IReadOnlyList<StitchPhotoUpload>>(), Arg.Any<StitchJobOptions>(), Arg.Any<StitchPhotoUpload?>(), Arg.Any<CancellationToken>())
            .Returns(new StitchJobCreationResult("sidecar-staging", "queued"));

        var options = new WallStitchStartOptions(
            WallStitchStagingTests.WallWidthM,
            WallStitchStagingTests.WallHeightM,
            projection,
            TransferHolds: true);
        var photos = Enumerable.Range(0, 3)
            .Select(i => new StitchPhotoUpload($"{i}.jpeg", "image/jpeg", [(byte)i, 1, 2]))
            .ToList();
        var job = await h.WallStitchService.StartJobAsync(h.WallId, h.Owner.Id, photos, options);

        var carried = reported.OfType<StitchCarriedHold>().Where(c => c.Classification == "carried_over").ToList();
        var missing = reported.OfType<StitchCarriedHold>().Where(c => c.Classification == "missing").ToList();
        var detected = reported.OfType<StitchNewHold>().ToList();
        var carryover = new StitchCarryover(
            Generation: 1,
            Counts: null,
            CountsBoulderLinked: null,
            EstimatedPrecision: 0.0,
            Blocker: blocker,
            Carried: carried,
            Missing: missing,
            New: detected);

        var result = new StitchJobResult(
            new StitchArtifactRef("flat.png", WallStitchStagingTests.FlatLongerSidePx, 4864),
            new StitchArtifactRef("natural.png", 7648, 4310),
            "display-flat.jpg",
            "display-natural.jpg",
            "cameras.json",
            CoordinateConvention: null,
            WallStitchStagingTests.WallWidthM,
            WallStitchStagingTests.WallHeightM,
            BuildCurvature(),
            Holds: null,
            HoldsNatural: null,
            carryover,
            Diagnostics: null);

        h.StitchClient.GetJobAsync("sidecar-staging", Arg.Any<CancellationToken>())
            .Returns(new StitchJobState("sidecar-staging", "succeeded", 1.0, "done", null, result));
        h.StitchClient
            .DownloadArtifactAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ci => WriteArtifactAsync(ci.ArgAt<string>(1), ci.ArgAt<Stream>(2)));

        await h.WallStitchService.RefreshJobAsync(job.Id);
        return job;
    }

    /// <summary>The pipeline's curvature block, stored verbatim as JSON on the staged/live wall.</summary>
    private static StitchCurvature BuildCurvature() =>
        new(
            Projection: "cylindrical",
            Default: "gentle",
            RequestedThetaMaxDeg: 30.0,
            ViewDistM: 3.0,
            EyeFrac: 0.5,
            Curves: [new StitchCurveRef("gentle", "natural.png", "display-natural.jpg", 7648, 4310, 12.0, 0.6, 8.0)]);

    private static async Task WriteArtifactAsync(string artifact, Stream destination)
    {
        byte[] payload = artifact switch
        {
            "display-flat.jpg" => [10, 11, 12],
            "display-natural.jpg" => [20, 21, 22],
            "cameras.json" => System.Text.Encoding.UTF8.GetBytes(WallStitchStagingTests.CamerasJson),
            "flat.png" => [1, 1, 1, 1],
            _ => [2, 2, 2, 2],
        };

        await destination.WriteAsync(payload);
    }
}
