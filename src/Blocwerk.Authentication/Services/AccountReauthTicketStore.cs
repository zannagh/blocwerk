using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// In-memory <see cref="IAccountReauthTicketStore"/>. Registered as a singleton so a ticket outlives
/// the request that issued it and is visible to the circuit that spends it.
/// </summary>
/// <remarks>
/// Deliberately not persisted. A ticket is worthless after fifteen minutes and after one use, and
/// losing the whole set on a restart costs a user one extra click through their provider — whereas a
/// table would keep a record that somebody was about to delete their account. The app runs as a
/// single instance (as the wall cache and the kiosk throttle already assume); a second instance would
/// need this moved to a shared store.
/// </remarks>
public sealed class AccountReauthTicketStore : IAccountReauthTicketStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, AccountReauthTicket> tickets = new(StringComparer.Ordinal);

    public string Issue(Guid userId)
    {
        Sweep();

        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        tickets[ticket] = new AccountReauthTicket(userId, DateTimeOffset.UtcNow.Add(Lifetime));
        return ticket;
    }

    public bool Consume(string? ticket, Guid userId)
    {
        if (!TryGetLive(ticket, userId, out var key))
        {
            return false;
        }

        return tickets.TryRemove(key, out _);
    }

    private bool TryGetLive(string? ticket, Guid userId, out string key)
    {
        key = ticket ?? string.Empty;

        return !string.IsNullOrEmpty(ticket)
               && tickets.TryGetValue(ticket, out var entry)
               && entry.UserId == userId
               && entry.ExpiresAt > DateTimeOffset.UtcNow;
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, entry) in tickets)
        {
            if (entry.ExpiresAt <= now)
            {
                tickets.TryRemove(key, out _);
            }
        }
    }
}
