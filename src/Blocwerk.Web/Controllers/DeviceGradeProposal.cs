// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>An open grade proposal on a boulder.</summary>
/// <param name="Id">The proposal (accept or reject it by this id).</param>
/// <param name="ProposedGrade">The grade proposed.</param>
/// <param name="ProposedByUserId">Who proposed it.</param>
/// <param name="ProposedByName">Their display name.</param>
/// <param name="CreatedAt">When it was proposed (or last changed).</param>
public sealed record DeviceGradeProposal(Guid Id, string ProposedGrade, Guid ProposedByUserId, string ProposedByName, DateTimeOffset CreatedAt);
