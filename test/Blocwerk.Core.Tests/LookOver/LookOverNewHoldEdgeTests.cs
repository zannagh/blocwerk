// <copyright file="LookOverNewHoldEdgeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>The edges of a mixed "use the new hold" (<see cref="NewHoldEdgeScenarios"/>), on SQLite.</summary>
public class LookOverNewHoldEdgeTests
{
    [Fact]
    public async Task ArchivedAndHistoricBoulders_KeepTheirHold_OnTheNewHold()
    {
        using var h = new WallTestHarness();
        await NewHoldEdgeScenarios.FrozenBouldersMoveAsync(h);
    }

    [Fact]
    public async Task ABoulderOnBothHolds_KeepsOneMembershipWithTheStrongerRole_AndIsMarked()
    {
        using var h = new WallTestHarness();
        await NewHoldEdgeScenarios.BoulderOnBothHoldsAsync(h);
    }

    [Fact]
    public async Task WhatIsStackedOnTheOldHold_SitsOnTheNewOne_AndItsLinkGoes_UntilUndone()
    {
        using var h = new WallTestHarness();
        await NewHoldEdgeScenarios.StackedAndLinkedAsync(h);
    }

    [Fact]
    public async Task ABoulderThatJoinedBeforeTheLock_IsRefused()
    {
        using var h = new WallTestHarness();
        await NewHoldEdgeScenarios.JoinedUnderTheLockAsync(h);
    }
}
