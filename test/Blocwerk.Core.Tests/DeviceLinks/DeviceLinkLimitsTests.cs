// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.DeviceLinks;

namespace Blocwerk.Core.Tests.DeviceLinks;

/// <summary>The server-wide exchange budget and the per-user consent budget.</summary>
public class DeviceLinkLimitsTests
{
    [Fact]
    public void Exchanges_ShareOneServerWideBudget()
    {
        using var limits = new DeviceLinkLimits(globalExchangesPerMinute: 3);

        Assert.True(limits.TryExchange());
        Assert.True(limits.TryExchange());
        Assert.True(limits.TryExchange());
        Assert.False(limits.TryExchange());
    }

    [Fact]
    public void Confirms_AreCountedPerUser()
    {
        using var limits = new DeviceLinkLimits(confirmsPerUserPerMinute: 2);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        Assert.True(limits.TryConfirm(alice));
        Assert.True(limits.TryConfirm(alice));
        Assert.False(limits.TryConfirm(alice));
        Assert.True(limits.TryConfirm(bob));
    }
}
