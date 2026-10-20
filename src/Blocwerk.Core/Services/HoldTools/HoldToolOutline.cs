// <copyright file="HoldToolOutline.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Services;

/// <summary>An outline for a preview on the photo: absolute normalized rings.</summary>
/// <param name="Outer">The outer ring.</param>
/// <param name="Holes">The interior rings.</param>
public sealed record HoldToolOutline(IReadOnlyList<NormalizedPoint> Outer, IReadOnlyList<IReadOnlyList<NormalizedPoint>> Holes);
