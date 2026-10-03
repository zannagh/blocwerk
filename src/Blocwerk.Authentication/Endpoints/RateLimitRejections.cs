// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.RateLimiting;

namespace Blocwerk.Authentication.Endpoints;

/// <summary>The rejection handler of each policy registered through <see cref="RateLimitPolicies"/>, by policy name.</summary>
public sealed class RateLimitRejections
{
    public Dictionary<string, Func<OnRejectedContext, CancellationToken, ValueTask>> Handlers { get; } = new(StringComparer.Ordinal);
}
