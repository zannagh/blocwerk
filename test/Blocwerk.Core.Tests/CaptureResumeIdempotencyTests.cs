using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A crash between "model imported and activated" and "model id recorded on the capture" must not
/// import (and activate, and invalidate) a second model on resume; and a clean shutdown — a deploy —
/// must not eat into the capture's attempt budget.
/// </summary>
public class CaptureResumeIdempotencyTests
{
    [Fact]
    public async Task CrashAfterImport_ResumesFromTheImportedModel_InsteadOfImportingAgain()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        Guid modelId;
        await using (var db = h.CreateContext())
        {
            modelId = (await db.WallGeometryModels.SingleAsync()).Id;
            var capture = await db.WallCaptures.SingleAsync();
            capture.Status = WallCaptureStatus.Solving;
            capture.GeometryModelId = null;
            capture.TexturesJobId = null;
            capture.Attempts = 1;
            await db.SaveChangesAsync();
        }

        var solveSubmissions = s.Client.JsonSubmissions.Count;
        s.Client.Forgotten.Add((await ReadAsync(h)).SolveJobId!);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var read = h.CreateContext();
        var model = await read.WallGeometryModels.SingleAsync();
        Assert.Equal(modelId, model.Id);
        Assert.True(model.IsActive);
        Assert.Equal($"capture {captureId:N}", model.Source);
        var resumed = await read.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Succeeded, resumed.Status);
        Assert.Equal(modelId, resumed.GeometryModelId);
        Assert.Equal(solveSubmissions, s.Client.JsonSubmissions.Count);
    }

    [Fact]
    public async Task CleanShutdown_MidSolve_DoesNotCountAsAnAttempt()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        s.Client.NeverFinish.Add("solve");
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Processor.ProcessAsync(captureId, shutdown.Token));

        var capture = await ReadAsync(h);
        Assert.Equal(0, capture.Attempts);
        Assert.Equal(WallCaptureStatus.Solving, capture.Status);
        Assert.NotNull(capture.SolveJobId);
    }

    [Fact]
    public async Task ManyDeploysDuringOneSolve_NeverFailTheCapture()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        s.Client.NeverFinish.Add("solve");
        for (var deploy = 0; deploy < s.Options.MaxAttempts + 2; deploy++)
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Processor.ProcessAsync(captureId, shutdown.Token));
        }

        s.Client.NeverFinish.Clear();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.Equal(WallCaptureStatus.Succeeded, (await ReadAsync(h)).Status);
    }

    private static async Task<WallCapture> ReadAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallCaptures.AsNoTracking().SingleAsync();
    }
}
