namespace Blocwerk.Web.State;

/// <summary>
/// Per-circuit wrapper over the singleton <see cref="EditActivityRegistry"/>. Editing components
/// inject this (scoped, like <see cref="SessionState"/>) and begin a session when they enter an
/// editing state. It tracks the leases it hands out so its own <see cref="Dispose"/> — invoked when
/// the circuit's scope is torn down, either gracefully or after the disconnect-retention period on
/// an abrupt drop — releases any that a component forgot to release, so "busy" can never get stuck.
/// </summary>
public sealed class CircuitEditActivity : IDisposable
{
    private readonly EditActivityRegistry registry;
    private readonly List<IEditLease> leases = new();
    private readonly object gate = new();
    private bool disposed;

    public CircuitEditActivity(EditActivityRegistry registry)
    {
        this.registry = registry;
    }

    /// <summary>Marks the boulder-create page as in-flight; dispose the result to clear it.</summary>
    public IDisposable BeginBoulderCreate(Guid wallId, Guid? userId) =>
        Begin(EditKind.BoulderCreate, wallId, userId);

    /// <summary>Marks the boulder-revise page as in-flight; dispose the result to clear it.</summary>
    public IDisposable BeginBoulderRevise(Guid wallId, Guid? userId) =>
        Begin(EditKind.BoulderRevise, wallId, userId);

    /// <summary>Marks an inline boulder edit as in-flight; dispose the result to clear it.</summary>
    public IDisposable BeginBoulderEdit(Guid wallId, Guid? userId) =>
        Begin(EditKind.BoulderEdit, wallId, userId);

    /// <summary>Marks the wall-create page as in-flight; dispose the result to clear it.</summary>
    public IDisposable BeginWallCreate(Guid? userId) =>
        Begin(EditKind.WallCreate, null, userId);

    /// <summary>Marks a wall as being edited; dispose the result to clear it.</summary>
    public IDisposable BeginWallEdit(Guid wallId, Guid? userId) =>
        Begin(EditKind.WallEdit, wallId, userId);

    public void Dispose()
    {
        List<IEditLease> toRelease;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            toRelease = new List<IEditLease>(leases);
            leases.Clear();
        }

        foreach (var lease in toRelease)
        {
            lease.Dispose();
        }
    }

    private IDisposable Begin(EditKind kind, Guid? wallId, Guid? userId)
    {
        var lease = registry.Acquire(kind, wallId, userId);
        lock (gate)
        {
            if (disposed)
            {
                // The circuit is already gone; don't leak the entry.
                lease.Dispose();
                return lease;
            }

            leases.Add(lease);
        }

        return new TrackedLease(this, lease);
    }

    private void Forget(IEditLease lease)
    {
        lock (gate)
        {
            leases.Remove(lease);
        }
    }

    // Wraps a registry lease so disposing it also drops our tracking reference (avoids the
    // backstop list growing across many enter/leave-edit cycles in one long-lived circuit).
    private sealed class TrackedLease : IDisposable
    {
        private readonly IEditLease inner;
        private CircuitEditActivity? owner;

        public TrackedLease(CircuitEditActivity owner, IEditLease inner)
        {
            this.owner = owner;
            this.inner = inner;
        }

        public void Dispose()
        {
            var current = owner;
            if (current is null)
            {
                return;
            }

            owner = null;
            current.Forget(inner);
            inner.Dispose();
        }
    }
}
