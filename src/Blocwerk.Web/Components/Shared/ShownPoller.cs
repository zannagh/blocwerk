// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The refresh loop of a polling list: loads now and then every interval, but only while the list is on screen (the tab
/// is visible and its panel open, <c>element-shown.js</c>). A failed load (the list shows its error) and an exception of
/// the loop itself keep it going, waiting longer each time up to <see cref="MaxBackoff"/>. It takes its token once and
/// never touches the component's token source again.
/// </summary>
/// <param name="js">The circuit's JS runtime.</param>
/// <param name="element">The list's root element.</param>
/// <param name="invoke">The component's <c>InvokeAsync</c> (loads run on its dispatcher).</param>
/// <param name="load">Loads the list; false when it failed.</param>
/// <param name="logger">Logs a loop that could not refresh.</param>
internal sealed class ShownPoller(
    IJSRuntime js, Func<ElementReference> element, Func<Func<Task>, Task> invoke, Func<CancellationToken, Task<bool>> load, ILogger logger)
{
    /// <summary>The longest wait after failed refreshes.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>Runs until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(TimeSpan every, CancellationToken ct)
    {
        var wait = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, ct);
                var ok = true;
                await invoke(async () => ok = !await IsShownAsync(ct) || await load(ct));
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
