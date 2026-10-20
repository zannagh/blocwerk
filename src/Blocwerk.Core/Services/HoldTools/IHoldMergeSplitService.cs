// <copyright file="IHoldMergeSplitService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Services;

/// <summary>
/// The wall editor's merge and split tools: two holds that are really one become one (outline union, boulders, stacks,
/// lineage and panel links carried over), and one hold that is really two is cut in two. Each write is ONE wall-scoped
/// journal batch, so it can be undone exactly and is replayed with the wall's other changes.
/// </summary>
public interface IHoldMergeSplitService
{
    /// <summary>What merging the two holds would look like and which one should stay.</summary>
    Task<HoldMergePreview> PreviewMergeAsync(Guid wallId, Guid firstId, Guid secondId, CancellationToken ct = default);

    /// <summary>Merges <paramref name="removedId"/> into <paramref name="keptId"/>.</summary>
    Task<HoldToolResult> MergeAsync(Guid wallId, Guid keptId, Guid removedId, CancellationToken ct = default);

    /// <summary>What cutting the hold along the stroke would do. Never throws for a bad cut; says why instead.</summary>
    Task<HoldSplitPreview> PreviewSplitAsync(Guid wallId, Guid holdId, IReadOnlyList<NormalizedPoint> stroke, CancellationToken ct = default);

    /// <summary>
    /// Cuts the hold in two; the boulders in <paramref name="bouldersOnSmallerPart"/> move to the new, smaller part.
    /// The hold's panel links (the same hold on a neighbouring photo) go with the smaller part when
    /// <paramref name="panelLinksToSmaller"/> says so; null means with the part most of the boulders use.
    /// </summary>
    Task<HoldToolResult> SplitAsync(
        Guid wallId,
        Guid holdId,
        IReadOnlyList<NormalizedPoint> stroke,
        IReadOnlyCollection<Guid> bouldersOnSmallerPart,
        bool? panelLinksToSmaller = null,
        CancellationToken ct = default);

    /// <summary>Undoes a merge or split of this wall.</summary>
    Task<ChangeJournalRevertResult> UndoAsync(Guid wallId, Guid batchId, CancellationToken ct = default);
}
