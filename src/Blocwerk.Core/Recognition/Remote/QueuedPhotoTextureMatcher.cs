// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// The texture matcher with a queued session for callers that await their matches (<see cref="OpenQueued"/>). The synchronous
/// <see cref="OpenPhoto"/> stays in the app, for the reason <see cref="QueuedHoldPresenceProbe"/> gives.
/// </summary>
public sealed class QueuedPhotoTextureMatcher(IPhotoTextureMatcher inProcess, RecognitionCaller caller) : IPhotoTextureMatcher
{
    /// <inheritdoc/>
    public IPhotoTextureSession OpenPhoto(byte[] encodedPhoto) => inProcess.OpenPhoto(encodedPhoto);

    /// <summary>A session whose matches are <c>texture-match</c> jobs; the photo is not decoded here (its size comes from its header).</summary>
    /// <exception cref="ArgumentException">The photo is too large or no image.</exception>
    public IAsyncPhotoTextureSession OpenQueued(byte[] encodedPhoto)
    {
        ImagePixelLimit.EnsureDecodable(encodedPhoto, nameof(encodedPhoto));
        var (width, height) = ImagePixelLimit.ReadSize(encodedPhoto) ?? throw new ArgumentException("The photo cannot be decoded.", nameof(encodedPhoto));
        return new QueuedPhotoTextureSession(encodedPhoto, width, height, inProcess, caller);
    }
}
