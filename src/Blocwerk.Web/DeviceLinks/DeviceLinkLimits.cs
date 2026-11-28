// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.RateLimiting;

namespace Blocwerk.Web.DeviceLinks;

/// <summary>
/// The device link's limit that a rate-limit policy cannot give: one partition per signed-in user on the consent post
/// (the rate-limit middleware runs before authentication, so a policy cannot partition by user).
/// </summary>
public sealed class DeviceLinkLimits : IDisposable
{
    /// <summary>Consent posts one user may make per minute.</summary>
    public const int DefaultConfirmsPerUserPerMinute = 10;

    private readonly PartitionedRateLimiter<Guid> perUser;

    public DeviceLinkLimits(int confirmsPerUserPerMinute = DefaultConfirmsPerUserPerMinute)
    {
        perUser = PartitionedRateLimiter.Create<Guid, Guid>(
            userId => RateLimitPartition.GetFixedWindowLimiter(userId, _ => Window(confirmsPerUserPerMinute)));
    }

    /// <summary>Takes one consent post from <paramref name="userId"/>'s budget; false when it is spent.</summary>
    public bool TryConfirm(Guid userId)
    {
        using var lease = perUser.AttemptAcquire(userId);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
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
