// <copyright file="HoldOutlineMerge.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>The outcome of <see cref="HoldOutlineBoolean.Merge"/>.</summary>
/// <param name="Shape">The merged outline.</param>
/// <param name="Gap">The gap that was bridged (normalized units of the photo height), 0 when the holds touched.</param>
/// <param name="Bridged">True when a bridge was added.</param>
internal sealed record HoldOutlineMerge(OutlineRings Shape, double Gap, bool Bridged);
