// <copyright file="BigWallUpdate.Session.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The wizard's durability layer: the probe that finds an in-flight update, the resume that lands on
/// the step (and neighbour) the user actually left with every decision they made restored, the phase
/// cursor writes, and the conflict path when a second admin starts an update on a wall that already
/// has one open. It lives apart from the phase orchestration in <see cref="BigWallUpdate"/> because it
/// is purely the persistence seam — it never decides anything, it only remembers and recalls — and
/// keeping it here holds the main partial under the project's file-size rule.
/// </summary>
public partial class BigWallUpdate
{
    [Inject]
    private IWallUpdateSessionService Sessions { get; set; } = default!;

    /// <summary>The open session's header: where to resume, and who has it open. Null when none.</summary>
    private WallUpdateSessionInfo? _sessionInfo;

    /// <summary>
    /// The decisions read back from the session, handed to the phase components so a reopened
    /// carryover review or overlap stepper reflects what the user decided rather than the matcher's
    /// fresh suggestions. Null on a first (never-resumed) pass through a phase.
    /// </summary>
    private BigUpdateConfirmation? _restored;

    /// <summary>Set when StageAsync refused because another admin's update is already open.</summary>
    private WallUpdateSessionInfo? _conflict;

    /// <summary>The photos the refused Start was carrying, kept so a takeover can retry with them.</summary>
    private IReadOnlyList<BigUpdatePhoto>? _pendingPhotos;

    /// <summary>
    /// Set once this circuit's update has been replaced by another admin's. Terminal: everything on this
    /// screen describes staged photos that no longer exist, so the only honest offer is to close and look
    /// again. Kept separate from <see cref="_error"/>, which is for things worth retrying.
    /// </summary>
    private bool _superseded;

    /// <summary>
    /// Parks the wizard on the "this update was replaced" notice. Called wherever a promote or discard is
    /// refused because the wall has moved on — never by guessing, always off the service's refusal.
    /// </summary>
    private void MarkSuperseded()
    {
        _superseded = true;
        _error = null;
        _conflict = null;
        _pendingPhotos = null;
    }

    // ---- Probe -----------------------------------------------------------------
    private async Task ProbeAsync()
    {
        try
        {
            _sessionInfo = await Sessions.GetOpenSessionAsync(WallId);
        }
        catch (Exception)
        {
            // A probe failure must never block the flow; the staged probe below still decides.
            _sessionInfo = null;
        }

        // The staged probe is the older, session-row-independent truth: an update staged before
        // sessions existed has panels but no session header, and must still be resumable.
        try
        {
            _session = await BigUpdate.GetStagedAsync(WallId);
            _phase = WallUpdatePhase.ResumePrompt;
        }
        catch (Exception)
        {
            _sessionInfo = null;
            _phase = WallUpdatePhase.Upload;
        }
    }

    // ---- Resume ----------------------------------------------------------------

    /// <summary>The persisted step to resume at; <see cref="WallUpdatePhase.Detected"/> when unknown.</summary>
    private WallUpdatePhase ResumeTarget => WallUpdateResume.TargetFor(_sessionInfo?.Phase);

    /// <summary>What the Resume button promises, so the user knows where they are going back to.</summary>
    private string ResumeLabel => WallUpdateResume.LabelFor(ResumeTarget);

    /// <summary>
    /// Picks the flow back up where it was left. Everything before the carryover has nothing persisted
    /// to restore (the staged holds ARE the state), so it lands on the pre-match review as before;
    /// anything later re-runs the matcher — which is deterministic from the staged rows, hence never
    /// persisted — and then lays the user's recorded decisions back over it.
    /// </summary>
    private async Task ResumeExistingAsync()
    {
        var target = ResumeTarget;
        if (target == WallUpdatePhase.Detected)
        {
            _phase = WallUpdatePhase.Detected;
            return;
        }

        _error = null;
        _phase = WallUpdatePhase.Working;
        try
        {
            _session = await BigUpdate.ResumeAsync(WallId);
            await RestoreDecisionsAsync();
            _neighbourIndex = ClampNeighbourIndex(_sessionInfo?.NeighbourIndex ?? 0);
            _phase = target;
            if (target == WallUpdatePhase.Confirm)
            {
                await LoadShapeSummaryAsync();
            }
        }
        catch (Exception ex)
        {
            // Falling back to the pre-match review is the safe failure: nothing is lost, the user just
            // walks forward again, and their decisions are still on the session.
            _error = $"Could not restore where you left off: {ex.Message}";
            _phase = WallUpdatePhase.Detected;
        }
    }

