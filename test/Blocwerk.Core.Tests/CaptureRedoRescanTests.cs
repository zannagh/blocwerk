// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A re-solve or re-render mark no run clears (its run failed before it could) is queued again by the idle workers, so
/// the wall does not stay busy until a restart; a mark a live run works on is left alone, and a second run of the same
/// capture never starts beside it.
/// </summary>
public class CaptureRedoRescanTests
{
    [Fact]
    public async Task AStuckRerenderMark_IsQueuedAgain_AndItsRunClearsIt()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await FinishedAsync(s);
        await MarkAsync(h, captureId, solve: null, textures: CaptureTextureOutcome.RerenderMark);

        Assert.Equal(1, await RerenderWorker(s).RequeueStuckAsync(CancellationToken.None));
        Assert.Equal(captureId, await s.TextureQueue.DequeueAsync(Timeout()));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        Assert.False(await CaptureRedoMarks.AnyOnWallAsync(db, h.WallId));
    }

    [Fact]
    public async Task AStuckResolveMark_IsQueuedAgain_AndItsRunFreesTheWall()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await FinishedAsync(s);
        await MarkAsync(h, captureId, solve: CaptureResolveMark.Mark + "lost-job", textures: null);
        await using (var db = h.CreateContext())
        {
            (await db.WallGeometryModels.SingleAsync(m => m.IsActive)).IsActive = false;
            await db.SaveChangesAsync();
        }

        var worker = new WallModelResolveWorker(s.ResolveQueue, s.Processor, h.RootContextFactory, NullLogger<WallModelResolveWorker>.Instance);
        Assert.Equal(1, await worker.RequeueStuckAsync(CancellationToken.None));
        Assert.Equal(captureId, await s.ResolveQueue.DequeueAsync(Timeout()));
        await s.Processor.ResolveModelAsync(captureId, CancellationToken.None);

        await using var read = h.CreateContext();
        Assert.False(await CaptureRedoMarks.AnyOnWallAsync(read, h.WallId));
    }

    [Fact]
    public async Task AMarkALiveRunWorksOn_IsNotQueuedAgain_AndASecondRunDoesNothing()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await FinishedAsync(s);
        s.Client.NeverFinish.Add("textures");
        await MarkAsync(h, captureId, solve: null, textures: CaptureTextureOutcome.RerenderMark);
        var texturesSubmitted = s.Client.JsonSubmissions.Count + s.Client.MultipartSubmissions.Count;

        using var stop = new CancellationTokenSource();
        var live = Task.Run(() => s.Processor.RerenderTexturesAsync(captureId, stop.Token));
        await WaitAsync(() => s.Client.JsonSubmissions.Count + s.Client.MultipartSubmissions.Count > texturesSubmitted);

        Assert.Equal(0, await RerenderWorker(s).RequeueStuckAsync(CancellationToken.None));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);
        Assert.Equal(texturesSubmitted + 1, s.Client.JsonSubmissions.Count + s.Client.MultipartSubmissions.Count);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live);
    }

    private static WallTextureRerenderWorker RerenderWorker(CaptureScenario s) =>
        new(s.TextureQueue, s.Processor, s.Harness.RootContextFactory, NullLogger<WallTextureRerenderWorker>.Instance);

    private static async Task<Guid> FinishedAsync(CaptureScenario s)
    {
        var captureId = await s.StartCaptureAsync();
        Assert.Equal(captureId, await s.Queue.DequeueAsync(CancellationToken.None));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        return captureId;
    }

    private static async Task MarkAsync(WallTestHarness h, Guid captureId, string? solve, string? textures)
    {
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        capture.SolveJobId = solve;
        capture.TexturesJobId = textures;
        await db.SaveChangesAsync();
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(done(), "the live run never submitted its job");
    }
}
