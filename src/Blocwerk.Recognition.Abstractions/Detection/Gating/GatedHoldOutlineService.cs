// <copyright file="GatedHoldOutlineService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection.Gating;

/// <summary>Outline sessions behind the <see cref="IImageRecognitionGate"/>: a session holds its slot until disposed.</summary>
public sealed class GatedHoldOutlineService : IHoldOutlineService
{
    private readonly IHoldOutlineService inner;
    private readonly IImageRecognitionGate gate;

    /// <summary>Initializes a new instance of the <see cref="GatedHoldOutlineService"/> class.</summary>
    /// <param name="inner">The outliner.</param>
    /// <param name="gate">The process-wide recognition gate.</param>
    public GatedHoldOutlineService(IHoldOutlineService inner, IImageRecognitionGate gate)
    {
        this.inner = inner;
        this.gate = gate;
    }

    /// <inheritdoc/>
    public IHoldOutlineSession OpenSession(byte[] encodedImage)
    {
        var lease = gate.Enter("outline-session");
        try
        {
            return new GatedHoldOutlineSession(inner.OpenSession(encodedImage), lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}
