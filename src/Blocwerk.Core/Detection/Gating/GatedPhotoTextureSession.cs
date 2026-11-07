// <copyright file="GatedPhotoTextureSession.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection.Gating;

/// <summary>A texture matcher session that gives its gate slot back when it is disposed.</summary>
internal sealed class GatedPhotoTextureSession : IPhotoTextureSession
{
    private readonly IPhotoTextureSession inner;
    private readonly IDisposable lease;

    /// <summary>Initializes a new instance of the <see cref="GatedPhotoTextureSession"/> class.</summary>
    /// <param name="inner">The session.</param>
    /// <param name="lease">The gate slot it holds.</param>
    public GatedPhotoTextureSession(IPhotoTextureSession inner, IDisposable lease)
    {
        this.inner = inner;
        this.lease = lease;
    }

    /// <inheritdoc/>
    public int Width => inner.Width;

    /// <inheritdoc/>
    public int Height => inner.Height;

    /// <inheritdoc/>
    public PhotoTextureMatch Match(byte[] encodedTexture, byte[]? encodedMask, double textureMmPerPx, double[]? seed = null, PhotoTextureAttempt? attempt = null) =>
        inner.Match(encodedTexture, encodedMask, textureMmPerPx, seed, attempt);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            inner.Dispose();
        }
        finally
        {
            lease.Dispose();
        }
    }
}
