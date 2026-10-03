// <copyright file="CoverageReportFollowUpStepTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The coverage report step in the post-capture chain: it waits until the capture is done, stores the report on the
/// capture and says how many tips it has, runs again only when the visible volumes (or the photo-real view) changed,
/// and a failure is recorded without stopping the chain.
/// </summary>
public class CoverageReportFollowUpStepTests
{
    [Fact]
    public async Task ItRunsOnceTheCaptureIsDone_StoresTheReport_AndSaysHowManyTips()
    {
        using var h = new WallTestHarness();
        var (captureId, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        var coverage = Counting(h);
        var chain = FollowUpChains.Build(h.RootContextFactory, new CoverageReportFollowUpStep(coverage, h.DbContextFactory));

        await chain.RunAsync(captureId, CaptureFollowUpPhase.Model, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.Final, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        Assert.Equal(0, coverage.Computations);

        await SetDoneAsync(h, captureId);
        var record = await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);

        Assert.Equal(1, coverage.Computations);
        var report = CaptureCoverageReport.Parse(await CoverageJsonAsync(h, captureId));
        Assert.NotNull(report);
        Assert.Equal((captureId, modelId), (report.CaptureId, report.ModelId));
        Assert.NotEmpty(report.Advice);
        Assert.Equal($"{CoverageReportFollowUpStep.Describe(report.Advice.Count)}.", CaptureFollowUpText.Summary(record));
    }

    [Fact]
    public async Task ItRunsAgain_OnlyWhenTheVisibleVolumesChanged()
    {
        using var h = new WallTestHarness();
        var (captureId, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        await SetDoneAsync(h, captureId);
        var coverage = Counting(h);
        var chain = FollowUpChains.Build(h.RootContextFactory, new CoverageReportFollowUpStep(coverage, h.DbContextFactory));

        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        Assert.Equal(1, coverage.Computations);

        await using (var db = h.CreateContext())
        {
            db.WallVolumes.Add(new WallVolume
            {
                WallId = h.WallId, GeometryModelId = modelId, FacetId = "0", Index = 1, FootprintJson = "[]",
                SurfaceJson = CoverageFixtures.Block(1, 500, 500).Surface.ToJson(),
            });
            await db.SaveChangesAsync();
        }

        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        Assert.Equal(2, coverage.Computations);
    }

    [Fact]
    public async Task AFailure_IsRecorded_AndTheOtherStepsStillRun()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        await SetDoneAsync(h, captureId);
        var broken = new CountingCoverageService(Service(h)) { Throws = true };
        var proposals = new FindHoldProposalsFollowUpStep(new FakeHoldProposals { Proposals = 2 }, h.DbContextFactory);

        var record = await FollowUpChains.Build(h.RootContextFactory, new CoverageReportFollowUpStep(broken, h.DbContextFactory), proposals)
            .RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);

        Assert.Equal(CaptureFollowUpOutcome.Failed, record.Find("coverage-report")!.Outcome);
        Assert.Equal(CaptureFollowUpOutcome.Done, record.Find("find-hold-proposals")!.Outcome);
        Assert.Equal("Checking what the capture covered failed.", CaptureFollowUpText.Note(record));
        Assert.Null(await CoverageJsonAsync(h, captureId));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1 tip for the next capture")]
    [InlineData(4, "4 tips for the next capture")]
    public void Describe_SaysHowManyTips(int tips, string expected) =>
        Assert.Equal(expected, CoverageReportFollowUpStep.Describe(tips));

    internal static CaptureCoverageService Service(WallTestHarness h, Abstractions.IKioskContext? kiosk = null) =>
        new(h.DbContextFactory, h.CurrentUser, NullLogger<CaptureCoverageService>.Instance, kiosk);

    internal static async Task SetDoneAsync(WallTestHarness h, Guid captureId)
    {
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        capture.Status = WallCaptureStatus.Succeeded;
        capture.Stage = "Done";
        await db.SaveChangesAsync();
    }

    private static CountingCoverageService Counting(WallTestHarness h) => new(Service(h));

    private static async Task<string?> CoverageJsonAsync(WallTestHarness h, Guid captureId)
    {
        await using var db = h.CreateContext();
        return await db.WallCaptures.Where(c => c.Id == captureId).Select(c => c.CoverageJson).SingleAsync();
    }
}
