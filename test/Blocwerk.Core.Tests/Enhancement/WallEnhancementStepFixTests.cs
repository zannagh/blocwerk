// <copyright file="WallEnhancementStepFixTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Enhancement.Steps;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using NSubstitute;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>The review's fixes in the steps: proposals kept and reused, the search's checkpoint and ETA, every duplicate, one hold's entries.</summary>
public sealed class WallEnhancementStepFixTests
{
    private static readonly Guid Model = Guid.NewGuid();

    [Fact]
    public void ASearch_KeepsEveryPendingProposal_UpdatesTheOneOnTheSameSpotInPlace_AndAddsTheRest()
    {
        var near = Pending(0, 0, 0);
        var untouched = Pending(900, 900, 0);
        var otherModel = Pending(1000, 0, 0, Guid.NewGuid());

        var added = HoldProposalService.Merge([near, untouched, otherModel], [(Candidate(8, 0, 0), null), (Candidate(1000, 0, 0), null)], Guid.NewGuid(), Model);

        Assert.Equal(8, near.X);
        Assert.Equal(900, untouched.X);
        var fresh = Assert.Single(added);
        Assert.Equal((1000.0, Model), (fresh.X, fresh.GeometryModelId));
    }

    [Fact]
    public async Task TheNewHoldsStep_ReusesTheModelsOwnProposalPass_AndOnlySearchesWithoutOne()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var proposal = Pending(0, 0, 0);
        proposal.PanelId = f.PanelId;
        f.Proposals.ListAsync(f.WallId, Arg.Any<HoldProposalStatus>(), Arg.Any<CancellationToken>()).Returns([proposal]);
        var context = new WallEnhancementStepContext(f.WallId, f.Harness.Owner.Id, await new EnhancementTestActors(f).CreateAsync(f.Harness.Owner.Id, default));
        f.Proposals.HasProposalPassAsync(f.WallId, Arg.Any<CancellationToken>()).Returns(true);

        var reused = await new NewHoldsEnhancementStep().RunAsync(context, default);

        await f.Proposals.DidNotReceive().FindInBackgroundAsync(Arg.Any<Guid>(), Arg.Any<IProgress<HoldSearchProgress>?>(), Arg.Any<CancellationToken>());
        Assert.Equal(proposal.Id, Assert.Single(reused.Suggestions).ReferenceId);
        Assert.Contains("own search", reused.Summary);

        f.Proposals.HasProposalPassAsync(f.WallId, Arg.Any<CancellationToken>()).Returns(false);
        await new NewHoldsEnhancementStep().RunAsync(context, default);
        await f.Proposals.Received(1).FindInBackgroundAsync(f.WallId, Arg.Is<IProgress<HoldSearchProgress>?>(p => p != null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ThePhotoCheckpoint_StoresEachPhotosDetections_AndReadsThemBack()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        await using var db = f.Harness.CreateContext();
        await CaptureDetectionStore.SaveAsync(db, "a.jpg", "yolo", [new CaptureDetection("a", 1, 2, 3, 0.9)], default);
        await CaptureDetectionStore.SaveAsync(db, "a.jpg", "yolo", [new CaptureDetection("a", 4, 5, 6, 0.8), new CaptureDetection("a", 7, 8, 9, 0.7)], default);
        await CaptureDetectionStore.SaveAsync(db, "b.jpg", "other", [], default);

        var back = await CaptureDetectionStore.LoadAsync(db, ["a.jpg", "b.jpg", "c.jpg"], "yolo", default);

        Assert.Equal(2, Assert.Single(back).Value.Count);
        Assert.Equal(4, back["a.jpg"][0].Px);
    }

    [Fact]
    public void TheInAppDetector_NamesItsColourFallback_SoACheckpointNeverPassesItOffAsAModelPass()
    {
        var settings = new Configuration.BlocwerkSettings();
        settings.HoldDetection.ModelPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".onnx");
        var detector = new InAppCaptureHoldDetector(Substitute.For<IHoldDetectionService>(), settings);
        Assert.Equal("color-cpu", detector.Name);

        File.WriteAllBytes(settings.HoldDetection.ModelPath, [1]);
        try
        {
            Assert.Equal("yolo-cpu", detector.Name);
        }
        finally
        {
            File.Delete(settings.HoldDetection.ModelPath);
        }
    }

