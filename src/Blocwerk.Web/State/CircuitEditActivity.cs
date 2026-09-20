namespace Blocwerk.Web.State;

/// <summary>
/// Per-circuit wrapper over the singleton <see cref="EditActivityRegistry"/>. Editing components
/// inject this (scoped, like <see cref="SessionState"/>) and begin a session when they enter an
/// editing state. It tracks the leases it hands out so its own <see cref="Dispose"/> — invoked when
/// the circuit's scope is torn down, either gracefully or after the disconnect-retention period on
/// an abrupt drop — releases any that a component forgot to release, so "busy" can never get stuck.
/// </summary>
/// <remarks>
/// It also holds the template each lease was taken from, which is what lets
/// <see cref="Resume"/> and <see cref="RecordActivity"/> put a session back into the registry after
/// it has been expired out. Every editing component acquires ONCE, in its initialisation, so without
/// that a user who reconnected after the TTL would have gone on editing with no protection at all
/// and appeared nowhere in the health check.
/// </remarks>
public sealed class CircuitEditActivity : IDisposable
{
    private readonly EditActivityRegistry registry;
    private readonly List<TrackedLease> leases = new();
    private readonly object gate = new();
    private bool disposed;

    public CircuitEditActivity(EditActivityRegistry registry)
    {
        this.registry = registry;
    }

    /// <summary>Marks the boulder-create page as in-flight; dispose the result to clear it.</summary>
    public IDisposable BeginBoulderCreate(Guid wallId, Guid? userId, DateTimeOffset? nowOverride = null) =>
        Begin(EditKind.BoulderCreate, wallId, userId, nowOverride);

    /// <summary>Marks the boulder-revise page as in-flight; dispose the result to clear it.</summary>
    public IDisposable BeginBoulderRevise(Guid wallId, Guid? userId, DateTimeOffset? nowOverride = null) =>
        Begin(EditKind.BoulderRevise, wallId, userId, nowOverride);

    /// <summary>Marks an inline boulder edit as in-flight; dispose the result to clear it.</summary>
    public IDisposable BeginBoulderEdit(Guid wallId, Guid? userId, DateTimeOffset? nowOverride = null) =>
        Begin(EditKind.BoulderEdit, wallId, userId, nowOverride);

    /// <summary>Marks the wall-create page as in-flight; dispose the result to clear it.</summary>
    public IDisposable BeginWallCreate(Guid? userId, DateTimeOffset? nowOverride = null) =>
        Begin(EditKind.WallCreate, null, userId, nowOverride);

    /// <summary>Marks a wall as being edited; dispose the result to clear it.</summary>
    public IDisposable BeginWallEdit(Guid wallId, Guid? userId, DateTimeOffset? nowOverride = null) =>
        Begin(EditKind.WallEdit, wallId, userId, nowOverride);

    /// <summary>
    /// Re-stamps every lease this circuit holds, proving to the registry that the circuit is still
    /// connected. Called from <see cref="EditActivityCircuitHandler"/> on its heartbeat — and only
    /// while the circuit's connection is up, which is what makes the TTL able to catch a dead client.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT restore an expired lease: a heartbeat is the one signal an abandoned tab
    /// produces on its own, so letting it re-register would defeat
    /// <see cref="EditActivityPolicy.InactivityTimeToLive"/> outright.
    /// </remarks>
    /// <returns>How many leases were touched; zero for the overwhelming majority of circuits, which
    /// are only reading.</returns>
    public int Touch(DateTimeOffset? nowOverride = null) =>
        Pulse(nowOverride, userActivity: false, restore: false);

    /// <summary>
    /// A reconnect: the connection coming back up is proof the client is there, so leases the
    /// registry has already dropped are put back — with their original activity history, so one that
    /// timed out for want of a human stays gone.
    /// </summary>
    public int Resume(DateTimeOffset? nowOverride = null) =>
        Pulse(nowOverride, userActivity: false, restore: true);

    /// <summary>
    /// Inbound circuit activity: a real UI event or JS-to-.NET call. Restarts both clocks, and
    /// restores a lease that had expired while the user was away.
    /// </summary>
    public int RecordActivity(DateTimeOffset? nowOverride = null) =>
        Pulse(nowOverride, userActivity: true, restore: true);

