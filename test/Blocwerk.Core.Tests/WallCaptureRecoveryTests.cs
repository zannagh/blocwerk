using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// An app restart must never leave a capture "running" forever: the worker re-enqueues what the
/// previous process left in flight, the processor resumes from the recorded job id (or re-submits
/// when the worker forgot it), and a capture that keeps dying is failed after its attempt budget.
/// </summary>
public class WallCaptureRecoveryTests
{
    [Fact]
    public async Task Recover_ReenqueuesInFlightCaptures_AndResumesTheRecordedSolveJob()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        var jobId = s.Client.Adopt("solve");
        await SimulateCrashAsync(h, captureId, WallCaptureStatus.Solving, solveJobId: jobId);

        var worker = Worker(s);
        await worker.RecoverAsync(CancellationToken.None);
        var dequeued = await s.Queue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal(captureId, dequeued);
        await s.Processor.ProcessAsync(dequeued, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.Equal(jobId, capture.SolveJobId);
        Assert.Empty(s.Client.JsonSubmissions); // resumed, not re-submitted
        Assert.True((await db.WallGeometryModels.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task ResumedJobTheWorkerForgot_IsSubmittedAgain()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        var lost = s.Client.Adopt("solve");
        s.Client.Forgotten.Add(lost);
        await SimulateCrashAsync(h, captureId, WallCaptureStatus.Solving, solveJobId: lost);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
        Assert.NotEqual(lost, capture.SolveJobId);
        Assert.Single(s.Client.JsonSubmissions);
    }

    [Fact]
    public async Task CaptureThatKeepsDying_IsFailedAfterItsAttemptBudget()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        await SimulateCrashAsync(h, captureId, WallCaptureStatus.Texturing, solveJobId: null, attempts: s.Options.MaxAttempts);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Failed, capture.Status);
        Assert.Contains("interrupted", capture.Error);
    }

    [Fact]
    public async Task CreatorWhoLostAdminRights_StopsTheCapture()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        await h.SeedWallAsync(holdCount: 0);
        var admin = await h.AddMemberAsync("admin@test", WallRole.Admin);
        h.ActingUser = admin;
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        await s.Service.AddPhotoAsync(draft.CaptureId, "a.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(1)), CancellationToken.None);
        await s.Service.AddPhotoAsync(draft.CaptureId, "b.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(2)), CancellationToken.None);
        Assert.Empty(await s.Service.StartAsync(draft.CaptureId, await s.Service.SuggestDeclarationsAsync(draft.CaptureId), null));
        await using (var db = h.CreateContext())
        {
            var member = await db.WallMembers.SingleAsync(m => m.UserId == admin.Id);
            member.Role = WallRole.Member;
            await db.SaveChangesAsync();
        }

        await s.Processor.ProcessAsync(draft.CaptureId, CancellationToken.None);

        await using var read = h.CreateContext();
        var capture = await read.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Failed, capture.Status);
        Assert.Contains("no longer an admin", capture.Error);
        Assert.Empty(s.Client.JsonSubmissions);
    }

    [Fact]
    public async Task Recover_SweepsStaleDrafts_AndTheirFiles()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        await s.Service.AddPhotoAsync(draft.CaptureId, "a.jpg", CaptureScenario.TinyJpeg(), CancellationToken.None);
        string stored;
        await using (var db = h.CreateContext())
        {
            stored = (await db.WallCapturePhotos.SingleAsync()).StoredPath;
            var capture = await db.WallCaptures.SingleAsync();
            capture.CreatedAt = DateTimeOffset.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
        }

        await Worker(s).RecoverAsync(CancellationToken.None);

        await using var read = h.CreateContext();
        Assert.Empty(await read.WallCaptures.ToListAsync());
        Assert.False(File.Exists(s.Files.ResolvePhysicalPath(stored)));
    }

    private static WallCaptureWorker Worker(CaptureScenario s) => new(
        s.Harness.RootContextFactory, s.Queue, s.Processor, s.Files, s.Options, NullLogger<WallCaptureWorker>.Instance);

    /// <summary>Leaves the row as a process killed mid-stage would.</summary>
    private static async Task SimulateCrashAsync(
        WallTestHarness h, Guid captureId, WallCaptureStatus status, string? solveJobId, int attempts = 1)
    {
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        capture.Status = status;
        capture.SolveJobId = solveJobId;
        capture.Attempts = attempts;
        await db.SaveChangesAsync();
    }
}
