// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.State.Live;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The refresh loop of a polling list: loads now and then every interval, but only while the list is on screen (the tab
/// is visible and its panel open, <c>element-shown.js</c>). A failed load (the list shows its error) and an exception of
/// the loop itself keep it going, waiting longer each time up to <see cref="MaxBackoff"/>. It takes its token once and
/// never touches the component's token source again.
/// <para>
/// With a <see cref="PulseWake"/> (a wall's job list), a shown list re-reads on its jobs' pulses and every
/// <see cref="PulseWake.SafetyPoll"/> instead of every interval; a hidden one keeps checking every interval, which costs
/// no query, so opening its panel still shows fresh jobs at once.
/// </para>
/// </summary>
/// <param name="js">The circuit's JS runtime.</param>
/// <param name="element">The list's root element.</param>
/// <param name="invoke">The component's <c>InvokeAsync</c> (loads run on its dispatcher).</param>
/// <param name="load">Loads the list; false when it failed.</param>
/// <param name="logger">Logs a loop that could not refresh.</param>
/// <param name="wake">The list's pulses, once open; null (or returning null) polls every interval.</param>
internal sealed class ShownPoller(
    IJSRuntime js,
    Func<ElementReference> element,
    Func<Func<Task>, Task> invoke,
    Func<CancellationToken, Task<bool>> load,
    ILogger logger,
    Func<PulseWake?>? wake = null)
{
    /// <summary>The longest wait after failed refreshes.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>Runs until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(TimeSpan every, CancellationToken ct)
    {
        var wait = TimeSpan.Zero;
        var loaded = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await WaitAsync(loaded && wait == every ? wake?.Invoke() : null, wait, ct);
                var ok = true;
                loaded = false;
                await invoke(async () => ok = !await IsShownAsync(ct) || (loaded = await load(ct)));
                wait = ok ? every : TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, Math.Max(every.Ticks, wait.Ticks * 2)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "A polling list could not refresh");
                wait = MaxBackoff;
            }
        }
    }

    // Shown and loaded fine: a pulse or the safety poll; else the interval (or the backoff).
    private static async Task WaitAsync(PulseWake? pulses, TimeSpan wait, CancellationToken ct)
    {
        if (pulses is null)
        {
            await Task.Delay(wait, ct);
            return;
        }

        await pulses.WaitAsync(PulseWake.SafetyPoll, ct);
    }

    private async Task<bool> IsShownAsync(CancellationToken ct)
    {
        try
        {
            return await js.InvokeAsync<bool>("bwShown", ct, element());
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }
}
