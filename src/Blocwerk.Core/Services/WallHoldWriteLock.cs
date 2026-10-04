using System.Collections.Concurrent;

namespace Blocwerk.Core.Services;

/// <summary>
/// One writer of a wall's holds at a time (inside this server process; the app runs as one instance). The hold
/// shape cleanup, the outline upgrade and the wall-update commits all rewrite many holds from a snapshot they
/// planned earlier, so they must not interleave. The cleanup fails fast (it is an admin button: "busy, try again"),
/// the others wait a bounded time (a wall-update commit should simply follow a cleanup that is finishing).
/// </summary>
public static class WallHoldWriteLock
{
    /// <summary>How long a waiting writer gives a running one before it gives up.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    /// <summary>Takes the wall's lock or throws at once when another writer holds it.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="busyMessage">The message for the admin when it is busy.</param>
    /// <returns>Dispose to release.</returns>
    public static IDisposable TryAcquire(Guid wallId, string busyMessage)
    {
        var gate = Gates.GetOrAdd(wallId, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            throw new UserFacingException(busyMessage);
        }

        return new Releaser(gate);
    }

    /// <summary>Waits (bounded) for the wall's lock.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="timeout">How long to wait; <see cref="DefaultWait"/> when null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Dispose to release.</returns>
    public static async Task<IDisposable> AcquireAsync(Guid wallId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(wallId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(timeout ?? DefaultWait, ct))
        {
            throw new UserFacingException("Another update of this wall's holds is still running. Try again in a moment.");
        }

        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
