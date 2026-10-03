// <copyright file="MarkerRevisionEffectiveChange.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>The planner card's "Markers swapped on the wall" request.</summary>
/// <param name="Revision">The plan revision.</param>
/// <param name="EffectiveFrom">When its markers went up; null = planned, not yet on the wall.</param>
public sealed record MarkerRevisionEffectiveChange(int Revision, DateTimeOffset? EffectiveFrom);
