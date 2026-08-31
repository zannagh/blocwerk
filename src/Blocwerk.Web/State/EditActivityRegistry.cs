using System.Collections.Concurrent;

namespace Blocwerk.Web.State;

/// <summary>
/// App-wide, in-memory registry of unsaved, in-flight editing work. Registered as a singleton so it
/// spans every Blazor circuit on this instance. "Busy" here means at least one user is actively
/// creating a boulder or editing a wall — DB flags can't express that (a boulder row is only written
/// on save, and <c>Wall.UnderMaintenance</c> is a separate explicit admin toggle), so this signal
/// lives only in memory. The <c>busy</c> health check reads it to gate deploys.
/// </summary>
public sealed class EditActivityRegistry
{
    private readonly ConcurrentDictionary<Guid, EditActivityEntry> entries = new();

    /// <summary>True while any editing session is in flight.</summary>
    public bool IsBusy => !entries.IsEmpty;

    /// <summary>Number of in-flight editing sessions.</summary>
    public int Count => entries.Count;

    /// <summary>
    /// Registers an editing session and returns a lease. Dispose the lease (or let the owning
    /// circuit's <see cref="CircuitEditActivity"/> dispose it on teardown) to clear the session.
    /// </summary>
    public IEditLease Acquire(EditKind kind, Guid? wallId, Guid? userId)
    {
        var id = Guid.NewGuid();
        entries[id] = new EditActivityEntry(kind, wallId, userId, DateTimeOffset.UtcNow);
        return new Lease(this, id);
    }

    /// <summary>A point-in-time copy of the current editing sessions.</summary>
    public IReadOnlyList<EditActivityEntry> Snapshot() => entries.Values.ToList();

    private void Release(Guid id)
    {
        entries.TryRemove(id, out _);
    }

    private sealed class Lease : IEditLease
    {
        private readonly Guid id;
        private EditActivityRegistry? owner;

        public Lease(EditActivityRegistry owner, Guid id)
        {
            this.owner = owner;
            this.id = id;
        }

        public void Dispose()
        {
            // Idempotent: the first dispose releases and nulls the owner; later disposes no-op.
            var current = owner;
            if (current is null)
            {
                return;
            }

            owner = null;
            current.Release(id);
        }
    }
}
