// <copyright file="SimilarHoldAssignerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.HoldMoves;

namespace Blocwerk.Core.Tests;

/// <summary>"There are five similar holds on our wall": the assignment inside a look-alike group moves as little as possible.</summary>
public class SimilarHoldAssignerTests
{
    private static readonly HoldMoveOptions Options = new();

    private static Guid Id(int n) => new(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    private static SimilarHold Black(int n) => new(Id(n), "black", RelocationScenario.Green(), 80, null);

    private static Func<Guid, Guid, double?> Line(Dictionary<Guid, double> oldAt, Dictionary<Guid, double> newAt) =>
        (o, n) => Math.Abs(oldAt[o] - newAt[n]);

    private static Func<Guid, Guid, double?> Plane(Dictionary<Guid, (double X, double Y)> oldAt, Dictionary<Guid, (double X, double Y)> newAt) =>
        (o, n) => Math.Sqrt(Math.Pow(oldAt[o].X - newAt[n].X, 2) + Math.Pow(oldAt[o].Y - newAt[n].Y, 2));

    [Fact]
    public void FiveIdenticalHolds_OneMoved_OnlyThatOneIsFlagged()
    {
        // Old 1..5 at 0,200,400,600,800. New (listed in a scrambled order): four at the same spots, one at 1500.
        var olds = Enumerable.Range(1, 5).Select(Black).ToList();
        var news = new[] { 14, 11, 15, 12, 13 }.Select(Black).ToList();
        var oldAt = Enumerable.Range(1, 5).ToDictionary(i => Id(i), i => ((i - 1) * 200.0, 0.0));
        var newAt = new Dictionary<Guid, (double X, double Y)>
        {
            [Id(11)] = (0, 0), [Id(12)] = (200, 0), [Id(13)] = (400, 0), [Id(14)] = (600, 900), [Id(15)] = (800, 0),
        };

        // The fourth hold (old at 600) is the one that moved: its detection (14) sits 900 mm above it.
        var assigned = SimilarHoldAssigner.Assign(olds, news, Plane(oldAt, newAt), Options);

        Assert.Equal(5, assigned.Count);
        Assert.Equal(1, assigned.Count(a => a.DistanceMm > 0));
        var mover = assigned.Single(a => a.DistanceMm > 0);
        Assert.Equal((Id(4), Id(14)), (mover.OldId, mover.NewId));
        Assert.Equal(900, mover.DistanceMm!.Value, 3);
        Assert.All(assigned.Where(a => a.OldId != Id(4)), a => Assert.Equal(0, a.DistanceMm!.Value, 3));
    }

    [Fact]
    public void TwoSimilarHoldsSwapped_EachIsAssignedToTheNearestSpot()
    {
        // A at 0 and B at 500 trade places on the wall. Nearest-spot assignment says each stayed put.
        var olds = new[] { Black(1), Black(2) };
        var news = new[] { Black(11), Black(12) };
        var oldAt = new Dictionary<Guid, double> { [Id(1)] = 0, [Id(2)] = 500 };
        var newAt = new Dictionary<Guid, double> { [Id(11)] = 500, [Id(12)] = 0 };

        var assigned = SimilarHoldAssigner.Assign(olds, news, Line(oldAt, newAt), Options);

        Assert.Equal(new[] { (Id(1), Id(12)), (Id(2), Id(11)) }, assigned.Select(a => (a.OldId, a.NewId)).ToArray());
        Assert.All(assigned, a => Assert.Equal(0, a.DistanceMm!.Value, 3));
    }

    [Fact]
    public void MinimisesTheTotal_NotEachHoldsOwnNearest()
    {
        // Greedy would give old 1 (at 0) the detection at 90, leaving old 2 (at 100) the one at 400: 90 + 300.
        // The total-minimum assignment pairs 1 with 30... both detections are far; the optimum is 1->90? check by cost.
        var olds = new[] { Black(1), Black(2) };
        var news = new[] { Black(11), Black(12) };
        var oldAt = new Dictionary<Guid, double> { [Id(1)] = 0, [Id(2)] = 100 };
        var newAt = new Dictionary<Guid, double> { [Id(11)] = -20, [Id(12)] = 110 };

        var assigned = SimilarHoldAssigner.Assign(olds, news, Line(oldAt, newAt), Options);

        Assert.Equal(30, assigned.Sum(a => a.DistanceMm!.Value), 3);
        Assert.Equal(Id(11), assigned.Single(a => a.OldId == Id(1)).NewId);
    }

    [Fact]
    public void DifferentColoursAreDifferentGroups_AndNeverPaired()
    {
        var olds = new[] { Black(1) };
        var news = new[] { new SimilarHold(Id(11), "red", RelocationScenario.Green(), 80, null) };

        Assert.Empty(SimilarHoldAssigner.Assign(olds, news, (_, _) => 0, Options));
    }

    [Fact]
    public void DifferentSizeOrDepth_AreNotLookAlikes()
    {
        var a = Black(1);

        Assert.False(SimilarHoldAssigner.AreSimilar(a, a with { Id = Id(2), WidthMm = 200 }, Options));
        Assert.False(SimilarHoldAssigner.AreSimilar(a with { DepthMm = 20 }, a with { Id = Id(2), DepthMm = 80 }, Options));
        Assert.True(SimilarHoldAssigner.AreSimilar(a with { DepthMm = 20 }, a with { Id = Id(2), DepthMm = 30 }, Options));
    }

    [Fact]
    public void UnknownDistances_AreNeverAssigned_AndMoreDetectionsThanOldHoldsLeaveTheExtraOut()
    {
        var olds = new[] { Black(1) };
        var news = new[] { Black(11), Black(12) };

        Assert.Empty(SimilarHoldAssigner.Assign(olds, news, (_, _) => null, Options));

        var oldAt = new Dictionary<Guid, double> { [Id(1)] = 0 };
        var newAt = new Dictionary<Guid, double> { [Id(11)] = 400, [Id(12)] = 10 };
        var one = Assert.Single(SimilarHoldAssigner.Assign(olds, news, Line(oldAt, newAt), Options));
        Assert.Equal(Id(12), one.NewId);
    }

    [Fact]
    public void TheResultIsTheSameForAnyInputOrder()
    {
        var olds = Enumerable.Range(1, 4).Select(Black).ToList();
        var news = Enumerable.Range(11, 4).Select(Black).ToList();
        var oldAt = olds.ToDictionary(o => o.Id, o => 0.0);
        var newAt = news.ToDictionary(n => n.Id, n => 0.0);

        var first = SimilarHoldAssigner.Assign(olds, news, Line(oldAt, newAt), Options);
        var second = SimilarHoldAssigner.Assign(Enumerable.Reverse(olds).ToList(), Enumerable.Reverse(news).ToList(), Line(oldAt, newAt), Options);

        Assert.Equal(first, second);
    }
}
