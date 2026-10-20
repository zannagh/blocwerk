// <copyright file="HoldMergePreview.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>What merging two holds would do, before anything is written.</summary>
/// <param name="SuggestedKeptId">The hold that should stay: more boulders, then a longer lineage, then more panel links.</param>
/// <param name="Outline">The merged outline.</param>
/// <param name="Bridged">True when the two outlines did not touch and a bridge joins them.</param>
/// <param name="Boulders">Active boulders on each of the two holds (keyed by hold id).</param>
/// <param name="BouldersOnBoth">Active boulders that use both holds (flagged for review by the merge).</param>
public sealed record HoldMergePreview(
    Guid SuggestedKeptId,
    HoldToolOutline Outline,
    bool Bridged,
    IReadOnlyDictionary<Guid, int> Boulders,
    int BouldersOnBoth);
