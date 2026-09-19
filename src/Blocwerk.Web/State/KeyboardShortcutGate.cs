using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace Blocwerk.Web.State;

/// <summary>
/// Answers, once per circuit, whether this session may drive the global keyboard shortcuts
/// (wwwroot/js/keyboard-shortcuts.js).
/// </summary>
/// <remarks>
/// A kiosk is a shared, unattended tablet: a passing keyboard must not be able to reach the editor's
/// destructive shortcuts, so a kiosk session gets shortcuts only when the wall's admin opted that
/// wall in (<see cref="Blocwerk.Core.Entities.Wall.AllowKioskKeyboardShortcuts"/>). Every other
/// session is allowed, unconditionally — this must never take shortcuts away from an ordinary user.
/// <para>
/// A SUCCESSFUL answer is resolved at most once per circuit and cached as a TASK, not a value: three
/// components (the carousel, the photo editor, the big-wall wizard) commonly ask in the same render
/// batch, and sharing the in-flight task means one database read instead of three. A FAILED answer
/// is never cached: kiosk circuits are long-lived and reconnect rather than reload, so one blip must
/// not leave an opted-in tablet without shortcuts for days. The next caller simply re-resolves.
/// </para>
/// <para>
/// No call into this class can ever fault the cached task: every step is exception-proof and the
/// browser push is strictly best-effort, so awaiting the gate from a component lifecycle method can
/// at worst cost shortcuts, never the page.
/// </para>
/// <para>
/// On a resolution the answer is also pushed to the browser via <c>bwKeys.setEnabled</c>, because
/// the JS gate also covers the built-in "?" reference shortcut, which has no C# scope. The one
/// exception is a session whose kind we could not establish: see <see cref="IsAllowedForSessionAsync"/>.
/// </para>
/// </remarks>
public sealed class KeyboardShortcutGate
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ILogger<KeyboardShortcutGate> logger;
    private readonly IKioskContext? kioskContext;
    private readonly object gate = new();
    private Task<bool>? resolution;

    /// <summary>Creates the per-circuit gate.</summary>
    /// <param name="dbContextFactory">
    /// The scoped, kiosk-stamped factory — see <see cref="KioskScopedDbContextFactory"/>.
    /// </param>
    /// <param name="logger">Records a failed wall read; the gate itself never throws.</param>
    /// <param name="kioskContext">
    /// Absent in hosts with no HTTP layer (tests, tooling), which simply means "never a kiosk".
    /// </param>
    public KeyboardShortcutGate(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ILogger<KeyboardShortcutGate> logger,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.logger = logger;
        this.kioskContext = kioskContext;
    }

    /// <summary>
    /// Whether keyboard shortcuts may be registered on this circuit. Safe to call from several
    /// components at once: the first call starts the resolution and the rest await the same task.
    /// The returned task never faults.
    /// </summary>
    public Task<bool> IsAllowedAsync(IJSRuntime js)
    {
        TaskCompletionSource<bool> starter;

        lock (gate)
        {
            // Cache the task rather than the bool so concurrent callers in one render batch share a
            // single resolution (and a single DB read) instead of racing three of them.
            if (resolution is { } cached)
            {
                return cached;
            }

            starter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            resolution = starter.Task;
        }

        // Started OUTSIDE the lock: the resolution's synchronous prefix (kiosk/auth state, creating
        // a DbContext) must never run while the lock is held. The lock only guards the field.
        _ = ResolveAsync(js, starter);
        return starter.Task;
    }

    private async Task ResolveAsync(IJSRuntime js, TaskCompletionSource<bool> starter)
    {
        var allowed = false;

        try
        {
            var outcome = await IsAllowedForSessionAsync();
            allowed = outcome.Allowed;

            if (!outcome.Resolved)
            {
                Forget(starter.Task);
            }

            if (outcome.Push)
            {
                await PushToBrowserAsync(js, outcome.Allowed);
            }
        }
        catch (Exception ex)
        {
            // Defence in depth: nothing above is expected to throw, and if it somehow does we still
            // complete (never fault) the shared task, and we do not persist the failure.
            logger.LogWarning(ex, "The keyboard-shortcut gate failed to resolve; shortcuts stay off for this call.");
            Forget(starter.Task);
            allowed = false;
        }

        starter.TrySetResult(allowed);
    }

    /// <summary>Drops a failed resolution so the next caller retries it, unless it was replaced already.</summary>
    private void Forget(Task<bool> failed)
    {
        lock (gate)
        {
            if (ReferenceEquals(resolution, failed))
            {
                resolution = null;
            }
        }
    }

    /// <summary>
    /// Resolves the session. <c>Resolved</c> says whether the answer may be cached for the circuit;
    /// <c>Push</c> says whether the browser gate should be told about it.
    /// </summary>
    private async Task<(bool Allowed, bool Resolved, bool Push)> IsAllowedForSessionAsync()
    {
        if (kioskContext is null)
        {
            return (true, true, true);
        }

        bool isKiosk;
        Guid? wallId;

        try
        {
            // Idempotent and cached; it only ever re-reads when the circuit handler has not primed it.
            await kioskContext.InitializeAsync();
            isKiosk = kioskContext.IsKiosk;
            wallId = kioskContext.KioskWallId;
        }
        catch (Exception ex)
        {
            // We never established WHAT this session is, so fail closed for this call only: the C#
            // scopes stay unregistered, but we deliberately do not push setEnabled(false), because
            // this is most likely an ordinary desktop user and that push would wipe the dispatcher
            // stack and kill the built-in "?" app-wide for a risk that exists only on kiosks.
            // Not cached either, so the next caller re-asks once auth state has settled.
            logger.LogWarning(ex, "Could not establish the session kind for the keyboard-shortcut gate; shortcuts stay off for this call.");
            return (false, false, false);
        }

        if (!isKiosk)
        {
            return (true, true, true);
        }

        var wall = await KioskWallAllowsAsync(wallId);
        return (wall.Allowed, wall.Resolved, true);
    }

    private async Task<(bool Allowed, bool Resolved)> KioskWallAllowsAsync(Guid? wallId)
    {
        // A kiosk we cannot identify must not get shortcuts. That is a definitive answer, not a
        // failure, so it is cacheable.
        if (wallId is not { } id || id == Guid.Empty)
        {
            return (false, true);
        }

        try
        {
            // Read straight through the scoped factory rather than IWallService: the flag is one
            // column, while GetWallAsync materialises holds and boulders for it. The read is legal
            // for a kiosk because this factory stamps BlocwerkDbContext.KioskWallId with the
            // tablet's own wall, and leaving CurrentUserId at Guid.Empty opens the membership half
            // of the wall query filter the same way an anonymous kiosk view does — so the query can
            // still only ever see the one wall the device is registered to.
            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            var allowed = await db.Walls
                .Where(w => w.Id == id)
                .Select(w => (bool?)w.AllowKioskKeyboardShortcuts)
                .FirstOrDefaultAsync();

            // No row (filtered away, deleted, re-registered device) is the same unidentified kiosk.
            return (allowed ?? false, true);
        }
        catch (Exception ex)
        {
            // Deny now, but do not cache: a connection blip must not leave an opted-in tablet
            // without shortcuts for the lifetime of a circuit that reconnects instead of reloading.
            logger.LogWarning(ex, "Could not read the kiosk keyboard-shortcut flag for wall {WallId}.", id);
            return (false, false);
        }
    }

    /// <summary>Best-effort only: this must never throw, and never fault the cached resolution.</summary>
    private async Task PushToBrowserAsync(IJSRuntime js, bool allowed)
    {
        try
        {
            await js.InvokeVoidAsync("bwKeys.setEnabled", allowed);
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone; there is no dispatcher left to tell.
        }
        catch (Exception ex)
        {
            // Everything else is equally survivable and MUST be swallowed: a round-trip that
            // exceeded the default interop timeout (TaskCanceledException), a disposed runtime,
            // prerender ("JS interop calls cannot be issued at this time"), or a JS-side error.
            // The C# gate still holds without the browser ever hearing about it.
            logger.LogDebug(ex, "Could not push the keyboard-shortcut gate to the browser.");
        }
    }
}
