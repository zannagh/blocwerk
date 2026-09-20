// <copyright file="BigWallUpdate.Promote.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The last step of the wizard: the confirm-step summary counts and the promote. It lives apart from
/// the phase orchestration in <see cref="BigWallUpdate"/> because the promote has one rule of its own
/// worth isolating — what it sends is the SESSION's record of the decisions, topped up with the
/// matcher's warp dictionaries, so an interrupted update promotes exactly like an uninterrupted one.
/// </summary>
public partial class BigWallUpdate
{
    // ---- Finish ----------------------------------------------------------------
    private int CarriedCount => _outcome?.Carryover.Count(d => d.Kind == CarryKind.Carried) ?? 0;
    private int ChangedCount => _outcome?.Carryover.Count(d => d.Kind == CarryKind.Changed) ?? 0;
    private int RemovedCount => _outcome?.Carryover.Count(d => d.Kind == CarryKind.Removed) ?? 0;
    private int NewKeptCount => _outcome?.AcceptedNewCenterHoldIds.Count ?? 0;
    private int LinkCount => _linkSets.Sum(l => l.Links.Count);

    /// <summary>
    /// Promotes what the SESSION says was decided, not what this circuit happens to remember — the two
    /// are the same in an uninterrupted run and only the former survives a resume. The persisted
    /// payload deliberately carries null warp dictionaries (they are deterministic matcher output, not
    /// a decision), so they are copied back on from the re-run session before promoting; without them
    /// PromoteAsync silently falls back to cloning every blind-carried hold at its stale OLD position.
    /// </summary>
    private async Task Apply()
    {
        // The Apply button is disabled while a reset notice is unread; the Enter binding is not, so the
        // gate lives here too rather than only on the markup.
        if (ScopeResetsPending)
        {
            _phase = WallUpdatePhase.Confirm;
            return;
        }

        _error = null;
        _phase = WallUpdatePhase.Working;
        try
        {
            var confirmation = await BuildConfirmationAsync();

            // BuildConfirmationAsync re-reads the session rows, so it can surface out-of-scope verdicts
            // this circuit had not seen yet (another admin's decisions, or a resume that skipped the
            // carryover). They are already neutralised in the payload — but "not silently" is the whole
            // rule, so the promote waits one round trip for the user to read the notice.
            if (ScopeResetsPending)
            {
                _phase = WallUpdatePhase.Confirm;
                return;
            }

            await BigUpdate.PromoteAsync(WallId, confirmation, _sessionInfo?.Id);
            _outcome = new CarryoverOutcome(
                confirmation.Carryover, confirmation.AcceptedNewCenterHoldIds, confirmation.RemovedNewCenterHoldIds);
            _linkSets.Clear();
            _linkSets.AddRange(confirmation.Neighbours);
            _phase = WallUpdatePhase.Done;
            await OnPromoted.InvokeAsync();
        }
        catch (WallUpdateSessionSupersededException)
        {
            MarkSuperseded();
        }
        catch (Exception ex)
        {
            _error = $"Could not apply the update: {ex.Message}";
            _phase = WallUpdatePhase.Confirm;
        }
    }

