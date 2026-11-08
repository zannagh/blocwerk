// <copyright file="GatedHoldPresenceProbe.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection.Gating;

/// <summary>The presence probe behind the <see cref="IImageRecognitionGate"/>.</summary>
public sealed class GatedHoldPresenceProbe : IHoldPresenceProbe
{
    private readonly IHoldPresenceProbe inner;
    private readonly IImageRecognitionGate gate;

    /// <summary>Initializes a new instance of the <see cref="GatedHoldPresenceProbe"/> class.</summary>
    /// <param name="inner">The probe.</param>
    /// <param name="gate">The process-wide recognition gate.</param>
    public GatedHoldPresenceProbe(IHoldPresenceProbe inner, IImageRecognitionGate gate)
    {
        this.inner = inner;
        this.gate = gate;
    }

    /// <inheritdoc/>
    public IReadOnlyList<double?> Score(byte[] oldImage, byte[] newImage, IReadOnlyList<PresenceQuery> queries)
    {
        using var lease = gate.Enter("presence-probe");
        return inner.Score(oldImage, newImage, queries);
    }
}
