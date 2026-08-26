using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Stitching;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers the orchestration seam: admin gating, job persistence, and how a sidecar poll is
/// mirrored onto the <see cref="WallStitchJob"/> row.
/// </summary>
public class WallStitchServiceTests
{
    private const double WallWidthM = 3.5;
    private const double WallHeightM = 4.2;

    private static readonly WallStitchStartOptions Options =
        new(WallWidthM, WallHeightM, WallPhotoProjection.Natural, Natural: "natural", TransferHolds: true);

    [Fact]
    public async Task StartJob_PersistsAQueuedJob_ForAWallAdmin()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 3);
        harness.StitchClient
            .CreateJobAsync(Arg.Any<IReadOnlyList<StitchPhotoUpload>>(), Arg.Any<StitchJobOptions>(), Arg.Any<StitchPhotoUpload?>(), Arg.Any<CancellationToken>())
            .Returns(new StitchJobCreationResult("sidecar-1", "queued"));

        var job = await harness.WallStitchService.StartJobAsync(harness.WallId, harness.Owner.Id, Photos(3), Options);

        Assert.Equal(WallStitchJobStatus.Queued, job.Status);
        Assert.Equal("sidecar-1", job.SidecarJobId);
        Assert.Equal(3, job.PhotoCount);
        Assert.Equal(WallWidthM, job.WallWidthM);
        Assert.Equal(WallHeightM, job.WallHeightM);
        Assert.True(job.TransferHolds);

        await using var db = harness.CreateContext();
        var stored = await db.WallStitchJobs.SingleAsync();
        Assert.Equal(job.Id, stored.Id);
        Assert.Equal(harness.WallId, stored.WallId);
        Assert.Equal(harness.Owner.Id, stored.RequestedByUserId);
    }

    [Fact]
    public async Task StartJob_SendsTheCurrentGenerationHolds_AndTheOldPhoto()
    {
        using var harness = new WallTestHarness();
        var holds = await harness.SeedWallAsync(holdCount: 2);
        StitchJobOptions? sent = null;
        StitchPhotoUpload? oldPhoto = null;
        harness.StitchClient
            .CreateJobAsync(Arg.Any<IReadOnlyList<StitchPhotoUpload>>(), Arg.Do<StitchJobOptions>(o => sent = o), Arg.Do<StitchPhotoUpload?>(p => oldPhoto = p), Arg.Any<CancellationToken>())
            .Returns(new StitchJobCreationResult("sidecar-2", "queued"));

        await harness.WallStitchService.StartJobAsync(harness.WallId, harness.Owner.Id, Photos(2), Options);

        Assert.NotNull(sent);
        Assert.Equal("natural", sent!.Natural);
        Assert.Equal(WallWidthM, sent.WallWidthM);
        Assert.Equal(WallHeightM, sent.WallHeightM);
        Assert.True(sent.TransferHolds);
        Assert.Equal(holds.Count, sent.Holds.Count);
        Assert.NotNull(oldPhoto);
        Assert.Equal("image/jpeg", oldPhoto!.ContentType);
    }

    [Fact]
    public async Task StartJob_IsRejected_ForANonAdminMember()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();
        var member = await harness.AddMemberAsync("member@test", WallRole.Member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            harness.WallStitchService.StartJobAsync(harness.WallId, member.Id, Photos(2), Options));

        await using var db = harness.CreateContext();
        Assert.Empty(await db.WallStitchJobs.ToListAsync());
        await harness.StitchClient.DidNotReceive().CreateJobAsync(
            Arg.Any<IReadOnlyList<StitchPhotoUpload>>(), Arg.Any<StitchJobOptions>(), Arg.Any<StitchPhotoUpload?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartJob_IsRejected_ForAStrangerAndForABadPhotoCount()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            harness.WallStitchService.StartJobAsync(harness.WallId, Guid.NewGuid(), Photos(2), Options));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.WallStitchService.StartJobAsync(harness.WallId, harness.Owner.Id, Photos(1), Options));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.WallStitchService.StartJobAsync(harness.WallId, harness.Owner.Id, Photos(13), Options));
    }

    [Fact]
    public async Task RefreshJob_MirrorsRunningThenSucceeded_OntoTheRow()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();
        harness.StitchClient
            .CreateJobAsync(Arg.Any<IReadOnlyList<StitchPhotoUpload>>(), Arg.Any<StitchJobOptions>(), Arg.Any<StitchPhotoUpload?>(), Arg.Any<CancellationToken>())
            .Returns(new StitchJobCreationResult("sidecar-3", "queued"));
        var job = await harness.WallStitchService.StartJobAsync(harness.WallId, harness.Owner.Id, Photos(4), Options);

        harness.StitchClient.GetJobAsync("sidecar-3", Arg.Any<CancellationToken>())
            .Returns(new StitchJobState("sidecar-3", "running", 0.4, "rectifying", null, null));
        var running = await harness.WallStitchService.RefreshJobAsync(job.Id);

        Assert.Equal(WallStitchJobStatus.Running, running!.Status);
        Assert.Equal(0.4, running.Progress);
        Assert.Equal("rectifying", running.Stage);
        Assert.NotNull(running.StartedAt);
        Assert.Null(running.CompletedAt);

        harness.StitchClient.GetJobAsync("sidecar-3", Arg.Any<CancellationToken>())
            .Returns(new StitchJobState("sidecar-3", "succeeded", 1.0, "done", null, SucceededResult()));
        var done = await harness.WallStitchService.RefreshJobAsync(job.Id);

        Assert.Equal(WallStitchJobStatus.Succeeded, done!.Status);
        Assert.Equal(1.0, done.Progress);
        Assert.NotNull(done.CompletedAt);
        Assert.Contains("straightness", done.DiagnosticsJson);

        await using var db = harness.CreateContext();
        var stored = await db.WallStitchJobs.SingleAsync();
        Assert.Equal(WallStitchJobStatus.Succeeded, stored.Status);
        Assert.NotNull(stored.CompletedAt);
    }

    [Fact]
    public async Task RefreshJob_RecordsFailure_AndStopsPollingAfterwards()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();
        harness.StitchClient
            .CreateJobAsync(Arg.Any<IReadOnlyList<StitchPhotoUpload>>(), Arg.Any<StitchJobOptions>(), Arg.Any<StitchPhotoUpload?>(), Arg.Any<CancellationToken>())
            .Returns(new StitchJobCreationResult("sidecar-4", "queued"));
        var job = await harness.WallStitchService.StartJobAsync(harness.WallId, harness.Owner.Id, Photos(2), Options);

        harness.StitchClient.GetJobAsync("sidecar-4", Arg.Any<CancellationToken>())
            .Returns(new StitchJobState("sidecar-4", "failed", 0.2, "registering", new StitchJobError("too_few_overlaps", "Not enough overlap"), null));

        var failed = await harness.WallStitchService.RefreshJobAsync(job.Id);
        Assert.Equal(WallStitchJobStatus.Failed, failed!.Status);
        Assert.Equal("too_few_overlaps", failed.ErrorCode);
        Assert.Equal("Not enough overlap", failed.ErrorMessage);

        harness.StitchClient.ClearReceivedCalls();
        await harness.WallStitchService.RefreshJobAsync(job.Id);
        await harness.StitchClient.DidNotReceive().GetJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // A failed job has no result to hand out.
        Assert.Null(await harness.WallStitchService.GetResultAsync(job.Id));
    }

    [Fact]
    public async Task ApplyResultToStaging_RejectsAnUnknownJob()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.WallStitchService.ApplyResultToStagingAsync(Guid.NewGuid(), harness.Owner.Id));
    }

    [Fact]
    public void MasterStorage_RejectsTraversalAndRoundTripsACommit()
    {
        var root = Path.Combine(Path.GetTempPath(), "blocwerk-master-guard", Guid.NewGuid().ToString("N"));
        var settings = new BlocwerkSettings();
        settings.WallPhotoMaster.StoragePath = root;
        var storage = new FileSystemWallPhotoMasterStorage(settings);

        try
        {
            var temp = storage.CreateTempPath(".png");
            File.WriteAllBytes(temp, [1, 2, 3, 4]);
            var stored = storage.Commit(temp, ".png");

            var resolved = storage.ResolvePhysicalPath(stored);
            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
            Assert.Equal(Path.GetFullPath(root), Path.GetDirectoryName(resolved));

            Assert.Null(storage.ResolvePhysicalPath("../escaped.png"));
            Assert.Null(storage.ResolvePhysicalPath("../../etc/passwd"));
            Assert.Null(storage.ResolvePhysicalPath("nested/inner.png"));
            Assert.Null(storage.ResolvePhysicalPath(Path.Combine(Path.GetTempPath(), "absolute.png")));
            Assert.Null(storage.ResolvePhysicalPath("   "));

            // Delete goes through the same guard, so a traversal name never removes anything.
            storage.Delete("../escaped.png");
            Assert.True(File.Exists(resolved));

            storage.Delete(stored);
            Assert.False(File.Exists(resolved));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static StitchJobResult SucceededResult() => new(
        new StitchArtifactRef("flat.png", 7648, 4864),
        new StitchArtifactRef("natural.png", 7648, 4310),
        "display-flat.jpg",
        "display-natural.jpg",
        "cameras.json",
        CoordinateConvention: null,
        WallWidthM,
        WallHeightM,
        Curvature: null,
        Holds: null,
        HoldsNatural: null,
        Carryover: new StitchCarryover(
            Generation: 1,
            Counts: null,
            CountsBoulderLinked: null,
            EstimatedPrecision: 0.92,
            Blocker: null,
            Carried: [new StitchCarriedHold(
                Guid.NewGuid(), 0.5, 0.31, 0.011, null, "carried_over", "det-1", 0.2, true, 0, true, null)],
            Missing: [],
            New: []),
        Diagnostics: new StitchDiagnostics(["1.jpeg"], [], null, 0.98, 1200, 1.13, []));

    private static List<StitchPhotoUpload> Photos(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new StitchPhotoUpload($"{i}.jpeg", "image/jpeg", [(byte)i, 1, 2]))
            .ToList();
}
