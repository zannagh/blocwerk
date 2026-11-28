// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.RateLimiting;

namespace Blocwerk.Web.DeviceLinks;

/// <summary>
/// A server-wide backstop on nonce issuance, above the per-address limit. Issuing stores nothing, so this only bounds
/// the work of a flood from many addresses; it is set far above what real sign-ins need.
/// </summary>
public sealed class AppleNonceIssuanceCap : IDisposable
{
    /// <summary>Nonces the whole server issues per minute; more answer 429.</summary>
    public const int DefaultNoncesPerMinute = 1000;

    private readonly FixedWindowRateLimiter limiter;

    public AppleNonceIssuanceCap(int noncesPerMinute = DefaultNoncesPerMinute)
    {
        limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = noncesPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    /// <summary>Takes one nonce from the budget; false when it is spent.</summary>
    public bool TryIssue()
    {
        using var lease = limiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        limiter.Dispose();
    }
}
