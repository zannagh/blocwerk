// <copyright file="RedetectSafetyTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Redetect;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.HoldColors;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Redetect;

/// <summary>
/// What may be accepted together, what is shown as "the detector is unsure", and that applying writes only what a
/// card showed.
/// </summary>
public class RedetectSafetyTests
{
    private static readonly HoldSamenessOptions Options = new();

    [Fact]
    public void AnOutlineForACircle_ThatTakesInANeighbouringHold_IsNotAcceptedTogether_AndIsMarkedUnsure()
    {
        var placed = new Hold { X = 0.5, Y = 0.5, Radius = 0.03, IsAutoDetected = false };
        var neighbour = Outlined(0.518, 0.5, 0.004, 0.004);
        var detection = Detected(0.5, 0.5, 0.024, 0.024);

        var alone = Shape(Input([placed], [detection]), placed);
        var crowded = Shape(Input([placed, neighbour], [detection]), placed);

        Assert.True(alone.Safe);
        Assert.False(alone.Unsure);
        Assert.False(crowded.Safe);
        Assert.True(crowded.Unsure);
    }

    [Fact]
    public void AnOutlineThatDropsPartOfTheStoredOne_IsNotAcceptedTogether_AndFromAThirdItIsMarkedUnsure()
    {
        var stored = Outlined(0.5, 0.5, 0.03, 0.03);

        var same = Shape(Input([stored], [Detected(0.504, 0.5, 0.03, 0.03)]), stored);
        var smaller = Shape(Input([stored], [Detected(0.5, 0.5, 0.026, 0.03)]), stored);
        var inset = Shape(Input([stored], [Detected(0.5, 0.5, 0.019, 0.03)]), stored);

        Assert.True(same.Safe);
        Assert.False(smaller.Safe);
        Assert.False(smaller.Unsure);
        Assert.False(inset.Safe);
        Assert.True(inset.Unsure);
    }

    [Fact]
    public void AnOutlineThatAddsMoreThanALittle_IsNotAcceptedTogether_AndMuchMoreIsMarkedUnsure()
    {
        var stored = Outlined(0.5, 0.5, 0.03, 0.03);

        var larger = Shape(Input([stored], [Detected(0.5, 0.5, 0.036, 0.03)]), stored);
        var muchLarger = Shape(Input([stored], [Detected(0.5, 0.5, 0.05, 0.03)]), stored);

        Assert.False(larger.Safe);
        Assert.False(larger.Unsure);
        Assert.True(muchLarger.Unsure);
    }

    [Fact]
    public void AHoldThatBouldersUse_IsAcceptedTogether_OnlyWhenItsOutlineHardlyChanges()
    {
        var stored = Outlined(0.5, 0.5, 0.03, 0.03);
        RedetectInput Used(RedetectDetection detection) => new(
            [new RedetectExisting(stored, new HashSet<Guid> { Guid.NewGuid() }, 1, false)], [detection], new HashSet<(Guid, Guid)>(), CanMerge: true);

        Assert.True(Shape(Used(Detected(0.504, 0.5, 0.03, 0.03)), stored).Safe);
        Assert.False(Shape(Used(Detected(0.5055, 0.5, 0.0315, 0.03)), stored).Safe);
        Assert.True(Shape(Input([stored], [Detected(0.5055, 0.5, 0.0315, 0.03)]), stored).Safe);
    }

    [Fact]
    public void AnOutlineBeyondTheDetectorBox_IsNotAcceptedTogether()
    {
        var placed = new Hold { X = 0.5, Y = 0.5, Radius = 0.03, IsAutoDetected = false };
        var inside = Detected(0.5, 0.5, 0.024, 0.024);
        inside.Shape.DetectedBox = new HoldDetectedBox(0.05, 0.05);
        var beyond = Detected(0.5, 0.5, 0.024, 0.024);
        beyond.Shape.DetectedBox = new HoldDetectedBox(0.03, 0.03);

        Assert.True(Shape(Input([placed], [inside]), placed).Safe);
        Assert.False(Shape(Input([placed], [beyond]), placed).Safe);
    }