    /// <summary>
    /// Reads every decision the session holds and seeds the wizard's own state from it. The carryover
    /// outcome and the confirmed link sets come straight back; the per-phase components get the same
    /// payload through <c>_restored</c> so they seed themselves rather than starting from the matcher.
    /// </summary>
    private async Task RestoreDecisionsAsync()
    {
        _restored = await Sessions.GetDecisionsAsync(WallId);

        // Resuming at Neighbours, Touchup or Confirm never re-walks the carryover, so nothing else
        // would ever look at these rows again before the promote reads them straight out of the
        // session. Neutralise the ones about holds no surface can show HERE, so the reset is in effect
        // (and its notice is armed) on every resume target, not only when the carryover is re-walked.
        var reconciled = ReconcileCarryScope(_restored.Carryover);
        RecordScopeResets(reconciled.Reset);
        _restored = _restored with { Carryover = reconciled.Decisions.ToList() };

        _outcome = new CarryoverOutcome(
            _restored.Carryover,
            _restored.AcceptedNewCenterHoldIds,
            _restored.RemovedNewCenterHoldIds);
        _linkSets.Clear();
        _linkSets.AddRange(_restored.Neighbours);
    }

    private int ClampNeighbourIndex(int index)
    {
        var count = _session?.Neighbours.Count ?? 0;
        if (count == 0)
        {
            return 0;
        }

        return Math.Clamp(index, 0, count - 1);
    }

    /// <summary>The confirmed outcome for one staged panel, if the session already holds one.</summary>
    private NeighbourLinkSet? RestoredFor(Guid panelId) =>
        _restored?.Neighbours.FirstOrDefault(n => n.PanelId == panelId);

    // ---- Phase cursor ----------------------------------------------------------

    /// <summary>
    /// Moves the wizard AND the persisted resume cursor together, so the two can never disagree. A
    /// failed write is deliberately swallowed: losing the cursor costs the user one extra walk
    /// forward, whereas refusing to advance would trap them on a step they are done with.
    /// </summary>
    private async Task GoToPhaseAsync(WallUpdatePhase phase, int neighbourIndex = 0)
    {
        _phase = phase;
        try
        {
            _sessionInfo = await Sessions.SetPhaseAsync(WallId, phase, neighbourIndex);
        }
        catch (Exception)
        {
            // No open session (a legacy staged update) or a lost write: the flow carries on in memory.
        }
    }

    // ---- Who has it open (a session belongs to the wall, not to one admin) ------
    private bool HasOwnerInfo => _sessionInfo is not null || _conflict is not null;

    private WallUpdateSessionInfo? OwnerInfo => _conflict ?? _sessionInfo;

    private string StartedByLine =>
        OwnerInfo is { } s
            ? $"Started by {s.CreatedByName ?? "another admin"} on {s.CreatedAt.ToLocalTime():d MMM yyyy, HH:mm}"
            : string.Empty;

    /// <summary>Only worth showing when someone other than the starter touched it last.</summary>
    private string? LastActiveLine
    {
        get
        {
            if (OwnerInfo is not { } s || s.LastActiveByUserId is null
                || s.LastActiveByUserId == s.CreatedByUserId)
            {
                return null;
            }

            return $"Last continued by {s.LastActiveByName ?? "another admin"} "
                + $"on {s.UpdatedAt.ToLocalTime():d MMM yyyy, HH:mm}";
        }
    }

    // ---- Conflict path ---------------------------------------------------------

    /// <summary>Takes over the refused start's slot: the other update is discarded, explicitly.</summary>
    private async Task TakeOverAsync()
    {
        if (_pendingPhotos is not { } photos)
        {
            _conflict = null;
            return;
        }

        _conflict = null;
        await StartUpdateAsync(photos, takeOverExisting: true);
    }

    /// <summary>Abandons the refused start and continues the update that is already open instead.</summary>
    private async Task ResumeConflictingAsync()
    {
        _sessionInfo = _conflict;
        _conflict = null;
        _pendingPhotos = null;

        try
        {
            _session = await BigUpdate.GetStagedAsync(WallId);
        }
        catch (Exception ex)
        {
            _error = $"Could not open the update in progress: {ex.Message}";
            _phase = WallUpdatePhase.Upload;
            return;
        }

        await ResumeExistingAsync();
    }

    private void CancelConflict()
    {
        _conflict = null;
        _pendingPhotos = null;
        _phase = WallUpdatePhase.Upload;
    }
}
