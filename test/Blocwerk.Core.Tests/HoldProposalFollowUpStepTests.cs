// <copyright file="HoldProposalFollowUpStepTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The "find hold proposals" step in the chain: it waits until the capture is done (never in the model or final
/// phase), says how many holds it proposes, does not search again for a new photo-real view unless the visible
/// volumes changed, and a failure is recorded without touching the other steps.
/// </summary>
public class HoldProposalFollowUpStepTests
{
    [Fact]
    public async Task ItRunsOnlyOnceTheCaptureIsDone_AndSaysWhatItProposed()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        var proposals = new FakeHoldProposals { Proposals = 9 };
        var log = new List<string>();
        var chain = FollowUpChains.Build(
            h.RootContextFactory, new ScriptedFollowUpStep("place", 100, log), new FindHoldProposalsFollowUpStep(proposals, h.DbContextFactory));

        await chain.RunAsync(captureId, CaptureFollowUpPhase.Model, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.Final, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        Assert.Equal(0, proposals.Calls);

        await SetStatusAsync(h, captureId, WallCaptureStatus.Succeeded, "Done");
        var record = await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);

        Assert.Equal(1, proposals.Calls);
        Assert.Equal(["place"], log);
        Assert.Equal(["place", "find-hold-proposals"], record.Steps.Select(s => s.Key));
        Assert.Equal("9 possible new holds to review.", CaptureFollowUpText.Summary(record));
        await using var db = h.CreateContext();
        Assert.Equal("Done", await db.WallCaptures.Where(c => c.Id == captureId).Select(c => c.Stage).SingleAsync());
    }

    [Fact]
    public async Task ANewPhotoRealView_DoesNotSearchAgain_UnlessTheVisibleVolumesChanged()
    {
        using var h = new WallTestHarness();
        var (captureId, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        await SetStatusAsync(h, captureId, WallCaptureStatus.Succeeded, "Done");
        var proposals = new FakeHoldProposals { Proposals = 3 };
        var log = new List<string>();
        var chain = FollowUpChains.Build(
            h.RootContextFactory,
            new ScriptedFollowUpStep("volumes", 250, log, needsPhotoReal: true),
            new FindHoldProposalsFollowUpStep(proposals, h.DbContextFactory));
        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        Assert.Equal(1, proposals.Calls);

        // A runner delivers the view: the photo-real steps run again, the (unchanged) volumes ask for no new search.
        await AddSplatAsync(h, modelId);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.Final, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        Assert.Equal(["volumes"], log);
        Assert.Equal(1, proposals.Calls);

        var volumeId = await AddVolumeAsync(h, modelId);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        Assert.Equal(2, proposals.Calls);

        await using (var db = h.CreateContext())
        {
            (await db.WallVolumes.SingleAsync(v => v.Id == volumeId)).IsHidden = true;
            await db.SaveChangesAsync();
        }

        await chain.RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);
        Assert.Equal(3, proposals.Calls);
    }

    [Fact]
    public async Task AFailedSearch_IsRecorded_AndChangesNothingElse()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        await SetStatusAsync(h, captureId, WallCaptureStatus.Succeeded, "Done");
        var log = new List<string>();
        var place = new ScriptedFollowUpStep("place", 100, log) { Summary = "3 holds placed on the 3D model" };
        await FollowUpChains.Build(h.RootContextFactory, place).RunAsync(captureId, CaptureFollowUpPhase.Model, default);
        var broken = new FakeHoldProposals { Throws = true };

        var record = await FollowUpChains.Build(h.RootContextFactory, place, new FindHoldProposalsFollowUpStep(broken, h.DbContextFactory))
            .RunAsync(captureId, CaptureFollowUpPhase.AfterCompletion, default);

        Assert.Equal(CaptureFollowUpOutcome.Failed, record.Find("find-hold-proposals")!.Outcome);
        Assert.Equal("3 holds placed on the 3D model.", CaptureFollowUpText.Summary(record));
        Assert.Equal("Finding new holds in the capture photos failed.", CaptureFollowUpText.Note(record));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1 possible new hold to review")]
    [InlineData(9, "9 possible new holds to review")]
    public void Describe_SaysHowManyHoldsWaitForReview(int proposals, string expected) =>
        Assert.Equal(expected, FindHoldProposalsFollowUpStep.Describe(proposals));

    private static async Task SetStatusAsync(WallTestHarness h, Guid captureId, WallCaptureStatus status, string stage)
    {
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        capture.Status = status;
        capture.Stage = stage;
        await db.SaveChangesAsync();
    }

    private static async Task AddSplatAsync(WallTestHarness h, Guid modelId)
    {
        await using var db = h.CreateContext();
        db.WallGeometrySplats.Add(new WallGeometrySplat { GeometryModelId = modelId, StoredPath = "s.spz", SizeBytes = 1, FrameJson = "{}" });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddVolumeAsync(WallTestHarness h, Guid modelId)
    {
        await using var db = h.CreateContext();
        var volume = new WallVolume
        {
            WallId = h.WallId, GeometryModelId = modelId, FacetId = "f1", FootprintJson = "[]", SurfaceJson = "{\"cells\":[1]}",
        };
        db.WallVolumes.Add(volume);
        await db.SaveChangesAsync();
        return volume.Id;
    }
}