    [Fact]
    public void AnOutlineForACircle_ThatReachesClearlyBeyondTheCircle_OrFromADetectorThatIsNotSure_IsNotAcceptedTogether()
    {
        var placed = new Hold { X = 0.5, Y = 0.5, Radius = 0.03, IsAutoDetected = false };
        var larger = Detected(0.5, 0.5, 0.036, 0.03);
        var guessed = Detected(0.5, 0.5, 0.024, 0.024);
        guessed.Shape.Confidence = 0.8;
        var pale = Detected(0.5, 0.5, 0.024, 0.024);
        pale.Shape.OutlineDistinct = false;

        Assert.False(Shape(Input([placed], [larger]), placed).Safe);
        Assert.False(Shape(Input([placed], [guessed]), placed).Safe);
        Assert.False(Shape(Input([placed], [pale]), placed).Safe);
    }

    [Fact]
    public void AnOutlineThatFillsANotchOfTheStoredOne_IsNotAcceptedTogether()
    {
        // The stored outline has a notch in its top edge; the detected one is the plain square over it.
        var stored = Outlined(0.5, 0.5, 0.03, 0.03);
        stored.ShapePoints =
        [
            new() { Dx = -0.03, Dy = -0.03 }, new() { Dx = -0.012, Dy = -0.03 }, new() { Dx = 0, Dy = -0.006 }, new() { Dx = 0.012, Dy = -0.03 },
            new() { Dx = 0.03, Dy = -0.03 }, new() { Dx = 0.03, Dy = 0.03 }, new() { Dx = -0.03, Dy = 0.03 },
        ];
        var bridged = Shape(Input([stored], [Detected(0.502, 0.5, 0.03, 0.03)]), stored);

        Assert.False(bridged.Safe);
        Assert.False(bridged.Unsure);
    }

    [Fact]
    public void ASplitWhosePiecesLeaveOutPartOfTheHold_IsMarkedUnsure_AndOneThatCoversItIsNot()
    {
        var stored = Outlined(0.5, 0.5, 0.04, 0.02);
        Hold Piece(double x, double halfWidth) => Outlined(x, 0.5, halfWidth, 0.02);

        Assert.Equal(RedetectDoubt.None, RedetectSafety.CheckSplit(stored, [Piece(0.48, 0.019), Piece(0.52, 0.019)]));
        Assert.Equal(RedetectDoubt.PiecesMissPart, RedetectSafety.CheckSplit(stored, [Piece(0.47, 0.01), Piece(0.52, 0.012)]));
    }

    [Fact]
    public void AnOutlineForACircle_OnAPaleHold_IsNotAcceptedTogether_ButNotMarkedUnsure()
    {
        var placed = new Hold { X = 0.5, Y = 0.5, Radius = 0.03, IsAutoDetected = false };
        var pale = Detected(0.5, 0.5, 0.024, 0.024);
        (pale.Shape.OutlineDistinct, pale.Shape.OutlinePale) = (false, true);
        var inset = Outlined(0.5, 0.5, 0.03, 0.03);

        Assert.Equal(RedetectDoubt.None, Shape(Input([placed], [pale]), placed).Doubt);
        Assert.False(Shape(Input([placed], [pale]), placed).Safe);
        Assert.Equal(RedetectDoubt.ThinPart, Shape(Input([placed], [Detected(0.5, 0.5, 0.028, 0.006)]), placed).Doubt);
        Assert.Equal(RedetectDoubt.None, Shape(Input([placed], [Detected(0.5, 0.5, 0.024, 0.024)]), placed).Doubt);
        Assert.Equal(RedetectDoubt.None, Shape(Input([inset], [Detected(0.5, 0.5, 0.023, 0.03)]), inset).Doubt);
        Assert.Equal(RedetectDoubt.DropsPart, Shape(Input([inset], [Detected(0.5, 0.5, 0.019, 0.03)]), inset).Doubt);
    }

    [Fact]
    public void AHookOrATail_ShowsAsAThinPart_AndACompactOutlineDoesNot()
    {
        P2[] square = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        P2[] tailed = [new(0, 0), new(1, 0), new(1, 0.45), new(2.2, 0.45), new(2.2, 0.55), new(1, 0.55), new(1, 1), new(0, 1)];

        Assert.True(RedetectSafety.ThinShare(square, 1) <= RedetectSafety.MaxThinShare);
        Assert.True(RedetectSafety.ThinShare(tailed, 1) > RedetectSafety.MaxThinShare);
    }

