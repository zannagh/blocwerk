using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>Which slice of the carryover the focused <see cref="CarryoverStepper"/> is walking.</summary>
public enum CarryReviewMode
{
    /// <summary>Carried-over old holds — accept, flag as physically changed, re-target, or remove.</summary>
    Carried = 0,

    /// <summary>Old holds the matcher could not re-find — keep (default) or mark removed.</summary>
    Removal = 1,

    /// <summary>Staged new-centre holds with no old twin — keep (default) or discard a false detection.</summary>
    New = 2,

    /// <summary>
    /// The attention queue: only the FEW old holds the matcher was not confident it re-found
    /// (no proposal, low confidence, or a high warp residual), residual-ranked. This is the default
    /// focused queue — the user confirms the exceptions and walks past the rest. Everything not in
    /// this queue is auto-carried. Behaves like <see cref="Carried"/> in the stepper (accept, flag
    /// changed, re-target, remove).
    /// </summary>
    Uncertain = 3,
}

/// <summary>
/// One item in a focused carryover review: an old hold and/or its proposed new-centre twin, plus the
/// persisted <see cref="CarryKind"/> so a reopened stepper can seed its per-hold state from the parent
/// decisions (the single source of truth) instead of starting blank.
/// </summary>
public record CarryReviewItem(Guid? OldHoldId, Guid? NewHoldId, CarryKind Kind = CarryKind.Carried);

/// <summary>A carryover decision the stepper wants the review to record for one old hold.</summary>
public record CarryDecisionChange(Guid OldHoldId, CarryKind Kind, Guid? NewHoldId);

/// <summary>A keep/discard decision the stepper wants the review to record for one staged new-centre hold.</summary>
public record NewDecisionChange(Guid NewHoldId, bool Discarded);

/// <summary>The carryover part of the final confirmation, handed up when Phase 1 is done.</summary>
public record CarryoverOutcome(
    List<CarryoverDecision> Carryover,
    List<Guid> AcceptedNewCenterHoldIds,
    List<Guid> RemovedNewCenterHoldIds);

/// <summary>
/// One row of the carryover review's "Possibly moved" list: a relocation suggestion with both holds
/// resolved on the panes it is drawn over.
/// </summary>
/// <param name="Suggestion">The persisted suggestion.</param>
/// <param name="Number">The row's 1-based number, also drawn as the badge on both panes.</param>
/// <param name="OldHold">The disappeared old hold (on the "before" photo).</param>
/// <param name="NewHold">The appeared staged hold (on the "after" photo).</param>
/// <param name="OldLabel">The old hold's label, as the rest of the review names it.</param>
/// <param name="OnBoulder">Whether a live boulder uses the old hold — accepting flags it for revision.</param>
/// <param name="Claimed">Whether another old hold's verdict already points at the new hold.</param>
public record RelocationRow(
    RelocationSuggestion Suggestion,
    int Number,
    PanelHold OldHold,
    PanelHold NewHold,
    string OldLabel,
    bool OnBoulder,
    bool Claimed);