    [Fact]
    public void TheSearchsPace_BecomesAnEta_ThatTheDeployGateReports()
    {
        var board = new DeployBusyEtaBoard();
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var tracker = new WallEnhancementStepTracker(Guid.NewGuid(), board, () => clock);

        tracker.Report(new WallEnhancementProgress(100, 425));
        clock = now.AddSeconds(70);
        tracker.Report(new WallEnhancementProgress(110, 425));

        Assert.Equal(now.AddSeconds(70 + (7 * 315)), tracker.ExpectedEnd);
        Assert.InRange(board.SecondsLeft(now.AddSeconds(70)), 2204, 2206);
        tracker.Clear();
        Assert.Equal(0, board.SecondsLeft(now.AddSeconds(70)));
    }

    [Fact]
    public async Task TheDuplicatesStep_PutsEveryPairUpForReview_PageByPage()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        f.Duplicates.ListAsync(f.WallId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Page(call.ArgAt<int>(1), Math.Min(call.ArgAt<int>(2), 100), 230));
        var context = new WallEnhancementStepContext(f.WallId, f.Harness.Owner.Id, await new EnhancementTestActors(f).CreateAsync(f.Harness.Owner.Id, default));

        var outcome = await new DuplicatesEnhancementStep().RunAsync(context, default);

        Assert.Equal(230, outcome.Suggestions.Count);
        Assert.Equal(230, outcome.Suggestions.Select(s => s.HoldId).Distinct().Count());
    }

    [Fact]
    public async Task OneHoldsJournalEntries_AreReadWithoutTheRest()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var (a, b) = (await f.AddHoldAsync(0.2, 0.2), await f.AddHoldAsync(0.6, 0.6));
        await f.StartAndWaitAsync(f.Service(FakeEnhancementStep.Writing(f, "outlines", 100, WallEnhancementItemKind.Shape, [a, b], h => h.Radius = 0.05)));
        var batches = (await f.BatchesAsync()).Where(x => x.Label == "wall-enhance:outlines").Select(x => x.Id).ToList();
        await using var db = f.Harness.CreateContext();

        var only = await WallEnhancementJournal.HoldUpdatesAsync(db, batches, default, b);

        Assert.Equal(b, Assert.Single(only).HoldId);
        Assert.Equal(2, (await WallEnhancementJournal.HoldUpdatesAsync(db, batches, default)).Count);
    }

    private static HoldProposal Pending(double x, double y, double z, Guid? model = null) => new()
    {
        WallId = Guid.NewGuid(), GeometryModelId = model ?? Model, FacetId = "f", BestPhoto = "p", X = x, Y = y, Z = z, SizeMm = 40,
        Status = HoldProposalStatus.Pending,
    };

    private static HoldProposalCandidate Candidate(double x, double y, double z) =>
        new("f", 0, 0, 0, [x, y, z], 40, 3, 0.9, 1, new CaptureDetection("p", 1, 1, 5, 0.9), []);

    private static HoldDuplicatePage Page(int skip, int take, int total)
    {
        var items = Enumerable.Range(skip, Math.Max(0, Math.Min(take, total - skip)))
            .Select(i => new HoldDuplicateItem(
                HoldDuplicateKind.InsideHandPlaced, 0.9,
                new HoldDuplicateHoldInfo(new Guid(i + 1, 0, 0, new byte[8]), null, null, false, false, 0, []),
                new HoldDuplicateHoldInfo(Guid.NewGuid(), null, null, false, false, 0, []),
                HoldMergeMode.KeepLeft,
                0))
            .ToList();
        return new HoldDuplicatePage(total, new Dictionary<HoldDuplicateKind, int>(), items, []);
    }
}
