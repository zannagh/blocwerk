// <copyright file="DifferentialDisplacementTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.HoldMoves;

namespace Blocwerk.Core.Tests;

/// <summary>The registration error is regional and systematic; only a hold's movement relative to its neighbours counts.</summary>
public class DifferentialDisplacementTests
{
    private static readonly HoldMoveOptions Options = new();

    private static Guid Id(int n) => new(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    private static DifferentialDisplacement.Input Hold(int n, double a, double b, double da, double db) =>
        new(Id(n), "0", a, b, da, db, 0);

    private static List<DifferentialDisplacement.Input> Grid(double offA, double offB) =>
        Enumerable.Range(0, 12).Select(i => Hold(i + 1, (i % 4) * 150, (i / 4) * 150, offA, offB)).ToList();

    [Fact]
    public void ASystematicOffset_IsCancelled_EvenWhenItIsFarBeyondTheCutoff()
    {
        var result = DifferentialDisplacement.Compute(Grid(130, -90), Options);

        Assert.Equal(12, result.Count);
        Assert.All(result.Values, r => Assert.True(r.ResidualMm < 1, $"{r.ResidualMm}"));
    }

    [Fact]
    public void OneRealMover_StandsOutOfTheOffset_AndTheOthersStayAtZero()
    {
        var holds = Grid(40, 30);
        holds[5] = holds[5] with { Da = 40 + 400 };

        var result = DifferentialDisplacement.Compute(holds, Options);

        Assert.Equal(400, result[Id(6)].ResidualMm, 3);
        Assert.All(result.Where(kv => kv.Key != Id(6)), kv => Assert.True(kv.Value.ResidualMm < 1));
    }

    [Fact]
    public void FewerNeighboursThanRequired_HaveNoVerdict_AndOtherFacetsDoNotCount()
    {
        var sparse = Grid(10, 10).Take(4).ToList();
        Assert.Empty(DifferentialDisplacement.Compute(sparse, Options));

        var other = Grid(10, 10).Select((h, i) => i < 8 ? h : h with { FacetId = "1" }).ToList();
        var result = DifferentialDisplacement.Compute(other, Options);
        Assert.Equal(8, result.Count);
    }

    [Fact]
    public void ANoisyNeighbourhood_IsNotConfident()
    {
        var rng = new Random(3);
        var noisy = Grid(0, 0).Select(h => h with { Da = (rng.NextDouble() * 140) - 70, Db = (rng.NextDouble() * 140) - 70 }).ToList();

        var result = DifferentialDisplacement.Compute(noisy, Options);

        Assert.All(result.Values, r => Assert.True(r.SpreadMm > Options.MaxSpreadMm));
    }

    [Fact]
    public void TheResult_DoesNotDependOnInputOrder()
    {
        var holds = Grid(20, 5);
        holds[2] = holds[2] with { Db = 200 };

        var first = DifferentialDisplacement.Compute(holds, Options);
        var second = DifferentialDisplacement.Compute(Enumerable.Reverse(holds).ToList(), Options);

        Assert.Equal(first.OrderBy(kv => kv.Key).Select(kv => kv.Value), second.OrderBy(kv => kv.Key).Select(kv => kv.Value));
    }
}
