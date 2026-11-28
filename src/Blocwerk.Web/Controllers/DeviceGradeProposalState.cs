// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>A boulder's grade proposal as the web boulder page shows it, with what the caller may do about it.</summary>
/// <param name="Proposal">The open proposal, or null.</param>
/// <param name="CanPropose">True unless the caller created the boulder (the web hides "Propose different grade" then).</param>
/// <param name="CanResolve">True for the creator, a setter of the boulder, or a wall admin (Accept and Reject).</param>
public sealed record DeviceGradeProposalState(DeviceGradeProposal? Proposal, bool CanPropose, bool CanResolve);
