// <copyright file="GatedPhotoTextureMatcher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection.Gating;

/// <summary>Texture matcher sessions behind the <see cref="IImageRecognitionGate"/>: a session holds its slot until disposed.</summary>
public sealed class GatedPhotoTextureMatcher : IPhotoTextureMatcher
{
    private readonly IPhotoTextureMatcher inner;
    private readonly IImageRecognitionGate gate;

    /// <summary>Initializes a new instance of the <see cref="GatedPhotoTextureMatcher"/> class.</summary>
    /// <param name="inner">The matcher.</param>
    /// <param name="gate">The process-wide recognition gate.</param>
    public GatedPhotoTextureMatcher(IPhotoTextureMatcher inner, IImageRecognitionGate gate)
    {
        this.inner = inner;
        this.gate = gate;
    }

    /// <inheritdoc/>
    public IPhotoTextureSession OpenPhoto(byte[] encodedPhoto)
    {
        var lease = gate.Enter("texture-session");
        try
        {
            return new GatedPhotoTextureSession(inner.OpenPhoto(encodedPhoto), lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}
