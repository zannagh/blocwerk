// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Re-solve and re-render runs count as started until they end: a graceful shutdown takes the count back, and a mark
/// whose runs keep not ending is dropped with a note instead of being queued again forever.
/// </summary>
public class CaptureRedoStartsTests
{
    [Theory]
    [InlineData(CaptureRedoKind.Resolve, 2, true)]
    [InlineData(CaptureRedoKind.Resolve, 3, false)]
    [InlineData(CaptureRedoKind.Rerender, 2, true)]
    [InlineData(CaptureRedoKind.Rerender, 3, false)]
    public async Task AMarkWhoseRunsKeepNotEnding_IsDroppedWithANote(CaptureRedoKind kind, int starts, bool queued)
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await FinishedAsync(s);
        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
            var record = CaptureFollowUpRecord.Parse(capture.FollowUpJson);
            if (kind == CaptureRedoKind.Resolve)
            {
                capture.SolveJobId = CaptureResolveMark.Mark + "job";
                capture.FollowUpJson = (record with { ResolveStarts = starts }).ToJson();
            }
            else
            {
                capture.TexturesJobId = CaptureTextureOutcome.RerenderMark + "job";
                capture.FollowUpJson = (record with { RerenderStarts = starts }).ToJson();
            }

            await db.SaveChangesAsync();
        }

        var requeued = kind == CaptureRedoKind.Resolve
            ? await new WallModelResolveWorker(s.ResolveQueue, s.Processor, h.RootContextFactory, NullLogger<WallModelResolveWorker>.Instance)
                .RequeueStuckAsync(CancellationToken.None)
            : await new WallTextureRerenderWorker(s.TextureQueue, s.Processor, h.RootContextFactory, NullLogger<WallTextureRerenderWorker>.Instance)
                .RequeueStuckAsync(CancellationToken.None);

        Assert.Equal(queued ? 1 : 0, requeued);
        await using var read = h.CreateContext();
        Assert.Equal(queued, await CaptureRedoMarks.AnyOnWallAsync(read, h.WallId));
        if (!queued)
        {
            Assert.Contains("did not finish in several tries", (await s.Service.GetCaptureAsync(captureId))!.FollowUpNote);
        }
    }

    [Fact]
    public async Task AReSolveRun_CountsWhileItRuns_AndAGracefulShutdownTakesItBack()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await FinishedAsync(s);
        s.Client.NeverFinish.Add("solve");
        Assert.Empty(await s.Service.ResolveModelAsync(captureId));

        using var shutdown = new CancellationTokenSource();
        var run = Task.Run(() => s.Processor.ResolveModelAsync(captureId, shutdown.Token));
        for (var i = 0; i < 400 && (await RecordAsync(h, captureId)).ResolveStarts == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, (await RecordAsync(h, captureId)).ResolveStarts);
        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(0, (await RecordAsync(h, captureId)).ResolveStarts);
        await using var db = h.CreateContext();
        Assert.True(await CaptureRedoMarks.AnyOnWallAsync(db, h.WallId));
    }

    private static async Task<Guid> FinishedAsync(CaptureScenario s)
    {
        var captureId = await s.StartCaptureAsync();
        Assert.Equal(captureId, await s.Queue.DequeueAsync(CancellationToken.None));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        return captureId;
    }

    private static async Task<CaptureFollowUpRecord> RecordAsync(WallTestHarness h, Guid captureId)
    {
        await using var db = h.CreateContext();
        return CaptureFollowUpRecord.Parse(await db.WallCaptures.Where(c => c.Id == captureId).Select(c => c.FollowUpJson).SingleAsync());
    }
}
