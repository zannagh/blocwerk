// <copyright file="RelocationProposalStatus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>
/// Where a "this hold moved" suggestion (<see cref="Entities.WallUpdateRelocationProposal"/>) stands in
/// the carryover review. Suggestions are never applied without a person accepting them.
/// </summary>
public enum RelocationProposalStatus
{
    /// <summary>Offered, not yet decided: both holds keep today's outcome (disappeared + new).</summary>
    Pending = 0,

    /// <summary>A person confirmed the new hold IS the old hold, moved (a Changed carry with lineage).</summary>
    Accepted = 1,

    /// <summary>A person rejected it: both holds keep today's outcome.</summary>
    Dismissed = 2,

    /// <summary>
    /// A person confirmed the new hold IS the old hold, unmoved — the photo shifted and the positional
    /// matcher missed it (a Carried carry with lineage Same; nothing is flagged).
    /// </summary>
    AcceptedAsSame = 3,
}
