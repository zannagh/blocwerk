// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>Contracts <c>jobs/recognition/texture-match-request</c>: the arguments of <see cref="IPhotoTextureSession.Match"/> besides the images.</summary>
public sealed record TextureMatchRequest(double TextureMmPerPx, IReadOnlyList<double>? Seed, PhotoTextureAttempt? Attempt)
{
    /// <summary>The seed as the matcher takes it; throws when the request is off the contract.</summary>
    public double[]? CheckedSeed()
    {
        if (!(TextureMmPerPx > 0) || Seed is { Count: not 9 })
        {
            throw new RecognitionInputException("The texture-match request needs textureMmPerPx > 0 and a seed of 9 numbers or none.");
        }

        return Seed?.ToArray();
    }
}
