// <copyright file="HoldColorPlanEntry.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Holds.Colors;

namespace Blocwerk.Core.Services;

/// <summary>The planned colour of one hold that has none.</summary>
/// <param name="HoldId">The hold.</param>
/// <param name="Color">The colour to write, or null when the hold stays without one.</param>
/// <param name="IsProposal">The colour is the detection's proposal (also when a linked hold's proposal is taken over).</param>
/// <param name="FromLinked">The colour is the one the same hold already has on another panel.</param>
/// <param name="Verdict">Why the hold stays without a colour, when it does.</param>
public sealed record HoldColorPlanEntry(Guid HoldId, string? Color, bool IsProposal, bool FromLinked, HoldColorVerdict Verdict);
