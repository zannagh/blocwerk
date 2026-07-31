using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.State;

/// <summary>
/// Per-circuit cache of the user's live climbing session. Components (the tab bar, the activity
/// page) read <see cref="Current"/> and subscribe to <see cref="Changed"/> so the "Session"
/// indicator updates the instant a session is started or ended, without a page reload.
/// </summary>
public sealed class SessionState
{
    private readonly ISessionService sessionService;
    private bool loaded;

    public SessionState(ISessionService sessionService)
    {
        this.sessionService = sessionService;
    }

    /// <summary>The live session, or null when none is open.</summary>
    public ClimbingSession? Current { get; private set; }

    public bool HasActiveSession => Current != null;

    /// <summary>Raised whenever <see cref="Current"/> changes so subscribers can re-render.</summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the active session once per circuit. Safe to call from every component's init;
    /// the underlying query only runs the first time unless <see cref="RefreshAsync"/> forces it.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (loaded)
        {
            return;
        }

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            Current = await sessionService.GetActiveSessionAsync();
        }
        catch (UnauthorizedAccessException)
        {
            // The layout loads this app-wide, including on anonymous pages where there is no
            // current user. No session is the right answer there, not a crash.
            Current = null;
        }

        loaded = true;
        Changed?.Invoke();
    }

    public async Task StartAsync(Guid wallId)
    {
        Current = await sessionService.StartSessionAsync(wallId);
        loaded = true;
        Changed?.Invoke();
    }

    public async Task EndAsync()
    {
        await sessionService.EndSessionAsync();
        Current = null;
        loaded = true;
        Changed?.Invoke();
    }
}
