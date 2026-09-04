namespace Blocwerk.Core.Services;

/// <summary>
/// Wakes the <see cref="BetaVideoNormalizationService"/> the moment a clip is queued, so a fresh
/// upload or an admin re-encode does not wait out the worker's fallback poll. The queue itself is
/// the database (the clip's <c>EncodingStatus</c>); this is only the "there is work now" nudge, so a
/// missed signal costs latency, never correctness — the worker re-polls on a timer regardless.
/// </summary>
/// <remarks>A singleton, shared by the producers (upload/admin) and the single consumer (the worker).</remarks>
public sealed class BetaVideoNormalizationSignal
{
    private readonly SemaphoreSlim gate = new(0, 1);

    /// <summary>Nudges the worker. Coalesces: several signals before the worker wakes count as one.</summary>
    public void Signal()
    {
        try
        {
            gate.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled and not yet consumed — the pending wake covers this one too.
        }
    }

    /// <summary>Waits for a signal or until <paramref name="timeout"/> elapses (the fallback poll).</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        gate.WaitAsync(timeout, cancellationToken);
}
