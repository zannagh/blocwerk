using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The dedicated big-wall update surface: upload a fresh multi-photo capture, review the fresh
/// detection before anything is matched, carry the old curated
/// holds (and their boulders) onto the new centre, confirm the overlaps of each neighbour panel, then
/// promote it all live in one go. Orchestrates the phases and the <see cref="IWallBigUpdateService"/>
/// calls; each phase's UI lives in its own component. State here, markup in the .razor.
/// <para>
/// Every decision is written through to the wall's update session as it is made (see
/// BigWallUpdate.Session.cs), so closing the browser mid-flow loses nothing and reopening lands on the
/// step that was left rather than at the start.
/// </para>
/// </summary>
public partial class BigWallUpdate : IDisposable
{
    [Inject]
    private IWallBigUpdateService BigUpdate { get; set; } = default!;

    [Inject]
    private CircuitEditActivity EditActivity { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private KeyboardShortcutGate KeyGate { get; set; } = default!;

    // Mirrors _editLease onto the browser bwEditGuard so the maintenance watchdog holds back an
    // auto-reload while this unsaved big-wall update flow is open. See EditGuardInterop.
    private bool _editGuardActive;

    // The whole carryover flow is unsaved, in-flight wall work (staged panels, carryover decisions,
    // overlap links) until Apply promotes it. Hold a wall-edit busy lease for the component's mounted
    // lifetime so a deploy can't recreate the container mid-flow. Released in Dispose (WallDetail
    // unmounts this component when the flow closes), with the circuit-teardown backstop behind it.
    private IDisposable? _editLease;

    [Parameter] public Guid WallId { get; set; }

    /// <summary>Raised when the user leaves the flow (discarded or finished) so the parent can reload.</summary>
    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>Raised after a successful promote so the parent can refresh the now-updated wall.</summary>
    [Parameter] public EventCallback OnPromoted { get; set; }

    private WallUpdatePhase _phase = WallUpdatePhase.Loading;
    private string? _error;

    private BigUpdateSession? _session;
    private CarryoverOutcome? _outcome;
    private readonly List<NeighbourLinkSet> _linkSets = [];
    private int _neighbourIndex;

    protected override async Task OnInitializedAsync()
    {
        _editLease = EditActivity.BeginWallEdit(WallId, userId: null);

        // A prior update may still be in flight (staged panels persisted); offer to resume it. The probe
        // reads the staged state only — the matcher runs when the user actually resumes, after the
        // pre-match review, so a resumed update matches the corrected holds exactly like a fresh one.
        await ProbeAsync();
    }

    private NeighbourOverlap? CurrentNeighbour =>
        _session is not null && _neighbourIndex >= 0 && _neighbourIndex < _session.Neighbours.Count
            ? _session.Neighbours[_neighbourIndex]
            : null;

    // ---- Start -----------------------------------------------------------------
    private async Task DiscardAndRestart()
    {
        await SafeDiscard();
        if (_superseded)
        {
            return;
        }

        _session = null;
        _sessionInfo = null;
        _restored = null;
        _phase = WallUpdatePhase.Upload;
    }

    private Task OnUpload(IReadOnlyList<BigUpdatePhoto> photos) =>
        StartUpdateAsync(photos, takeOverExisting: false);

    /// <summary>
    /// Stages a fresh capture. A refusal (another admin already has an update open on this wall) is NOT
    /// an error to shrug off: it parks on the conflict prompt so the user chooses between continuing
    /// that update and explicitly throwing it away.
    /// </summary>
    private async Task StartUpdateAsync(IReadOnlyList<BigUpdatePhoto> photos, bool takeOverExisting)
    {
        _error = null;
        _phase = WallUpdatePhase.Working;
        try
        {
            _session = await BigUpdate.StageAsync(WallId, photos, takeOverExisting);
            _restored = null;
            _outcome = null;
            _linkSets.Clear();
            _neighbourIndex = 0;
            _pendingPhotos = null;
            _sessionInfo = await Sessions.GetOpenSessionAsync(WallId);
            _phase = WallUpdatePhase.Detected;
        }
        catch (WallUpdateSessionConflictException ex)
        {
            _pendingPhotos = photos;
            _conflict = ex.Existing;
            _phase = WallUpdatePhase.Upload;
        }
        catch (Exception ex)
        {
            _error = $"Could not start the update: {ex.Message}";
            _phase = WallUpdatePhase.Upload;
        }
    }

    // ---- Phase 1: review the raw detection BEFORE any matching runs ------------
    // Leaving this phase is what triggers the matcher: ResumeAsync re-reads the staged holds as the
    // user left them, so an added, moved or deleted hold feeds the carryover and overlap proposals
    // instead of being corrected after the proposals were already computed.
    private async Task OnDetectedContinue()
    {
        _error = null;
        _phase = WallUpdatePhase.Working;
        try
        {
            _session = await BigUpdate.ResumeAsync(WallId);
            await GoToPhaseAsync(WallUpdatePhase.Carryover);
        }
        catch (Exception ex)
        {
            _error = $"Could not match the detected holds: {ex.Message}";
            _phase = WallUpdatePhase.Detected;
        }
    }

    // ---- Carryover → neighbours ------------------------------------------------
    // The bulk save: the per-hold upserts already wrote every decision as it was made, but the
    // accepted/discarded new-hold split is only knowable here (it is derived from the CURRENT staged
    // set), so the whole carryover half is rewritten in one go on the way out.
    private async Task OnCarryoverContinue(CarryoverOutcome outcome)
    {
        _outcome = outcome;

        try
        {
            await Sessions.SaveCarryOutcomeAsync(
                WallId, outcome.Carryover, outcome.AcceptedNewCenterHoldIds, outcome.RemovedNewCenterHoldIds);
        }
        catch (Exception ex)
        {
            // STAY on the step. The phase cursor is its own tiny write and would almost certainly have
            // succeeded, leaving a resumed session parked at Neighbours with only the per-hold upserts
            // persisted — and the carry defaults then invert every unsaved verdict (a Removed hold comes
            // back and un-freezes its boulder, a Changed one loses its flag). Advancing past a failed
            // save is exactly how that silent inversion happens, so the error has to block the step.
            _error = $"Your carryover decisions could not be saved, so this step is not finished yet: {ex.Message}";
            _phase = WallUpdatePhase.Carryover;
            return;
        }

        // Only once the carryover is safely recorded does the neighbour walk start from the beginning.
        _linkSets.Clear();
        _neighbourIndex = 0;

        var next = (_session?.Neighbours.Count ?? 0) == 0 ? WallUpdatePhase.Touchup : WallUpdatePhase.Neighbours;
        await GoToPhaseAsync(next);
    }

    // ---- Phase 2: one neighbour overlap at a time ------------------------------
    private async Task OnNeighbourConfirm(PanelConfirmation confirmation)
    {
        if (CurrentNeighbour is { } n)
        {
            var set = new NeighbourLinkSet(
                n.PanelId, confirmation.Links.ToList(), confirmation.RemovedNeighborHoldIds.ToList());
            RecordLinkSet(set);
            await SaveNeighbourAsync(set);
        }

        await AdvanceNeighbourAsync();
    }

    /// <summary>
    /// The stepper's as-you-go save: the panel's outcome so far, written before it is finished, so an
    /// interrupted walk through one panel's proposals comes back with the confirmations already made.
    /// The set replaces the panel's rows whole, exactly as the final confirm does.
    /// </summary>
    private async Task OnNeighbourProgress(PanelConfirmation confirmation)
    {
        if (CurrentNeighbour is not { } n)
        {
            return;
        }

        await SaveNeighbourAsync(new NeighbourLinkSet(
            n.PanelId, confirmation.Links.ToList(), confirmation.RemovedNeighborHoldIds.ToList()));
    }

    /// <summary>
    /// Skipping a neighbour keeps its panel (it is promoted with the rest) but records no links — it
    /// never deletes the staged panel, so no holds are lost.
    /// <para>
    /// Skip means "not now", never "throw away what I already decided". A panel confirmed in an EARLIER
    /// session keeps its rows: clearing them here destroyed real, already-confirmed work with no prompt,
    /// and because the in-memory set was emptied to match, the screen agreed and the loss was invisible.
    /// Preserving beats confirming-first because the destructive reading of Skip has no use case — a user
    /// who wants a panel's links gone re-walks it and confirms an empty set, which rewrites its rows.
    /// </para>
    /// </summary>
    private async Task OnNeighbourSkip()
    {
        if (CurrentNeighbour is { } n)
        {
            // Only a panel with NOTHING recorded gets an empty set; anything already decided is left
            // exactly as it stands, in memory and in the session.
            var existing = _linkSets.FirstOrDefault(l => l.PanelId == n.PanelId) ?? RestoredFor(n.PanelId);
            RecordLinkSet(existing ?? new NeighbourLinkSet(n.PanelId, [], []));
        }

        await AdvanceNeighbourAsync();
    }

    // A panel is always decided WHOLE, so a re-walk replaces its set rather than appending a second one.
    private void RecordLinkSet(NeighbourLinkSet set)
    {
        _linkSets.RemoveAll(l => l.PanelId == set.PanelId);
        _linkSets.Add(set);
    }

    private async Task SaveNeighbourAsync(NeighbourLinkSet set)
    {
        try
        {
            await Sessions.SaveNeighbourLinkSetAsync(WallId, set);
        }
        catch (Exception ex)
        {
            _error = $"Could not save this panel's overlaps: {ex.Message}";
        }
    }

    private async Task AdvanceNeighbourAsync()
    {
        if (_session is not null && _neighbourIndex < _session.Neighbours.Count - 1)
        {
            _neighbourIndex++;
            await GoToPhaseAsync(WallUpdatePhase.Neighbours, _neighbourIndex);
        }
        else
        {
            await GoToPhaseAsync(WallUpdatePhase.Touchup);
        }
    }

    // ---- Phase 3: manual touch-up across every staged panel --------------------
    // Both continue and skip land on the optional shape step (BigWallUpdate.Shapes.cs); touch-up only
    // edits staged-hold geometry and never emits a carryover decision, so nothing folds back here.
    private Task OnTouchupContinue() => GoToPhaseAsync(WallUpdatePhase.Shapes);

    private Task OnTouchupSkip() => GoToPhaseAsync(WallUpdatePhase.Shapes);

    // The confirm-step summary and the promote itself live in BigWallUpdate.Promote.cs.

    private async Task Discard()
    {
        await SafeDiscard();
        if (_superseded)
        {
            // The notice replaces the flow; closing here would hide the reason the discard was refused.
            return;
        }

        await OnClose.InvokeAsync();
    }

    private async Task SafeDiscard()
    {
        try
        {
            // Scoped to the session this circuit is looking at: a stale Discard must not delete the
            // staged panels of the update that replaced it.
            await BigUpdate.DiscardAsync(WallId, _sessionInfo?.Id);
        }
        catch (WallUpdateSessionSupersededException)
        {
            MarkSuperseded();
        }
        catch (Exception ex)
        {
            _error = $"Discard failed: {ex.Message}";
        }
    }

    private Task Close() => OnClose.InvokeAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _editGuardActive = await EditGuardInterop.SyncAsync(JS, "wall-bigupdate", _editLease is not null, _editGuardActive);

        // The keyboard layer lives in BigWallUpdate.Keys.cs; it reconciles itself against the phase.
        await ReconcileShortcutsAsync(firstRender);
    }

    public void Dispose()
    {
        _editLease?.Dispose();

        DisposeShortcuts();

        if (_editGuardActive)
        {
            // Best-effort clear on close; a torn-down circuit no-ops (the safe failure).
            _ = EditGuardInterop.SyncAsync(JS, "wall-bigupdate", editing: false, active: _editGuardActive);
        }
    }
}
