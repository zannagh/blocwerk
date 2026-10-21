using Microsoft.JSInterop;

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
/// <para>Presence comes from the browser, not the circuit: <c>edit-presence.js</c> reports real user input and the
/// page being hidden or shown through <see cref="PresenceReference"/>, which editors hand to <c>bwEditGuard.enter</c>
/// via <see cref="EditGuardInterop"/>.</para>
/// </remarks>
public sealed class CircuitEditActivity : IDisposable
{
    private readonly EditActivityRegistry registry;
    private readonly List<CircuitEditLease> leases = new();
    private readonly object gate = new();
    private DotNetObjectReference<EditPresenceInterop>? presenceReference;
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
        Pulse(nowOverride, EditSignal.Heartbeat, restore: false);

    /// <summary>
    /// A reconnect: the connection coming back up is proof the client is there, so leases the
    /// registry has already dropped are put back — with their original activity history, so one that
    /// timed out for want of a human stays gone.
    /// </summary>
    public int Resume(DateTimeOffset? nowOverride = null) =>
        Pulse(nowOverride, EditSignal.Heartbeat, restore: true);

    /// <summary>
    /// Real user input in the editor (pointer down or a drag, a key, the wheel), reported by <c>edit-presence.js</c>.
    /// Restarts both clocks, clears the hidden mark, and restores a lease that had expired while the user was away.
    /// </summary>
    /// <remarks>
    /// NOT called for inbound circuit traffic in general: a reconnect, a render acknowledgement or a JS reply is
    /// produced by a sleeping laptop's tab on every dark wake, which is how a lease once looked "active" all night.
    /// </remarks>
    public int RecordActivity(DateTimeOffset? nowOverride = null) =>
        Pulse(nowOverride, EditSignal.Input, restore: true);

    /// <summary>
    /// The editor's page was hidden, frozen or unloaded (<paramref name="visible"/> false), or shown again. Neither
    /// is presence: shown again only clears the hidden mark, and neither restores an expired lease.
    /// </summary>
    public int RecordVisibility(bool visible, DateTimeOffset? nowOverride = null) =>
        Pulse(nowOverride, visible ? EditSignal.Visible : EditSignal.Hidden, restore: false);

    /// <summary>
    /// The handle <c>edit-presence.js</c> reports through. Created once per circuit and disposed with it; null once
    /// the circuit is gone, so a late render cannot mint a reference nothing would ever dispose.
    /// </summary>
    public DotNetObjectReference<EditPresenceInterop>? PresenceReference
    {
        get
        {
            lock (gate)
            {
                return disposed ? null : presenceReference ??= DotNetObjectReference.Create(new EditPresenceInterop(this));
            }
        }
    }

    /// <summary>
    /// The leases that are at least <paramref name="after"/> old and have never had a presence report, each returned
    /// once. Read by the heartbeat, which logs them: such a lease looks idle to the deploy gate however busy its user is.
    /// </summary>
    public IReadOnlyList<EditActivityEntry> TakeSilentLeases(TimeSpan after, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        List<CircuitEditLease> current;
        lock (gate)
        {
            current = new List<CircuitEditLease>(leases);
        }

        return current.Select(l => l.TakeIfSilent(now, after)).OfType<EditActivityEntry>().ToList();
    }

    public void Dispose()
    {
        List<CircuitEditLease> toRelease;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            toRelease = new List<CircuitEditLease>(leases);
            leases.Clear();
            presenceReference?.Dispose();
            presenceReference = null;
        }

        foreach (var lease in toRelease)
        {
            lease.Dispose();
        }
    }

    internal void Forget(CircuitEditLease lease)
    {
        lock (gate)
        {
            leases.Remove(lease);
        }
    }

    private int Pulse(DateTimeOffset? nowOverride, EditSignal signal, bool restore)
    {
        List<CircuitEditLease> current;
        lock (gate)
        {
            if (disposed || leases.Count == 0)
            {
                return 0;
            }

            current = new List<CircuitEditLease>(leases);
        }

        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var touched = 0;
        foreach (var tracked in current)
        {
            if (tracked.Pulse(registry, now, signal, restore))
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
        var tracked = new CircuitEditLease(this, lease, template);

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
}
