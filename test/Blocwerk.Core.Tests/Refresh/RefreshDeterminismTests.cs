// <copyright file="RefreshDeterminismTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// What Apply promotes must not depend on read order: the default twin is the same wherever it is chosen (quick
/// defaults, scope reset), competing relocation accepts fold the same way in any order, and the version that Apply
/// checks covers the staged holds as well as the decisions.
/// </summary>
public class RefreshDeterminismTests
{
    private static readonly Guid Old = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Low = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid High = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    [Fact]
    public void TheDefaultTwin_IsTheMostConfident_ThenTheSmallestId()
    {
        Assert.Equal(High, CarryoverScope.DefaultTwins(Session(new(Old, High, 0.9, 1), new(Old, Low, 0.5, 1)))[Old]);
        Assert.Equal(High, CarryoverScope.DefaultTwins(Session(new(Old, Low, 0.5, 1), new(Old, High, 0.9, 1)))[Old]);
        Assert.Equal(Low, CarryoverScope.DefaultTwins(Session(new(Old, High, 0.7, 1), new(Old, Low, 0.7, 1)))[Old]);
    }

    [Fact]
    public void TheScopeReset_LandsOnTheTwinTheQuickDefaultsChose()
    {
        // The most confident proposal comes first: a last-wins reset would have picked the weaker one.
        var session = Session(new(Old, High, 0.9, 1), new(Old, Low, 0.5, 1));
        var quick = QuickUpdateDefaults.Build(session, new Dictionary<Guid, IReadOnlyList<Guid>>());

        var reconciled = CarryoverScope.Reconcile(session, [new CarryoverDecision(Old, CarryKind.Removed, null)]);

        Assert.Equal(High, Assert.Single(quick.Carryover).NewHoldId);
        Assert.Equal(High, Assert.Single(reconciled.Decisions).NewHoldId);
    }

    [Fact]
    public void CompetingRelocationAccepts_FoldTheSame_InAnyOrder()
    {
        var other = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var empty = new BigUpdateConfirmation([], [], [], []);
        (Guid, Guid, CarryKind) first = (Old, High, CarryKind.Changed);
        (Guid, Guid, CarryKind) second = (other, High, CarryKind.Changed);

        var forward = RelocationFold.Apply(empty, [first, second]).Carryover;
        var backward = RelocationFold.Apply(empty, [second, first]).Carryover;

        Assert.Equal(forward.OrderBy(d => d.OldHoldId), backward.OrderBy(d => d.OldHoldId));
        Assert.Equal(Old, Assert.Single(forward).OldHoldId);
    }

    [Fact]
    public void TheVersion_CoversTheStagedHolds_RegardlessOfOrder()
    {
        var panel = Guid.NewGuid();
        var decisions = new BigUpdateConfirmation([new CarryoverDecision(Old, CarryKind.Carried, High)], [], [], []);

        var staged = RefreshDecisions.Fingerprint(decisions, new Dictionary<Guid, IReadOnlyList<Guid>> { [panel] = [High, Low] });
        var reordered = RefreshDecisions.Fingerprint(decisions, new Dictionary<Guid, IReadOnlyList<Guid>> { [panel] = [Low, High] });
        var oneDeleted = RefreshDecisions.Fingerprint(decisions, new Dictionary<Guid, IReadOnlyList<Guid>> { [panel] = [High] });

        Assert.Equal(staged, reordered);
        Assert.NotEqual(staged, oneDeleted);
    }

    // No displayed (0,0) panel among the carried ones: every verdict is outside the reviewable scope.
    private static BigUpdateSession Session(params CarryoverProposal[] proposals) =>
        new(Guid.NewGuid(), Guid.NewGuid(), [.. proposals], [], [], [], CarriedPanels: [], CarriedOldHoldIds: [Old]);
}
