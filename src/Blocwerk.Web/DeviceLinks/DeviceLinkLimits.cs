// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.RateLimiting;

namespace Blocwerk.Web.DeviceLinks;

/// <summary>
/// The device link's limits that the per-address rate-limit policy cannot give: one partition for the whole server on
/// the code exchange (the per-address one trusts <c>X-Forwarded-For</c> while no trusted proxy is configured, so it
/// can be spread), and one per signed-in user on the consent post (the rate-limit middleware runs before
/// authentication, so a policy cannot partition by user).
/// </summary>
public sealed class DeviceLinkLimits : IDisposable
{
    /// <summary>Code exchanges the whole server answers per minute, on top of the per-address limit.</summary>
    public const int DefaultGlobalExchangesPerMinute = 60;

    /// <summary>Consent posts one user may make per minute.</summary>
    public const int DefaultConfirmsPerUserPerMinute = 10;

    private readonly FixedWindowRateLimiter global;
    private readonly PartitionedRateLimiter<Guid> perUser;

    public DeviceLinkLimits(
        int globalExchangesPerMinute = DefaultGlobalExchangesPerMinute,
        int confirmsPerUserPerMinute = DefaultConfirmsPerUserPerMinute)
    {
        global = new FixedWindowRateLimiter(Window(globalExchangesPerMinute));
        perUser = PartitionedRateLimiter.Create<Guid, Guid>(
            userId => RateLimitPartition.GetFixedWindowLimiter(userId, _ => Window(confirmsPerUserPerMinute)));
    }

    /// <summary>Takes one exchange from the server-wide budget; false when it is spent.</summary>
    public bool TryExchange()
    {
        using var lease = global.AttemptAcquire();
        return lease.IsAcquired;
    }

    /// <summary>Takes one consent post from <paramref name="userId"/>'s budget; false when it is spent.</summary>
    public bool TryConfirm(Guid userId)
    {
        using var lease = perUser.AttemptAcquire(userId);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        global.Dispose();
        perUser.Dispose();
    }

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
