// <copyright file="MatcherHealthTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// The quick update must not report "0 refound, 1128 kept" as a success: a matcher that could not start (its native image
/// library missing), or that found none of a panel's many old holds again, fails the run with what went wrong.
/// </summary>
public class MatcherHealthTests
{
    private static readonly Guid Centre = Guid.NewGuid();

    [Fact]
    public void AMatcherThatCouldNotStart_FailsTheRun_WithTheReason()
    {
        var session = Session(holds: 40, refound: 0) with
        {
            AutoMatchStatus = AutoMatchStatus.Unavailable,
            AutoMatchMessage = "The type initializer for 'OpenCvSharp.Internal.NativeMethods' threw an exception.",
        };

        var error = Assert.Throws<UserFacingException>(() => MatcherHealth.EnsureUsable(session));

        Assert.Contains("could not start", error.Message);
        Assert.Contains("NativeMethods", error.Message);
        Assert.Contains("libOpenCvSharpExtern", error.Message);
    }

    [Fact]
    public void NoOldHoldFoundAgainOnAPanelWithMany_FailsTheRun()
    {
        var error = Assert.Throws<UserFacingException>(() => MatcherHealth.EnsureUsable(Session(holds: 698, refound: 0)));

        Assert.Contains("none of the 698 old holds", error.Message);
        Assert.Contains("retake that panel's photo", error.Message);
        Assert.Contains("full revision", error.Message);
    }

    [Theory]
    [InlineData(698, 1)]
    [InlineData(MatcherHealth.MinOldHolds - 1, 0)]
    public void SomeOldHoldsFoundAgain_OrAPanelWithFew_IsUsable(int holds, int refound)
    {
        MatcherHealth.EnsureUsable(Session(holds, refound));
    }

    [Fact]
    public void APanelAnEarlierRunRecordedAsUnaligned_DoesNotStopTheRunAgain()
    {
        var session = Session(holds: 698, refound: 0);

        MatcherHealth.EnsureUsable(session, new HashSet<Guid> { Centre });
    }

    [Fact]
    public void TheApply_OnlyChecksThatTheMatcherRuns()
    {
        MatcherHealth.EnsureUsable(Session(holds: 698, refound: 0), panels: false);
        Assert.Throws<UserFacingException>(() =>
            MatcherHealth.EnsureUsable(Session(holds: 698, refound: 0) with { AutoMatchStatus = AutoMatchStatus.Unavailable }, panels: false));
    }

    [Fact]
    public void VirtualHolds_DoNotCount()
    {
        var old = Enumerable.Range(0, 40).Select(_ => new PanelHold(Guid.NewGuid(), 0.5, 0.5, 0.01, null, HoldCategory.Hand, IsVirtual: true)).ToList();
        var session = new BigUpdateSession(
            Guid.NewGuid(), Centre, [], [], [], [], CarriedPanels: [new CarriedPanelOldHolds(0, 0, Guid.NewGuid(), Centre, old)]);

        MatcherHealth.EnsureUsable(session);
    }

    [Fact]
    public void AFullRevision_MatchesNothingOld_AndIsUsable()
    {
        MatcherHealth.EnsureUsable(Session(holds: 698, refound: 0) with { FullRevision = true, AutoMatchStatus = AutoMatchStatus.Unavailable });
    }

    private static BigUpdateSession Session(int holds, int refound)
    {
        var old = Enumerable.Range(0, holds).Select(_ => new PanelHold(Guid.NewGuid(), 0.5, 0.5, 0.01, null, HoldCategory.Hand)).ToList();
        var carry = old.Take(refound).Select(h => new CarryoverProposal(h.Id, Guid.NewGuid(), 0.9, 1)).ToList();
        return new BigUpdateSession(
            Guid.NewGuid(), Centre, carry, [], [], [],
            CarriedPanels: [new CarriedPanelOldHolds(0, 0, Guid.NewGuid(), Centre, old)]);
    }
}
