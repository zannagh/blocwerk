using Blocwerk.Web.State;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// One component's registration with the global keyboard dispatcher
/// (wwwroot/js/keyboard-shortcuts.js).
/// </summary>
/// <remarks>
/// Scopes form a stack in JS: the most recently registered one is offered a key first, and a scope
/// only ever sees keys it declared. That lets an editor claim its letter keys while the carousel
/// underneath still receives the arrows it declared, without either knowing about the other.
/// <para>
/// Create one per component instance, register it in <c>OnAfterRenderAsync(firstRender)</c>, and
/// dispose it from the component's own <c>DisposeAsync</c>. Interop here follows the repo
/// convention of swallowing <see cref="JSDisconnectedException"/> — a torn-down circuit is a normal
/// way for these calls to end, not a fault.
/// </para>
/// <para>
/// Registering is a convenience, never a reason to break a page: neither the gate nor the interop
/// call may throw out of <see cref="RegisterAsync"/> or <see cref="SetKeysAsync"/>, because those
/// are awaited straight from a component's <c>OnAfterRenderAsync</c>, where an exception becomes an
/// unhandled circuit error and the user loses the whole page. A failure here costs shortcuts only.
/// </para>
/// </remarks>
public sealed class KeyboardShortcutScope : IAsyncDisposable
{
    private readonly IJSRuntime js;
    private readonly Func<string, Task> handler;
    private readonly KeyboardShortcutGate? gate;
    private readonly string token = Guid.NewGuid().ToString("n");
    private DotNetObjectReference<KeyboardShortcutScope>? selfRef;
    private bool registered;

    /// <summary>Creates a scope for one component instance.</summary>
    /// <param name="js">The component's JS runtime.</param>
    /// <param name="handler">Invoked with the pressed key token.</param>
    /// <param name="gate">
    /// When supplied, registration is skipped entirely on a session that may not drive shortcuts (a
    /// kiosk tablet whose wall has not opted in). Omitting it keeps the unconditional behaviour.
    /// </param>
    public KeyboardShortcutScope(IJSRuntime js, Func<string, Task> handler, KeyboardShortcutGate? gate = null)
    {
        this.js = js;
        this.handler = handler;
        this.gate = gate;
    }

    /// <summary>
    /// Declares the keys this scope wants and pushes it onto the dispatcher stack. Calling it again
    /// re-pushes the scope to the top, so re-registering is how a surface reclaims priority.
    /// </summary>
    public async Task RegisterAsync(params string[] keys)
    {
        try
        {
            if (!await IsAllowedAsync())
            {
                return;
            }

            selfRef ??= DotNetObjectReference.Create(this);
            await js.InvokeVoidAsync("bwKeys.register", token, selfRef, keys);
            registered = true;
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone; nothing to register against.
        }
        catch (Exception)
        {
            // An interop round-trip that timed out, a disposed runtime, prerender: all of them cost
            // this scope its shortcuts and nothing more. Never let them reach OnAfterRenderAsync.
        }
    }

    /// <summary>
    /// Re-declares the key set without changing stack position, for surfaces whose available actions
    /// change from step to step (the wall update wizard's phases).
    /// </summary>
    public async Task SetKeysAsync(params string[] keys)
    {
        try
        {
            if (!await IsAllowedAsync())
            {
                return;
            }

            if (!registered)
            {
                await RegisterAsync(keys);
                return;
            }

            await js.InvokeVoidAsync("bwKeys.setKeys", token, keys);
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone; the registration died with it.
        }
        catch (Exception)
        {
            // Same contract as RegisterAsync: a failure here may cost keys, never the page.
        }
    }

    /// <summary>
    /// Whether this session may register at all. No gate means yes, so an ungated scope behaves
    /// exactly as it always did.
    /// </summary>
    /// <remarks>
    /// <see cref="KeyboardShortcutGate"/> is written so its task can never fault; the guard here is
    /// defence in depth for that contract, and it keeps the callers' own catch-alls from being the
    /// only thing standing between a gate problem and a dead circuit. A gate that cannot answer
    /// denies this scope, matching the gate's own fail-closed direction.
    /// </remarks>
    private async Task<bool> IsAllowedAsync()
    {
        if (gate is null)
        {
            return true;
        }

        try
        {
            return await gate.IsAllowedAsync(js);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [JSInvokable]
    public Task OnShortcut(string key) => handler(key);

    public async ValueTask DisposeAsync()
    {
        if (registered)
        {
            try
            {
                await js.InvokeVoidAsync("bwKeys.unregister", token);
            }
            catch (JSDisconnectedException)
            {
                // Circuit gone; the dispatcher went with it.
            }
        }

        selfRef?.Dispose();
        selfRef = null;
    }
}
