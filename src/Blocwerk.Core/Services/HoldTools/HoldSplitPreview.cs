// <copyright file="HoldSplitPreview.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>What a cut would do, before anything is written.</summary>
/// <param name="Refusal">Why the cut is refused, in words for the person who drew it; null when it works.</param>
/// <param name="Larger">The larger part (keeps the hold's identity); null when refused.</param>
/// <param name="Smaller">The smaller part (becomes a new hold); null when refused.</param>
/// <param name="Boulders">The boulders on the hold, each of which picks a part (default: the larger one).</param>
/// <param name="PanelLinks">How many panel links (the same hold on a neighbouring photo) the hold has; one part takes them.</param>
public sealed record HoldSplitPreview(
    string? Refusal,
    HoldToolOutline? Larger,
    HoldToolOutline? Smaller,
    IReadOnlyList<HoldSplitBoulder> Boulders,
    int PanelLinks = 0);
