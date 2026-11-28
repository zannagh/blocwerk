// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Blocwerk.Authentication.Apple;

/// <summary>
/// Holds an Apple authorization code's <see cref="AppleCallbackTicket"/> from the form POST until
/// <c>/account/callback</c> spends it. A code is only ever added after the POST's state and cookie checked out, so a
/// code found here is one this server's own Apple round trip returned.
/// </summary>
/// <remarks>A singleton, in memory, like the state store the other providers use; Apple's codes live five minutes.</remarks>
public sealed class AppleCallbackStore(TimeProvider? clock = null)
{
    /// <summary>How long a ticket waits for its callback.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private const int MaxCodeLength = 512;

    private readonly ConcurrentDictionary<string, AppleCallbackTicket> tickets = new(StringComparer.Ordinal);

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <summary>Keeps a ticket for <paramref name="code"/>; false for an unusable or already known code.</summary>
    public bool Add(string code, string state, string rawNonce, string? name)
    {
        if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength)
        {
            return false;
        }

        Sweep();
        return tickets.TryAdd(code, new AppleCallbackTicket(state, rawNonce, name, Clock.GetUtcNow().Add(Ttl)));
    }

    /// <summary>Removes and returns the live ticket for <paramref name="code"/>.</summary>
    public bool TryTake(string? code, [NotNullWhen(true)] out AppleCallbackTicket? ticket)
    {
        ticket = null;
        if (string.IsNullOrEmpty(code) || !tickets.TryRemove(code, out var found))
        {
            return false;
        }

        if (found.ExpiresAt < Clock.GetUtcNow())
        {
            return false;
        }

        ticket = found;
        return true;
    }

    private void Sweep()
    {
        var now = Clock.GetUtcNow();
        foreach (var entry in tickets)
        {
            if (entry.Value.ExpiresAt < now)
            {
                tickets.TryRemove(entry.Key, out _);
            }
        }
    }
}
