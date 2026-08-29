using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>Which slice of the carryover the focused <see cref="CarryoverStepper"/> is walking.</summary>
public enum CarryReviewMode
{
    /// <summary>Old holds whose match moved — confirm or re-target.</summary>
    Moved = 0,

    /// <summary>Old holds the matcher could not re-find — keep (default) or mark removed.</summary>
    Removal = 1,

    /// <summary>Staged new-centre holds with no old twin — keep (default) or discard a false detection.</summary>
    New = 2,
}

/// <summary>One item in a focused carryover review: an old hold and/or its proposed new-centre twin.</summary>
public record CarryReviewItem(Guid? OldHoldId, Guid? NewHoldId);

/// <summary>A carryover decision the stepper wants the review to record for one old hold.</summary>
public record CarryDecisionChange(Guid OldHoldId, CarryKind Kind, Guid? NewHoldId);

/// <summary>A keep/discard decision the stepper wants the review to record for one staged new-centre hold.</summary>
public record NewDecisionChange(Guid NewHoldId, bool Discarded);

/// <summary>The carryover part of the final confirmation, handed up when Phase 1 is done.</summary>
public record CarryoverOutcome(
    List<CarryoverDecision> Carryover,
    List<Guid> AcceptedNewCenterHoldIds,
    List<Guid> RemovedNewCenterHoldIds);
