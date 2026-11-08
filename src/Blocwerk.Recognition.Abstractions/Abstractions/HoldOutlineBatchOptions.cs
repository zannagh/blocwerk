// <copyright file="HoldOutlineBatchOptions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>How an <see cref="IHoldOutlineBatch"/> outlines its seeds.</summary>
public sealed record HoldOutlineBatchOptions
{
    /// <summary>Gets the options that outline every seed on its own, as <see cref="IHoldOutlineSession.Outline"/> does.</summary>
    public static HoldOutlineBatchOptions Separately { get; } = new();

    /// <summary>Gets the options that outline the seeds together, as <see cref="HoldOutlineSessionExtensions.OutlineEach"/> does.</summary>
    public static HoldOutlineBatchOptions Jointly { get; } = new() { Joint = true };

    /// <summary>
    /// Gets a value indicating whether the seeds are outlined together, sharing work between them and using the detector
    /// boxes of the seeds that have one, where the outliner can.
    /// </summary>
    public bool Joint { get; init; }
}