    /// <summary>
    /// Builds the payload to promote, and refuses outright when this circuit is no longer looking at the
    /// wall's in-flight update.
    /// <para>
    /// The check is here as well as in the service because the wizard can say something the service
    /// cannot: the staged photos on the wall are not the ones on this screen. It also decides which
    /// source of decisions is legitimate — a LEGACY staged update (no session row at all) is the only
    /// case where this circuit's own memory may stand in. The old rule — "no persisted carryover rows,
    /// so use whatever is in memory" — could not tell a legacy update from a freshly-taken-over one, and
    /// that is precisely how a stale Apply came to promote another admin's capture under decisions made
    /// about photos that had already been deleted.
    /// </para>
    /// </summary>
    private async Task<BigUpdateConfirmation> BuildConfirmationAsync()
    {
        var open = await Sessions.GetOpenSessionAsync(WallId);
        if (open is not null && _sessionInfo is { } mine && open.Id != mine.Id)
        {
            throw new WallUpdateSessionSupersededException(WallId, mine.Id, open.Id);
        }

        BigUpdateConfirmation persisted;
        if (open is null)
        {
            // No session row on the wall at all: an update staged before sessions existed. Nothing was
            // ever persisted for it, so this circuit's own state is all there is — and, having no
            // session, nobody else can have replaced it underneath us.
            persisted = new BigUpdateConfirmation(
                _outcome?.Carryover ?? [],
                _outcome?.AcceptedNewCenterHoldIds ?? [],
                _outcome?.RemovedNewCenterHoldIds ?? [],
                _linkSets);
        }
        else
        {
            // The session is ours: what it records IS the decision set. Empty means "nothing decided",
            // which the promote's reconcile handles by carrying every old hold forward — never a reason
            // to substitute in-memory state.
            persisted = await Sessions.GetDecisionsAsync(WallId);
        }

        var matched = await MatchedSessionAsync();

        // The promote boundary is the last place a verdict about an unshowable hold can be stopped, and
        // the only one every path passes through — a resume straight to Confirm never re-walks the
        // carryover, so the rows reach here exactly as they were recorded. Neutralising them here as
        // well as on restore means no ordering of resume, take-over or re-read can let one through.
        var scoped = CarryoverScope.Reconcile(matched, persisted.Carryover);
        RecordScopeResets(scoped.Reset);

        return persisted with
        {
            Carryover = scoped.Decisions.ToList(),
            CarriedWarpPositions = matched.CarriedWarpPositions,
            CarriedWarpShapes = matched.CarriedWarpShapes,
        };
    }

    /// <summary>
    /// The session carrying the matcher's warp dictionaries, which come from the matcher and are never
    /// persisted. <c>ResumeAsync</c> produced them on the way into the carryover (fresh run) or on
    /// resume (re-run), so this circuit normally already has them; the matcher is only re-run when it
    /// somehow does not. The result is also what the scope reconcile reads the reviewable panel from.
    /// </summary>
    private async Task<BigUpdateSession> MatchedSessionAsync()
    {
        if (_session is { CarriedWarpPositions: not null } current)
        {
            return current;
        }

        _session = await BigUpdate.ResumeAsync(WallId);
        return _session;
    }

    // ---- Out-of-scope carry verdicts (see CarryoverScope) ----------------------

    /// <summary>
    /// The recorded verdicts that were reset because they are about old holds on a panel no review
    /// surface displays, and whether the user has acknowledged them. Kept on the wizard (not on the
    /// carryover step) because the resume paths that skip that step are exactly the dangerous ones.
    /// </summary>
    private readonly List<CarryoverDecision> _scopeResets = [];

    private bool _scopeResetsAcknowledged;

    private bool ScopeResetsPending => _scopeResets.Count > 0 && !_scopeResetsAcknowledged;

    private CarryoverScopeResult ReconcileCarryScope(IReadOnlyList<CarryoverDecision> decisions) =>
        _session is { } session
            ? CarryoverScope.Reconcile(session, decisions)
            : new CarryoverScopeResult(decisions, []);

    /// <summary>
    /// Folds newly-found resets in. Anything not already listed re-arms the acknowledgement, so a
    /// verdict discovered on a later read cannot ride out on an acknowledgement of earlier ones.
    /// </summary>
    private void RecordScopeResets(IReadOnlyList<CarryoverDecision> reset)
    {
        foreach (var decision in reset)
        {
            if (_scopeResets.Any(r => r.OldHoldId == decision.OldHoldId))
            {
                continue;
            }

            _scopeResets.Add(decision);
            _scopeResetsAcknowledged = false;
        }
    }

    private void AcknowledgeScopeResets() => _scopeResetsAcknowledged = true;
}
