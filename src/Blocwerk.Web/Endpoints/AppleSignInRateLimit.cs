// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.RateLimiting;
using Blocwerk.Authentication.Endpoints;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The per-address limits of the anonymous Sign in with Apple routes (nonce and sign-in), plus the server-wide failure
/// budget. There is no server-wide limit on requests: anyone could spend it and so block every sign-in.
/// </summary>
public static class AppleSignInRateLimit
{
    public const string NoncePolicy = "apple-signin-nonce";
    public const string SignInPolicy = "apple-signin";

    /// <summary>Nonces one client address may fetch per <see cref="Window"/>.</summary>
    public const int NoncesPerWindow = 20;

    /// <summary>Sign-ins one client address may try per <see cref="Window"/>, successes included.</summary>
    public const int SignInsPerWindow = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddAppleSignInRateLimit(this IServiceCollection services)
    {
        services.AddSingleton<DeviceLinks.AppleSignInFailureBudget>();
        PerAddress(services, NoncePolicy, NoncesPerWindow);
        return PerAddress(services, SignInPolicy, SignInsPerWindow);
    }

    private static IServiceCollection PerAddress(IServiceCollection services, string policy, int permits) =>
        services.AddRateLimitPolicy(
            policy,
            http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = Window,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }),
            (context, _) =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(AppleSignInRateLimit))
                    .LogWarning(
                        "Sign in with Apple rate limit {Policy} hit from {RemoteIp}",
                        policy,
                        context.HttpContext.Connection.RemoteIpAddress);
                return ValueTask.CompletedTask;
            });
}
