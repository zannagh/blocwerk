// <copyright file="MarkerPlacementCheckerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Xunit.Abstractions;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// "Did I place them right?" on The Attic's real solve (capture 1): the plan built from what is on the
/// wall matches; a plan the markers do NOT follow gets every mismatch named.
/// </summary>
public class MarkerPlacementCheckerTests(ITestOutputHelper output)
{
    private static readonly WallGeometryDocument Attic =
        WallGeometryDocument.Parse(File.ReadAllText(WallMarkerLayoutTests.Fixture("attic-wall-geometry.json")));

    private static readonly HashSet<int> AllDetected = Attic.Markers.Select(m => m.Id).ToHashSet();

    [Fact]
    public void TheAttic_MatchesItsOwnPlan()
    {
        var check = MarkerPlacementChecker.Check(WallMarkerLayout.FromPlan(AtticMarkerPlan.Plan), Attic, AllDetected);

        Assert.True(check.AllGood, string.Join("\n", check.Findings.Select(f => f.Message)));
        Assert.Equal((21, 21, 21), (check.PlannedMarkers, check.SolvedMarkers, check.CheckedMarkers));
    }

    [Fact]
    public void MisplacedMarkers_AreNamed()
    {
        var markers = AtticMarkerPlan.Plan.Markers
            .Select(m => m.Id switch
            {
                4 => m with { XMm = m.XMm + 400 }, // glued 40 cm off its spot
                26 => m with { Segment = 5, XMm = 700, YMm = 300 }, // planned on the right piece, glued on the main wall
                _ => m,
            })
            .Append(new PlanMarker(40, 0, 2500, 1700, 125, MarkerRole.Filler)) // never put up
            .Append(new PlanMarker(41, 1, 4000, 200, 125, MarkerRole.Filler)) // seen once, not solvable
            .ToList();
        var segments = AtticMarkerPlan.Segments.Select(s => s.Index == 0 ? s with { OverhangDeg = 30 } : s).ToList();
        var plan = AtticMarkerPlan.Plan with { Segments = segments, Markers = markers };

        var check = MarkerPlacementChecker.Check(WallMarkerLayout.FromPlan(plan), Attic, new HashSet<int>(AllDetected) { 41 });
        foreach (var finding in check.Findings)
        {
            output.WriteLine($"{finding.Kind}: {finding.Message}");
        }

        Assert.Equal(
            [
                (MarkerPlacementIssue.WrongSegment, (int?)26),
                (MarkerPlacementIssue.Offset, 4),
                (MarkerPlacementIssue.NeverSeen, 40),
                (MarkerPlacementIssue.NotSolved, 41),
                (MarkerPlacementIssue.AngleMismatch, null),
            ],
            check.Findings.Select(f => (f.Kind, f.MarkerId)));
        var wrong = check.Findings[0];
        Assert.Equal((5, 0), (wrong.PlannedSegment!.Value, wrong.ObservedSegment!.Value));
        Assert.Contains("planned on segment 5 (“right cornered piece”) but was found on segment 0 (“main wall”)", wrong.Message);
        Assert.InRange(check.Findings[1].Value!.Value, 380, 420);
        Assert.Equal(15.4, check.Findings[4].Value!.Value, 1);
    }

    [Fact]
    public void AMarkerMeasuringOtherThanPlanned_IsASizeMismatch_NoiseIsNot()
    {
        // Marker 4 planned at 100 mm but printed at 125 mm (the solver measured 125.4 mm); marker 5 at 129 mm
        // is within max(5 mm, 5 %) of its planned 125 mm; the rest carry no measurement (an older solve).
        var measured = new Dictionary<int, double> { [4] = 125.4, [5] = 129 };
        var solved = Attic with
        {
            Markers = Attic.Markers
                .Select(m => measured.TryGetValue(m.Id, out var side) ? m with { MeasuredSideMm = side, MeasuredSidePhotos = 5 } : m)
                .ToList(),
        };
        var plan = AtticMarkerPlan.Plan with
        {
            Markers = AtticMarkerPlan.Plan.Markers.Select(m => m.Id == 4 ? m with { SizeMm = 100 } : m).ToList(),
        };

        var check = MarkerPlacementChecker.Check(WallMarkerLayout.FromPlan(plan), solved, AllDetected);

        var finding = Assert.Single(check.Findings);
        Assert.Equal((MarkerPlacementIssue.SizeMismatch, (int?)4), (finding.Kind, finding.MarkerId));
        Assert.Equal(25.4, finding.Value!.Value, 1);
        Assert.Equal("Marker 4 was planned at 100 mm but measures ≈125 mm in the photos; check its printed size.", finding.Message);
    }

    [Fact]
    public void RobustFit_IgnoresOneBadPoint_AndMeasuresItsOffset()
    {
        PlanVector[] planned = [new(0, 0), new(1000, 0), new(1000, 800), new(0, 800), new(500, 400)];
        var angle = 0.05;
        var measured = planned
            .Select(p => new PlanVector((Math.Cos(angle) * p.X) - (Math.Sin(angle) * p.Y) + 70, (Math.Sin(angle) * p.X) + (Math.Cos(angle) * p.Y) - 30))
            .ToArray();
        measured[4] += new PlanVector(0, 250);
        var pairs = planned.Zip(measured, (a, b) => (a, b)).ToList();

        var fit = RigidFit2D.Robust(pairs, 100)!;

        Assert.All(pairs.Take(4), p => Assert.True(fit.Residual(p) < 0.01));
        Assert.Equal(250, fit.Residual(pairs[4]), 3);
    }
}
