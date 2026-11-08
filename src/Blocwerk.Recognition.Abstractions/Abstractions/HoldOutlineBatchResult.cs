// <copyright file="HoldOutlineBatchResult.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>The outlines of one <see cref="IHoldOutlineBatch.OutlineAsync"/> call.</summary>
/// <param name="ImageWidth">The decoded photo width in pixels.</param>
/// <param name="ImageHeight">The decoded photo height in pixels.</param>
/// <param name="Outlines">One outline per seed, in seed order.</param>
public sealed record HoldOutlineBatchResult(int ImageWidth, int ImageHeight, IReadOnlyList<HoldOutlineResult> Outlines);
