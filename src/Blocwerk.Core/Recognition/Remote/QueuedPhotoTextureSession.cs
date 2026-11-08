// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Compute;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// One photo matched against textures one <c>texture-match</c> job at a time (one facet per job). A match the app runs itself
/// (the synchronous <see cref="Match"/>, or the fallback) opens an in-process session for that match alone, so its gate slot is
/// given back before the next job is awaited.
/// </summary>
public sealed class QueuedPhotoTextureSession(byte[] photo, int width, int height, IPhotoTextureMatcher inProcess, RecognitionCaller caller)
    : IAsyncPhotoTextureSession
{
    /// <inheritdoc/>
    public int Width => width;

    /// <inheritdoc/>
    public int Height => height;

    /// <inheritdoc/>
    public PhotoTextureMatch Match(byte[] encodedTexture, byte[]? encodedMask, double textureMmPerPx, double[]? seed = null, PhotoTextureAttempt? attempt = null)
    {
        using var inApp = inProcess.OpenPhoto(photo);
        return inApp.Match(encodedTexture, encodedMask, textureMmPerPx, seed, attempt);
    }

    /// <inheritdoc/>
    public async Task<PhotoTextureMatch> MatchAsync(
        byte[] encodedTexture, byte[]? encodedMask, double textureMmPerPx, double[]? seed, PhotoTextureAttempt? attempt, CancellationToken ct)
    {
        var images = new List<byte[]> { photo, encodedTexture };
        var parts = new List<ComputeJobPart>
        {
            RecognitionParts.Request(new { version = 1, textureMmPerPx, seed, attempt }),
            RecognitionParts.Image("photo", "photo.jpg", photo),
            RecognitionParts.Image("texture", "texture.jpg", encodedTexture),
        };
        if (encodedMask is not null)
        {
            images.Add(encodedMask);
            parts.Add(RecognitionParts.Image("mask", "mask.png", encodedMask));
        }

        return await caller.RunAsync(RecognitionKinds.TextureMatch, parts, RecognitionParts.MemoryMb(images, together: true), RecognitionResults.TextureMatch, ct)
            ?? await Task.Run(() => Match(encodedTexture, encodedMask, textureMmPerPx, seed, attempt), ct);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing is held between matches.
    }
}
