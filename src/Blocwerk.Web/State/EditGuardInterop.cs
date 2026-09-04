using Microsoft.JSInterop;

namespace Blocwerk.Web.State;

/// <summary>
/// Thin, exception-swallowing bridge to the browser-side <c>window.bwEditGuard</c> ref-count that the
/// maintenance watchdog reads before it takes the kiosk auto-reconnect reload. Editor components mirror
/// their <see cref="CircuitEditActivity"/> busy lease onto that guard through <see cref="SyncAsync"/>,
/// so an auto-reload is held back while unsaved boulder/wall edits live only in circuit state.
/// </summary>
public static class EditGuardInterop
{
    /// <summary>
    /// Drives the browser ref-count for <paramref name="key"/> to match whether an edit lease is held.
    /// <paramref name="active"/> is the component's record of the last state it pushed; pass it in and
    /// store the returned value back. Every call is best-effort. On success it returns the intended
    /// state. On failure it returns the state that actually took effect in the browser: a failed
    /// <c>exit</c> reports the intended (cleared) state so the transition is not retried while the
    /// browser guard stays set — the safe over-hold — whereas a failed <c>enter</c> reports the
    /// PRIOR state unchanged so the caller retries the (never-applied) increment on the next render.
    /// </summary>
    public static async ValueTask<bool> SyncAsync(IJSRuntime js, string key, bool editing, bool active)
    {
        if (editing == active)
        {
            return active;
        }

        try
        {
            await js.InvokeVoidAsync(editing ? "bwEditGuard.enter" : "bwEditGuard.exit", key);
            return editing;
        }
        catch (Exception)
        {
            // Interop is unavailable during prerender and after a circuit drop.
            if (editing)
            {
                // Failed ENTER: bwEditGuard.enter never incremented the browser guard, so an
                // auto-reload is NOT actually held back over this unsaved editor — and with the
                // shared key another instance's exit could clear a guard we never set. Do NOT record
                // success: return the prior state so the caller retries the enter on the next render.
                return active;
            }

            // Failed EXIT: the browser guard was never decremented, so it stays set and keeps
            // holding back an auto-reload over work the torn-down circuit just lost — the SAFE
            // failure. Report the intended (cleared) state so the caller stops retrying; the browser
            // drops the whole guard on the next real page load anyway.
            return editing;
        }
    }
}
