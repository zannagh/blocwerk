// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Capture.FollowUp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Startup recovery of the follow-up marks is counted: a chain that keeps bringing the process down is dropped with a
/// note after <see cref="CaptureFollowUpChain.MaxRecoveries"/> starts in a row, and finishing resets the count.
/// </summary>
public class CaptureFollowUpRecoveryTests
{
    [Fact]
    public async Task ACorrectionChain_IsRecoveredAndCounted_UntilItIsDroppedWithANote()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        await SetAsync(h, captureId, CaptureFollowUpRecord.Repointed(null));
        var queue = new CorrectionFollowUpQueue();
        var chain = FollowUpChains.Build(h.RootContextFactory, new ScriptedFollowUpStep("place", 100, []));
        var worker = new CorrectionFollowUpWorker(queue, chain, h.RootContextFactory, NullLogger<CorrectionFollowUpWorker>.Instance);

        // The process "dies" at the step's write each time (write 1 counts the start): the run started, but never ended.
        var writes = 0;
        chain.BeforeConditionalWrite = () => ++writes == 2 ? throw new InvalidOperationException("the process died") : Task.CompletedTask;
        for (var start = 1; start <= CaptureFollowUpChain.MaxRecoveries; start++)
        {
            await worker.RecoverAsync(default);
            Assert.Equal(start - 1, (await RecordAsync(h, captureId)).Recoveries); // queueing does not count
            writes = 0;
            var queued = await queue.DequeueAsync(Timeout());
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync(queued, default));
            Assert.Equal(start, (await RecordAsync(h, captureId)).Recoveries);
        }

        await worker.RecoverAsync(default);

        var dropped = await RecordAsync(h, captureId);
        Assert.False(dropped.RunAgain);
        Assert.Equal(0, dropped.Recoveries);
        Assert.Contains("restarts in a row", dropped.Note);
        await worker.RecoverAsync(default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await queue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(100)).Token));
    }

    [Fact]
    public async Task AGracefulShutdownMidChain_DoesNotCount()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        await SetAsync(h, captureId, CaptureFollowUpRecord.Repointed(null));
        using var shutdown = new CancellationTokenSource();
        var step = new ScriptedFollowUpStep("place", 100, [])
        {
            Run = (_, ct) =>
            {
                shutdown.Cancel();
                ct.ThrowIfCancellationRequested();
                return CaptureFollowUpStepResult.Done("never");
            },
        };
        var chain = FollowUpChains.Build(h.RootContextFactory, step);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chain.RunAgainAsync(captureId, shutdown.Token));

        var record = await RecordAsync(h, captureId);
        Assert.True(record.RunAgain);
        Assert.Equal(0, record.Recoveries);
    }

    [Fact]
    public async Task FinishingTheChain_ResetsTheCount()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        await SetAsync(h, captureId, CaptureFollowUpRecord.Repointed(null) with { Recoveries = 2 });
        var queue = new CorrectionFollowUpQueue();
        var chain = FollowUpChains.Build(h.RootContextFactory, new ScriptedFollowUpStep("place", 100, []));
        var worker = new CorrectionFollowUpWorker(queue, chain, h.RootContextFactory, NullLogger<CorrectionFollowUpWorker>.Instance);

        await worker.RecoverAsync(default);
        await worker.RunAsync(await queue.DequeueAsync(Timeout()), default);

        var record = await RecordAsync(h, captureId);
        Assert.False(record.RunAgain);
        Assert.Equal(0, record.Recoveries);
    }

    [Fact]
    public async Task ARederiveMark_IsDroppedWithANote_AfterTooManyStarts()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        var chain = FollowUpChains.Build(h.RootContextFactory, new ScriptedFollowUpStep("place", 100, []));
        await SetAsync(h, captureId, CaptureFollowUpRecord.Empty with { Rederive = true, Recoveries = CaptureFollowUpChain.MaxRecoveries });
        using var worker = new WallModelResolveWorker(
            s.ResolveQueue, s.Processor, h.RootContextFactory, NullLogger<WallModelResolveWorker>.Instance, chain);

        await worker.StartAsync(CancellationToken.None);
        CaptureFollowUpRecord record = await RecordAsync(h, captureId);
        for (var i = 0; i < 200 && record.Rederive; i++)
        {
            await Task.Delay(10);
            record = await RecordAsync(h, captureId);
        }

        await worker.StopAsync(CancellationToken.None);
        Assert.False(record.Rederive);
        Assert.Contains("restarts in a row", record.Note);
    }

    private static async Task SetAsync(WallTestHarness h, Guid captureId, CaptureFollowUpRecord record)
    {
        await using var db = h.CreateContext();
        (await db.WallCaptures.SingleAsync(c => c.Id == captureId)).FollowUpJson = record.ToJson();
        await db.SaveChangesAsync();
    }

    private static async Task<CaptureFollowUpRecord> RecordAsync(WallTestHarness h, Guid captureId)
    {
        await using var db = h.CreateContext();
        return CaptureFollowUpRecord.Parse(await db.WallCaptures.Where(c => c.Id == captureId).Select(c => c.FollowUpJson).SingleAsync());
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;
}
