// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Pulse;

namespace Blocwerk.Web.State.Live;

/// <summary>
/// A job page's wake-up from <see cref="IWallPulse"/> (PR 1.7): its refresh loop waits on <see cref="WaitAsync"/>, which
/// returns on a pulse of one of its topics or, failing that, after the loop's own safety interval. The page re-reads
/// through its own service call, so a pulse only decides when, never what.
/// <para>
/// The circuit's viewer is read at every delivery (<see cref="WallCacheState.ActiveViewerOf"/>): a kiosk tablet is
/// <c>FeedViewer.Kiosk()</c>, a demoted or removed member stops receiving at once. Pulses arrive on the pulse loop and
/// only complete a task here; one that lands after <see cref="Dispose"/> is dropped.
/// </para>
/// </summary>
public sealed class PulseWake : IDisposable
{
    /// <summary>The shortest gap between two pulse wake-ups of one page, however often its jobs report.</summary>
    public static readonly TimeSpan DefaultMinGap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often a page re-reads a running job without a pulse: the safety net for a write that pulses nothing (or a
    /// circuit without a feed subscription). It replaces the old 1–5 s polls.
    /// </summary>
    public static readonly TimeSpan SafetyPoll = TimeSpan.FromSeconds(15);

    /// <summary>How often a page with nothing running re-reads anyway: a job started without a pulse reaching it shows.</summary>
    public static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(60);

    private readonly HashSet<string> topics;
    private readonly TimeSpan minGap;
    private readonly Lock gate = new();
    private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? subscription;
    private IDisposable? watch;
    private DateTimeOffset lastWake = DateTimeOffset.MinValue;
    private int disposed;

    private PulseWake(IEnumerable<string> topics, TimeSpan minGap)
    {
        this.topics = [.. topics];
        this.minGap = minGap;
    }

    /// <summary>The pulses this page received (tests).</summary>
    public int Received { get; private set; }

    /// <summary>
    /// Follows <paramref name="wallId"/>'s pulses of <paramref name="topics"/>. The circuit subscribes to the wall's feed
    /// first (that decides the viewer) and keeps that subscription while this lives.
    /// </summary>
    public static async Task<PulseWake> OpenAsync(
        WallCacheState cache, IWallPulse pulse, Guid wallId, IEnumerable<string> topics, TimeSpan? minGap = null)
    {
        var wake = new PulseWake(topics, minGap ?? DefaultMinGap) { watch = cache.Watch(wallId) };
        await cache.EnsureFeedAsync(wallId);
        var opened = pulse.Subscribe(wallId, () => cache.ActiveViewerOf(wallId), wake.OnPulse);
        lock (wake.gate)
        {
            if (Volatile.Read(ref wake.disposed) == 1)
            {
                opened.Dispose();
            }
            else
            {
                wake.subscription = opened;
            }
        }

        return wake;
    }

    /// <summary>
    /// Waits for a pulse, or for <paramref name="fallback"/> (<see cref="Timeout.InfiniteTimeSpan"/>: pulses only). Pulses
    /// that land while the page is still inside <see cref="DefaultMinGap"/> of its last wake-up join this one.
    /// </summary>
    /// <returns>True when a pulse woke it, false after the fallback.</returns>
    public async Task<bool> WaitAsync(TimeSpan fallback, CancellationToken ct)
    {
        Task pulsed;
        lock (gate)
        {
            pulsed = signal.Task;
        }

        bool woke;
        using (var delay = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            woke = await Task.WhenAny(pulsed, Task.Delay(fallback, delay.Token)) == pulsed;
            await delay.CancelAsync(); // the fallback's timer goes with the wait it lost
        }

        ct.ThrowIfCancellationRequested();
        if (woke && DateTimeOffset.UtcNow - lastWake < minGap)
        {
            await Task.Delay(minGap - (DateTimeOffset.UtcNow - lastWake), ct);
        }

        lock (gate)
        {
            if (signal.Task.IsCompleted)
            {
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        lastWake = DateTimeOffset.UtcNow;
        return woke;
    }

    /// <summary>Wakes the page's loop now, as a pulse would (after it started a job, so the loop's wait starts over).</summary>
    public void Nudge()
    {
        lock (gate)
        {
            signal.TrySetResult();
        }
    }

    public void Dispose()
    {
        IDisposable? opened;
        lock (gate)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1)
            {
                return;
            }

            opened = subscription;
            subscription = null;
        }

        opened?.Dispose();
        watch?.Dispose();
    }

    // On the pulse loop: never blocks, never touches the component.
    private void OnPulse(WallPulse pulse)
    {
        if (Volatile.Read(ref disposed) == 1 || !topics.Contains(pulse.Topic))
        {
            return;
        }

        lock (gate)
        {
            Received++;
            signal.TrySetResult();
        }
    }
}
