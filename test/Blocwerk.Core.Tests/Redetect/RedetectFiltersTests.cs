// <copyright file="RedetectFiltersTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Redetect;

namespace Blocwerk.Core.Tests.Redetect;

/// <summary>The bulk step's button and the "Safe" chip count the same cards, and a card the detector is unsure about is never safe.</summary>
public class RedetectFiltersTests
{
    [Fact]
    public void TheBulkButtonAndTheSafeChip_CountTheSameCards_WhateverIsAnswered()
    {
        var proposals = Enumerable.Range(0, 5).Select(i => Shape(i, safe: i < 4)).ToList();
        var answers = new Dictionary<string, bool> { ["shape:0"] = false, ["shape:4"] = true };
        bool? Answer(RedetectProposal p) => answers.TryGetValue(p.Key, out var a) ? a : null;

        var button = RedetectFilters.SafeToAccept(proposals, Answer).Count;
        var chip = proposals.Count(p => RedetectFilters.Matches(RedetectFilter.Safe, p, Answer(p)));

        Assert.Equal(3, button);
        Assert.Equal(button, chip);
    }

    [Fact]
    public void ACardTheDetectorIsUnsureAbout_IsNeverSafe_AndStartsOutKept()
    {
        var unsure = Shape(0, safe: true) with { Doubt = RedetectDoubt.PaleHold };
        var newHold = new RedetectProposal("new:1", RedetectProposalKind.NewHold, null, [], [1], false, 0);

        Assert.True(RedetectFilters.StartsKept(unsure));
        Assert.True(RedetectFilters.StartsKept(newHold));
        Assert.False(RedetectFilters.StartsKept(Shape(1, safe: true)));
    }

    private static RedetectProposal Shape(int i, bool safe) =>
        new($"shape:{i}", RedetectProposalKind.BetterShape, Guid.NewGuid(), [], [i], safe, 0.9);
}