    public void Dispose()
    {
        List<TrackedLease> toRelease;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            toRelease = new List<TrackedLease>(leases);
            leases.Clear();
        }

        foreach (var lease in toRelease)
        {
            lease.Dispose();
        }
    }

    private int Pulse(DateTimeOffset? nowOverride, bool userActivity, bool restore)
    {
        List<TrackedLease> current;
        lock (gate)
        {
            if (disposed || leases.Count == 0)
            {
                return 0;
            }

            current = new List<TrackedLease>(leases);
        }

        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var touched = 0;
        foreach (var tracked in current)
        {
            if (tracked.Pulse(registry, now, userActivity, restore))
            {
                touched++;
            }
        }

        return touched;
    }

    // nowOverride exists for the same reason it does on the registry: there is no time abstraction
    // in this codebase, and the TTLs are far too long for a test to wait out.
    private IDisposable Begin(EditKind kind, Guid? wallId, Guid? userId, DateTimeOffset? nowOverride)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var lease = registry.Acquire(kind, wallId, userId, now);
        var template = new EditActivityEntry(kind, wallId, userId, now, now, now);
        var tracked = new TrackedLease(this, lease, template);

        lock (gate)
        {
            if (disposed)
            {
                // The circuit is already gone; don't leak the entry.
                lease.Dispose();
                return tracked;
            }

            leases.Add(tracked);
        }

        return tracked;
    }

    private void Forget(TrackedLease lease)
    {
        lock (gate)
        {
            leases.Remove(lease);
        }
    }

    // Wraps a registry lease so disposing it also drops our tracking reference (avoids the
    // backstop list growing across many enter/leave-edit cycles in one long-lived circuit), and so
    // the session can be re-registered under a NEW lease id if the registry expired the old one
    // while the client was away.
    private sealed class TrackedLease : IDisposable
    {
        private readonly object gate = new();
        private CircuitEditActivity? owner;
        private IEditLease? lease;
        private EditActivityEntry template;

        public TrackedLease(CircuitEditActivity owner, IEditLease lease, EditActivityEntry template)
        {
            this.owner = owner;
            this.lease = lease;
            this.template = template;
        }

        public bool Pulse(
            EditActivityRegistry registry, DateTimeOffset now, bool userActivity, bool restore)
        {
            IEditLease? current;
            EditActivityEntry snapshot;
            lock (gate)
            {
                if (owner is null)
                {
                    return false;
                }

                current = lease;
                snapshot = template;
            }

            if (current is not null && registry.Touch(current.Id, now, userActivity))
            {
                Remember(snapshot with
                {
                    LastSeenUtc = now,
                    LastActivityUtc = userActivity ? now : snapshot.LastActivityUtc,
                });
                return true;
            }

            return restore && Restore(registry, now, userActivity, snapshot, current);
        }

        public void Dispose()
        {
            CircuitEditActivity? current;
            IEditLease? held;
            lock (gate)
            {
                current = owner;
                held = lease;
                owner = null;
                lease = null;
            }

            if (current is null)
            {
                return;
            }

            current.Forget(this);
            held?.Dispose();
        }

        private bool Restore(
            EditActivityRegistry registry,
            DateTimeOffset now,
            bool userActivity,
            EditActivityEntry snapshot,
            IEditLease? stale)
        {
            // The registry has dropped the entry. Hand it back the history it had, so the policy —
            // not this class — decides whether the session is genuinely over.
            var candidate = userActivity ? snapshot with { LastActivityUtc = now } : snapshot;
            var replacement = registry.Restore(candidate, now);
            if (replacement is null)
            {
                return false;
            }

            stale?.Dispose();

            lock (gate)
            {
                if (owner is null)
                {
                    replacement.Dispose();
                    return false;
                }

                lease = replacement;
                template = candidate with { LastSeenUtc = now };
            }

            return true;
        }

        private void Remember(EditActivityEntry updated)
        {
            lock (gate)
            {
                if (owner is not null)
                {
                    template = updated;
                }
            }
        }
    }
}
