using System.Collections.Concurrent;

namespace Blocwerk.Web.State;

/// <summary>
/// App-wide, in-memory registry of unsaved, in-flight editing work. Registered as a singleton so it
/// spans every Blazor circuit on this instance. "Busy" here means at least one user is actively
/// creating a boulder or editing a wall — DB flags can't express that (a boulder row is only written
/// on save, and <c>Wall.UnderMaintenance</c> is a separate explicit admin toggle), so this signal
/// lives only in memory. The <c>busy</c> health check reads it to gate deploys.
/// </summary>
/// <remarks>
/// <para><b>Leases expire.</b> Disposing a lease is still the normal way a session ends, but it is
/// no longer the only one: an entry that has not been touched for
/// <see cref="EditActivityPolicy.LeaseTimeToLive"/> is invisible to every read and is dropped on the
/// next write. Without that, a circuit whose socket died without the server noticing held the deploy
/// gate closed until the process restarted — and blocked the deploy that would have restarted it.
/// <see cref="EditActivityCircuitHandler"/> is the other half: it touches a live circuit's leases so
/// a real editor is never expired out from under them.</para>
/// <para>Expiry is checked on READ and pruned on WRITE, the same shape as
/// <see cref="KioskPairingRegistry"/>, and every public method takes a <c>nowOverride</c> for the
/// same reason it does there — there is no time abstraction in this codebase.</para>
/// </remarks>
public sealed class EditActivityRegistry
{
    private readonly ConcurrentDictionary<Guid, EditActivityEntry> entries = new();

    /// <summary>True while any unexpired editing session is in flight.</summary>
    public bool IsBusy(DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        return entries.Values.Any(entry => !EditActivityPolicy.IsExpired(entry, now));
    }

    /// <summary>Number of unexpired in-flight editing sessions.</summary>
    public int Count(DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        return entries.Values.Count(entry => !EditActivityPolicy.IsExpired(entry, now));
    }

    /// <summary>
    /// Registers an editing session and returns a lease. Dispose the lease (or let the owning
    /// circuit's <see cref="CircuitEditActivity"/> dispose it on teardown) to clear the session.
    /// </summary>
    public IEditLease Acquire(EditKind kind, Guid? wallId, Guid? userId, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        Prune(now);

        var id = Guid.NewGuid();
        entries[id] = new EditActivityEntry(kind, wallId, userId, now, now, now);
        return new Lease(this, id);
    }

    /// <summary>
    /// Re-registers a session the registry has already dropped, from the template the owning circuit
    /// still holds, and returns a fresh lease — or <c>null</c> when the restored entry would itself
    /// be expired, in which case the session really is over.
    /// </summary>
    /// <remarks>
    /// This is how a reconnect gets its protection back without weakening
    /// <see cref="Touch"/>'s "a stale touch cannot resurrect" rule. The two cases are separated by
    /// WHO is asking: <see cref="Touch"/> is a blind stamp from whoever holds the id, so it refuses
    /// anything past the deadline; a restore carries the entry's own history forward
    /// (<see cref="EditActivityEntry.StartedUtc"/> and
    /// <see cref="EditActivityEntry.LastActivityUtc"/> are preserved) and is re-checked against the
    /// policy, so a lease that timed out because nobody was there cannot come back, while one that
    /// timed out because the socket was down comes back the instant the socket does.
    /// </remarks>
    public IEditLease? Restore(EditActivityEntry template, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        Prune(now);

        var restored = template with { LastSeenUtc = now };
        if (EditActivityPolicy.IsExpired(restored, now))
        {
            return null;
        }

        var id = Guid.NewGuid();
        entries[id] = restored;
        return new Lease(this, id);
    }

    /// <summary>
    /// Records that the circuit holding <paramref name="leaseId"/> is still alive, restarting the
    /// entry's TTL. Returns false when there is no such entry (it was released, or it had already
    /// expired and been pruned).
    /// </summary>
    /// <param name="userActivity">
    /// True only when the touch was caused by a human doing something in the circuit, which also
    /// restarts <see cref="EditActivityPolicy.InactivityTimeToLive"/>. A heartbeat passes false: it
    /// proves the socket is up, which is not the same claim.
    /// </param>
    public bool Touch(Guid leaseId, DateTimeOffset? nowOverride = null, bool userActivity = false)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        Prune(now);

        // A touch does NOT resurrect an entry that is already past its TTL: if the heartbeat was
        // that late, the circuit was not alive, and the deploy gate has already been told so.
        if (!entries.TryGetValue(leaseId, out var current) || EditActivityPolicy.IsExpired(current, now))
        {
            return false;
        }

        var next = userActivity
            ? current with { LastSeenUtc = now, LastActivityUtc = now }
            : current with { LastSeenUtc = now };

        return entries.TryUpdate(leaseId, next, current);
    }

    /// <summary>A point-in-time copy of the current, unexpired editing sessions.</summary>
    public IReadOnlyList<EditActivityEntry> Snapshot(DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        return entries.Values.Where(entry => !EditActivityPolicy.IsExpired(entry, now)).ToList();
    }

    private void Release(Guid id)
    {
        entries.TryRemove(id, out _);
    }

    /// <summary>
    /// Drops expired entries. Called on every write. The map holds one entry per open editor on this
    /// instance — tens at most — so a full pass is cheaper than deciding whether to do one.
    /// </summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in entries.Where(pair => EditActivityPolicy.IsExpired(pair.Value, now)))
        {
            entries.TryRemove(pair.Key, out _);
        }
    }

    private sealed class Lease : IEditLease
    {
        private EditActivityRegistry? owner;

        public Lease(EditActivityRegistry owner, Guid id)
        {
            this.owner = owner;
            Id = id;
        }

        public Guid Id { get; }

        public void Dispose()
        {
            // Idempotent: the first dispose releases and nulls the owner; later disposes no-op.
            var current = owner;
            if (current is null)
            {
                return;
            }

            owner = null;
            current.Release(Id);
        }
    }
}
