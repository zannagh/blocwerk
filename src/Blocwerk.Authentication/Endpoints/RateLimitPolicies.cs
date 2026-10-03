// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Blocwerk.Authentication.Endpoints;

/// <summary>
/// Registers named rate-limit policies that each bring their own rejection handling. <see cref="RateLimiterOptions"/>
/// has ONE <c>OnRejected</c> for the whole app, so two features setting it would silently overwrite each other (the
/// API-key login and the 3D runners' API both limit). Every policy registered here shares one dispatcher that looks
/// up the rejected endpoint's policy and runs that policy's handler.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Adds <paramref name="policyName"/> (answering 429) with its own <paramref name="onRejected"/>.</summary>
    public static IServiceCollection AddRateLimitPolicy(
        this IServiceCollection services,
        string policyName,
        Func<HttpContext, RateLimitPartition<string>> partitioner,
        Func<OnRejectedContext, CancellationToken, ValueTask> onRejected)
    {
        services.Configure<RateLimitRejections>(r => r.Handlers[policyName] = onRejected);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = DispatchAsync;
            options.AddPolicy(policyName, partitioner);
        });
        return services;
    }

    /// <summary>Runs the handler of the policy that rejected the request (none known: just the 429).</summary>
    public static ValueTask DispatchAsync(OnRejectedContext context, CancellationToken ct)
    {
        var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        var handlers = context.HttpContext.RequestServices.GetService<IOptions<RateLimitRejections>>()?.Value;
        return policy is not null && handlers is not null && handlers.Handlers.TryGetValue(policy, out var handler)
            ? handler(context, ct)
            : ValueTask.CompletedTask;
    }
}
