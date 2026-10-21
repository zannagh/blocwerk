// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Pulse;

namespace Blocwerk.Web.State.Live;

/// <summary>
/// A job page's refresh loop on <see cref="PulseWake"/>: re-reads on a pulse of its topics, and after
/// <see cref="PulseWake.SafetyPoll"/> while <c>active</c> says a job of the page is still running. Started per wall,
/// disposed when the wall changes or the page goes. A refresh runs on the component's dispatcher (its <c>InvokeAsync</c>);
/// one that throws is logged and the loop carries on.
/// </summary>
public sealed class PulseLoop : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly ILogger logger;
    private PulseWake? wake;

    private PulseLoop(ILogger logger) => this.logger = logger;

    /// <summary>The wake this loop waits on, once open (tests).</summary>
    public PulseWake? Wake => wake;

    /// <summary>Opens the wall's pulses and starts the loop in the background.</summary>
    /// <param name="cache">The circuit's cache (its feed subscription decides the viewer).</param>
    /// <param name="pulse">The pulse channel.</param>
    /// <param name="wallId">The page's wall.</param>
    /// <param name="topics">The job kinds the page shows (<see cref="PulseTopics"/>).</param>
    /// <param name="active">True while a job of the page runs: only then does the safety poll re-read without a pulse.</param>
    /// <param name="invoke">The component's <c>InvokeAsync</c>.</param>
    /// <param name="refresh">Re-reads the page's jobs and renders.</param>
    /// <param name="logger">Logs a failed refresh.</param>
    /// <returns>The loop; dispose it to stop.</returns>
    public static PulseLoop Start(
        WallCacheState cache,
        IWallPulse pulse,
        Guid wallId,
        IEnumerable<string> topics,
        Func<bool> active,
        Func<Func<Task>, Task> invoke,
        Func<Task> refresh,
        ILogger logger)
    {
        var loop = new PulseLoop(logger);
        var token = loop.stop.Token;
        _ = Task.Run(() => loop.RunAsync(() => PulseWake.OpenAsync(cache, pulse, wallId, topics), active, invoke, refresh, token), CancellationToken.None);
        return loop;
    }

    /// <summary>Re-reads now (after the page started or stopped a job, so the wait starts over with the new state).</summary>
    public void Nudge() => wake?.Nudge();

    public void Dispose()
    {
        if (!stop.IsCancellationRequested)
        {
            stop.Cancel(); // not disposed: the loop may still be reading the token
        }

        wake?.Dispose();
    }

    private async Task RunAsync(
        Func<Task<PulseWake>> open, Func<bool> active, Func<Func<Task>, Task> invoke, Func<Task> refresh, CancellationToken ct)
    {
        try
        {
            wake = await open();
            if (ct.IsCancellationRequested)
            {
                wake.Dispose();
                return;
            }

            while (true)
            {
                await wake.WaitAsync(active() ? PulseWake.SafetyPoll : Timeout.InfiniteTimeSpan, ct);
                await RefreshAsync(invoke, refresh);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The page went or its wall changed.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A job page stopped following its pulses; it shows what it last read");
        }
    }

    private async Task RefreshAsync(Func<Func<Task>, Task> invoke, Func<Task> refresh)
    {
        try
        {
            await invoke(refresh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "A pulsed refresh failed; the next pulse or safety poll tries again");
        }
    }
}
