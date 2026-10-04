// <copyright file="PossiblyRemovedTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>"Possibly removed": bare wall needs a registration, a model from this visit and both scores confidently low.</summary>
public class PossiblyRemovedTests
{
    private static RemovalEvidence Evidence(
        bool registered = true,
        bool fresh = true,
        bool detection = false,
        bool seen = false,
        double? photo = 0.1,
        double? texture = 0.1) => new(registered, fresh, detection, seen, photo, texture);

    [Fact]
    public void BothScoresLow_OnARegisteredFreshModel_IsBareWall() =>
        Assert.Equal(RemovalVerdict.BareWall, PossiblyRemovedJudge.Judge(Evidence()));

    [Theory]
    [InlineData(0.6, 0.1)]
    [InlineData(0.1, 0.6)]
    [InlineData(0.9, 0.9)]
    public void AnyScoreShowingTheOldLook_IsHoldPresent(double photo, double texture) =>
        Assert.Equal(RemovalVerdict.HoldPresent, PossiblyRemovedJudge.Judge(Evidence(photo: photo, texture: texture)));

    [Fact]
    public void ADetectionOrAHoldSeenIn3D_IsHoldPresent()
    {
        Assert.Equal(RemovalVerdict.HoldPresent, PossiblyRemovedJudge.Judge(Evidence(detection: true)));
        Assert.Equal(RemovalVerdict.HoldPresent, PossiblyRemovedJudge.Judge(Evidence(seen: true)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NoRegistration_OrAnOldModel_IsUnknown_EvenWithLowScores(bool registered, bool fresh) =>
        Assert.Equal(RemovalVerdict.Unknown, PossiblyRemovedJudge.Judge(Evidence(registered: registered, fresh: fresh)));

    [Theory]
    [InlineData(null, 0.1)]
    [InlineData(0.1, null)]
    [InlineData(0.45, 0.1)]
    [InlineData(0.1, 0.31)]
    public void AMissingOrUnsureScore_IsUnknown(double? photo, double? texture) =>
        Assert.Equal(RemovalVerdict.Unknown, PossiblyRemovedJudge.Judge(Evidence(photo: photo, texture: texture)));

    [Fact]
    public void TheCheck_WithABareSpotAndConfidentProbes_FindsBareWall()
    {
        var findings = RemovalCheck.Run(Panel(), q => q.Select(_ => (double?)0.05).ToList(), (_, q) => q.Select(_ => (double?)0.1).ToList());

        var finding = Assert.Single(findings);
        Assert.Equal(RemovalVerdict.BareWall, finding.Verdict);
        Assert.Equal(0.05, finding.PhotoScore);
        Assert.Equal(0.1, finding.TextureScore);
        Assert.NotNull(finding.Spot);
    }

    [Fact]
    public void TheCheck_WithAHoldInTheTexture_FindsAHold()
    {
        var findings = RemovalCheck.Run(Panel(), q => q.Select(_ => (double?)0.05).ToList(), (_, q) => q.Select(_ => (double?)0.8).ToList());

        Assert.Equal(RemovalVerdict.HoldPresent, Assert.Single(findings).Verdict);
    }

    [Fact]
    public void TheCheck_WithoutProbes_AddsNothing()
    {
        var findings = RemovalCheck.Run(Panel(), null, null);

        Assert.Equal(RemovalVerdict.Unknown, Assert.Single(findings).Verdict);
    }

    [Fact]
    public void TheCheck_WithAnOldModel_NeverProbesAndAddsNothing()
    {
        var probed = false;
        IReadOnlyList<double?> Probe(IReadOnlyList<PresenceQuery> q)
        {
            probed = true;
            return q.Select(_ => (double?)0.0).ToList();
        }

        var findings = RemovalCheck.Run(Panel(fresh: false), Probe, (_, q) => Probe(q));

        Assert.False(probed);
        Assert.Equal(RemovalVerdict.Unknown, Assert.Single(findings).Verdict);
    }

    [Fact]
    public void TheCheck_WithAFailedRegistration_AddsNothing()
    {
        var registration = FacetRegistration.Rejected("0", new PlaneRectMm(0, 2000, 0, 3000), 10, 0, "too few inliers");
        var panel = Panel() with { Evidence = new Panel3DEvidence([registration], [], []) };

        var findings = RemovalCheck.Run(panel, q => q.Select(_ => (double?)0.0).ToList(), (_, q) => q.Select(_ => (double?)0.0).ToList());

        var finding = Assert.Single(findings);
        Assert.Equal(RemovalVerdict.Unknown, finding.Verdict);
        Assert.Null(finding.Spot);
    }

    [Fact]
    public void TheCheck_WithADetectionAtTheSpot_FindsAHoldWithoutProbing()
    {
        var panel = Panel() with { StagedHolds = [(SyntheticWallCamera.Width * 0.5, SyntheticWallCamera.Height * 0.5)] };

        var findings = RemovalCheck.Run(panel, q => q.Select(_ => (double?)0.0).ToList(), (_, q) => q.Select(_ => (double?)0.0).ToList());

        Assert.Equal(RemovalVerdict.HoldPresent, Assert.Single(findings).Verdict);
    }

    private static RemovalPanel Panel(bool fresh = true)
    {
        var camera = new SyntheticWallCamera();
        var (w, h) = (SyntheticWallCamera.Width, SyntheticWallCamera.Height);
        var spot = new RemovalCandidate(Guid.NewGuid(), 600, 450, w * 0.5, h * 0.5, 60);

        // The old photo is the new one at 0.3 scale.
        PointPair[] pairs = [new(0, 0, 0, 0), new(w, 0, w * 0.3, 0), new(0, h, 0, h * 0.3), new(w, h, w * 0.3, h * 0.3)];
        var inliers = new List<(double X, double Y)>();
        for (var gx = 0.2; gx <= 0.81; gx += 0.2)
        {
            for (var gy = 0.2; gy <= 0.81; gy += 0.2)
            {
                inliers.Add((gx, gy));
            }
        }

        var evidence = new Panel3DEvidence([camera.Registration() with { InlierPoints = inliers }], [], []);
        var frame = new TexturePlaneFrame("0", 0, 2000, 0, 3000, 1000, 1500);
        return new RemovalPanel(
            [spot], pairs, [], (w, h), evidence, new Dictionary<string, TexturePlaneFrame> { ["0"] = frame }, fresh);
    }
}
