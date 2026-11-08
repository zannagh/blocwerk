// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// A texture matcher session whose matches may run elsewhere (a recognition runner). Awaited without a gate slot: only a
/// match it does in the app takes one, so waiting for a runner never keeps other recognition waiting.
/// </summary>
public interface IAsyncPhotoTextureSession : IPhotoTextureSession
{
    /// <summary>Like <see cref="IPhotoTextureSession.Match"/>.</summary>
    /// <param name="encodedTexture">The facet texture.</param>
    /// <param name="encodedMask">Its coverage mask, or null.</param>
    /// <param name="textureMmPerPx">The texture's resolution.</param>
    /// <param name="seed">A predicted photo px → texture px homography, or null.</param>
    /// <param name="attempt">Which deterministic variant to run, or null.</param>
    /// <param name="ct">Cancels the wait; a runner's job is then cancelled too.</param>
    /// <returns>The correspondences.</returns>
    Task<PhotoTextureMatch> MatchAsync(
        byte[] encodedTexture, byte[]? encodedMask, double textureMmPerPx, double[]? seed, PhotoTextureAttempt? attempt, CancellationToken ct);
}
