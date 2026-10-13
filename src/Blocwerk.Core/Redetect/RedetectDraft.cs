// <copyright file="RedetectDraft.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Redetect;

/// <summary>A stored hold and a detection that are in the same place.</summary>
/// <param name="Existing">The stored hold.</param>
/// <param name="Detection">Index of the detection.</param>
/// <param name="Overlap">How they overlap.</param>
/// <param name="Verdict">Whether they are the same hold.</param>
internal sealed record RedetectCandidate(RedetectExisting Existing, int Detection, HoldOverlap Overlap, HoldSamenessVerdict Verdict);

/// <summary>The decisions of one re-detect while they are being collected.</summary>
internal sealed class RedetectDraft
{
    /// <summary>Gets the shape updates.</summary>
    public List<RedetectShapeUpdate> Shapes { get; } = [];

    /// <summary>Gets the shapes offered for hand-made outlines.</summary>
    public List<RedetectShapeUpdate> HandOffers { get; } = [];

    /// <summary>Gets, per detection a stored hold takes or is offered, that hold.</summary>
    public Dictionary<int, Guid> Owners { get; } = [];

    /// <summary>Gets the merges.</summary>
    public List<RedetectMerge> Merges { get; } = [];

    /// <summary>Gets the pairs on one detection that are likely one hold, but not sure enough to merge unasked.</summary>
    public List<RedetectMerge> LikelyMerges { get; } = [];

    /// <summary>Gets the pairs left for the review.</summary>
    public List<(Guid A, Guid B)> ForReview { get; } = [];

    /// <summary>Gets or sets the number of hand-made shapes that stay.</summary>
    public int HandShapesKept { get; set; }
}
