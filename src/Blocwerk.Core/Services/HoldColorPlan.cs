// <copyright file="HoldColorPlan.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// What the colour estimate would write, worked out without changing anything: one entry per hold that had no colour
/// when it was planned. <see cref="IHoldColorProposer.ApplyAsync"/> writes it, and looks at each hold again first.
/// </summary>
/// <param name="Entries">One entry per hold without a colour.</param>
public sealed record HoldColorPlan(IReadOnlyList<HoldColorPlanEntry> Entries);
