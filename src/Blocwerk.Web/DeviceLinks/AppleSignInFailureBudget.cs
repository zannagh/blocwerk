// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.RateLimiting;

namespace Blocwerk.Web.DeviceLinks;

/// <summary>
/// The server-wide limit of Sign in with Apple, counted in failed attempts only. A success never touches it, so nobody
/// can spend it to lock everyone out: a stranger's flood of bad tokens only turns the bad answers into 429s, while a
/// real token still signs in.
/// </summary>
public sealed class AppleSignInFailureBudget : IDisposable
{
    /// <summary>Failed sign-ins the whole server answers normally per minute; later ones answer 429.</summary>
    public const int DefaultFailuresPerMinute = 60;

    private readonly FixedWindowRateLimiter limiter;

    public AppleSignInFailureBudget(int failuresPerMinute = DefaultFailuresPerMinute)
    {
        limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = failuresPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    /// <summary>Charges one failure; false when the budget is spent and the failure should answer 429.</summary>
    public bool TryChargeFailure()
    {
        using var lease = limiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        limiter.Dispose();
    }
}
