// <copyright file="CarryoverScopeResult.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// The outcome of <see cref="CarryoverScope.Reconcile"/>: the carry decisions that may legitimately be
/// promoted, and the ones that were reset because they were made about a hold the review cannot show.
/// </summary>
/// <param name="Decisions">
/// The promote-safe decision set: every reviewable hold's verdict exactly as recorded, plus the matcher
/// default in place of each reset one. Same length and order as the input.
/// </param>
/// <param name="Reset">
/// The verdicts that were NOT applied, as they were recorded. Never empty silently — the caller must
/// surface the count to the user, because these are decisions the user made and cannot currently revisit.
/// </param>
public record CarryoverScopeResult(
    IReadOnlyList<CarryoverDecision> Decisions,
    IReadOnlyList<CarryoverDecision> Reset);
