// <copyright file="RevisePrefill.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>What the Revise editor opens with: the ticked current holds and the panel to show first.</summary>
/// <param name="Picks">The current holds to tick, most prominent mark first.</param>
/// <param name="UnmappedHoldIds">The boulder's own holds that map to no current hold (gone, deleted, or nothing close enough).</param>
/// <param name="StartPanelId">The live panel carrying most of the picks, or null when nothing was picked.</param>
public record RevisePrefill(IReadOnlyList<RevisePick> Picks, IReadOnlyList<Guid> UnmappedHoldIds, Guid? StartPanelId);
