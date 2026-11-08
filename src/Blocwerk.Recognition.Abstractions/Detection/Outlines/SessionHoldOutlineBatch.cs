// <copyright file="SessionHoldOutlineBatch.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// <see cref="IHoldOutlineBatch"/> in process: one <see cref="IHoldOutlineSession"/> per call, opened and disposed inside
/// one slot of the recognition gate (or the caller's slot, when its flow already holds one).
/// </summary>
public sealed class SessionHoldOutlineBatch : IHoldOutlineBatch
{
    /// <summary>The gate operation name of a call made outside any other recognition run.</summary>
    public const string Operation = "outline-holds";

    private readonly IHoldOutlineService outlines;
    private readonly IImageRecognitionGate? gate;

    /// <summary>Initializes a new instance of the <see cref="SessionHoldOutlineBatch"/> class.</summary>
    /// <param name="outlines">The outliner.</param>
    /// <param name="gate">The process-wide recognition gate; null runs ungated.</param>
    public SessionHoldOutlineBatch(IHoldOutlineService outlines, IImageRecognitionGate? gate)
    {
        ArgumentNullException.ThrowIfNull(outlines);
        this.outlines = outlines;
        this.gate = gate;
    }

    /// <inheritdoc/>
    public Task<HoldOutlineBatchResult> OutlineAsync(
        byte[] photo,
        IReadOnlyList<HoldSeed> seeds,
        HoldOutlineBatchOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(options);
        return gate.RecogniseAsync(Operation, () => Outline(photo, seeds, options), ct);
    }

    private HoldOutlineBatchResult Outline(byte[] photo, IReadOnlyList<HoldSeed> seeds, HoldOutlineBatchOptions options)
    {
        using var session = outlines.OpenSession(photo);
        IReadOnlyList<HoldOutlineResult> results = options.Joint ? session.OutlineEach(seeds) : [.. seeds.Select(session.Outline)];
        return new HoldOutlineBatchResult(session.ImageWidth, session.ImageHeight, results);
    }
}