    [Fact]
    public async Task AnAnswerAboutAHoldThatChangedSince_IsLeftOut_AndTheOthersAreApplied()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var edited = await s.AddHoldAsync(panel, (0.3, 0.3), configure: h => (h.Radius, h.IsAutoDetected) = (0.03, true));
        var untouched = await s.AddHoldAsync(panel, (0.7, 0.7), configure: h => (h.Radius, h.IsAutoDetected) = (0.03, true));
        s.Harness.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(new List<DetectedHold> { new(0.306, 0.3, 0.028, null, 0.9), new(0.706, 0.7, 0.028, null, 0.9) }));
        var redetect = Service(s);
        var plan = await redetect.PlanAsync(s.WallId, panel);
        var accepted = plan.Proposals.Select(p => p.Key).ToHashSet();
        Assert.Equal(2, accepted.Count);
        Assert.All(plan.Proposals, p => Assert.NotEmpty(p.Stamp));

        // Somebody resizes one of the two holds after the cards were made.
        await s.SetAsync(edited, h => h.Radius = 0.02);
        var done = await redetect.ApplyAsync(plan, accepted);

        Assert.Equal([plan.Proposals.Single(p => p.HoldId == edited).Key], done.Stale);
        Assert.Equal(0.02, (await s.GetAsync(edited)).Radius);
        Assert.Equal(0.706, (await s.GetAsync(untouched)).X, 6);
    }

    [Fact]
    public async Task WhereReDetectIsSwitchedOff_TheServiceRefusesToPlanAndToApply()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        s.Harness.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(new List<DetectedHold>()));
        var off = Service(s, new BlocwerkSettings());
        var on = new BlocwerkSettings();
        on.HoldDetection.RedetectEnabled = true;
        var plan = new PanelRedetectPlan(s.WallId, panel, [], PanelRedetectSummary.Nothing);

        await Assert.ThrowsAsync<UserFacingException>(() => off.PlanAsync(s.WallId, panel));
        await Assert.ThrowsAsync<UserFacingException>(() => off.ApplyAsync(plan, new HashSet<string>()));
        Assert.Empty((await Service(s, on).PlanAsync(s.WallId, panel)).Proposals);
    }

    private static PanelRedetectService Service(HoldColorScenario s, BlocwerkSettings? settings = null) => new(
        s.Harness.DbContextFactory, s.Harness.CurrentUser, s.Harness.HoldDetection, Options, NullLogger<PanelRedetectService>.Instance, settings: settings);

    private static RedetectProposal Shape(RedetectInput input, Hold hold) =>
        RedetectProposals.Build(input, RedetectPlanner.Plan(input, Options), []).Single(p => p.HoldId == hold.Id && p.Kind is RedetectProposalKind.BetterShape or RedetectProposalKind.HandShape);

    private static RedetectInput Input(Hold[] existing, RedetectDetection[] detections) => new(
        [.. existing.Select(h => new RedetectExisting(h, new HashSet<Guid>(), 0, false))], detections, new HashSet<(Guid, Guid)>(), CanMerge: true);

    private static RedetectDetection Detected(double x, double y, double halfWidth, double halfHeight)
    {
        var shape = Outlined(x, y, halfWidth, halfHeight);
        (shape.Confidence, shape.OutlineConfidence) = (0.95, 0.9);
        return new RedetectDetection(shape, null);
    }

    private static Hold Outlined(double x, double y, double halfWidth, double halfHeight) => new()
    {
        X = x,
        Y = y,
        Radius = Math.Max(halfWidth, halfHeight),
        IsAutoDetected = true,
        OutlineCorroborated = true,
        OutlineDistinct = true,
        OutlineSource = HoldOutlineSource.AutoContour,
        ShapePoints =
        [
            new() { Dx = -halfWidth, Dy = -halfHeight }, new() { Dx = halfWidth, Dy = -halfHeight },
            new() { Dx = halfWidth, Dy = halfHeight }, new() { Dx = -halfWidth, Dy = halfHeight },
        ],
    };
}
