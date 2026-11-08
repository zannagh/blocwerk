// <copyright file="PhotoTextureAttempt.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// One deterministic variant of a photo × texture match (<see cref="IPhotoTextureSession.Match"/>). The coarse
/// stage is chaotic in its inputs: the same photo and texture file matched with the texture's bounds 0.3 % larger
/// gave 131 instead of 1214 inliers (the copy wall's right panel photo on its main wall). A weak match is therefore
/// tried again with other RANSAC samples and each coarse scale on its own, and the best kept.
/// </summary>
/// <param name="RansacSeed">
/// Seeds the order in which the coarse and refit RANSAC see the correspondences, i.e. which samples they draw; 0 keeps
/// the order they were found in.
/// </param>
/// <param name="CoarsePass">The coarse scale pass to refine (an index into the matcher's passes), or null for the one with the most coarse inliers.</param>
/// <param name="ScaleJitter">A factor on that pass's coarse texture resolution (1 = as configured).</param>
public sealed record PhotoTextureAttempt(int RansacSeed, int? CoarsePass, double ScaleJitter)
{
    /// <summary>Gets the plain match: found order, the better coarse pass, configured scales.</summary>
    public static PhotoTextureAttempt Default { get; } = new(0, null, 1);
}
