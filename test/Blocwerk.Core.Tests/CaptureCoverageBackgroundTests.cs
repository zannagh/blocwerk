// <copyright file="CaptureCoverageBackgroundTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.Coverage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A missing coverage report is never computed on the page load: the read answers at once ("being computed"), the
/// computation runs once per capture in the background, and a failed one is left alone for a while.
/// </summary>
public class CaptureCoverageBackgroundTests
{
    [Fact]
    public async Task ReadingAMissingReport_AnswersAtOnce_AndComputesItInTheBackground()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        await CoverageReportFollowUpStepTests.SetDoneAsync(h, captureId);
        var service = CoverageReportFollowUpStepTests.Service(h);

        var first = await service.GetAsync(h.WallId, captureId);
        var again = await service.GetAsync(h.WallId, captureId);

        Assert.True(first.CaptureFound);
        Assert.Null(first.Report);
        Assert.True(first.Computing);
        Assert.Null(again.Report);
        Assert.True(again.Computing || again.Report is not null);
        await (CoverageBackgroundCompute.Pending(captureId) ?? Task.CompletedTask);

        var stored = await service.GetAsync(h.WallId, captureId);
        Assert.NotNull(stored.Report);
        Assert.False(stored.Computing);
        Assert.NotNull(CaptureCoverageReport.Parse(await CoverageJsonAsync(h, captureId)));
    }

    [Fact]
    public async Task ACaptureThatIsNotDone_IsNotComputed()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);

        var lookup = await CoverageReportFollowUpStepTests.Service(h).GetAsync(h.WallId, captureId);

        Assert.True(lookup.CaptureFound);
        Assert.Null(lookup.Report);
        Assert.False(lookup.Computing);
        Assert.Null(CoverageBackgroundCompute.Pending(captureId));
    }

    [Fact]
    public async Task ManyAsksForTheSameCapture_StartOneComputation()
    {
        var captureId = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        var runs = 0;

        var started = Enumerable.Range(0, 5).Select(_ => CoverageBackgroundCompute.Ensure(
            captureId,
            async () =>
            {
                Interlocked.Increment(ref runs);
                await gate.Task;
            },
            NullLogger.Instance)).ToList();
        var pending = CoverageBackgroundCompute.Pending(captureId);
        gate.SetResult();
        await pending!;

        Assert.All(started, Assert.True);
        Assert.Equal(1, runs);
        Assert.Null(CoverageBackgroundCompute.Pending(captureId));
    }

    [Fact]
    public async Task AFailedComputation_IsNotStartedAgainStraightAway()
    {
        var captureId = Guid.NewGuid();

        Assert.True(CoverageBackgroundCompute.Ensure(captureId, () => throw new InvalidOperationException("boom"), NullLogger.Instance));
        await (CoverageBackgroundCompute.Pending(captureId) ?? Task.CompletedTask);
        await Task.Delay(50);

        Assert.False(CoverageBackgroundCompute.Ensure(captureId, () => Task.CompletedTask, NullLogger.Instance));
    }

    private static async Task<string?> CoverageJsonAsync(WallTestHarness h, Guid captureId)
    {
        await using var db = h.CreateContext();
        return await db.WallCaptures.Where(c => c.Id == captureId).Select(c => c.CoverageJson).SingleAsync();
    }
}
