// <copyright file="FootprintView.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// One photo's silhouette of a hold, already projected onto the hold's facet plane (absolute a/b mm),
/// with the world position of the camera that took it.
/// </summary>
/// <param name="Silhouette">The projected silhouette ring (≥ 3 vertices).</param>
/// <param name="CameraMm">The camera centre in world millimetres.</param>
/// <param name="Label">Which photo it came from, for diagnostics.</param>
public sealed record FootprintView(IReadOnlyList<(double A, double B)> Silhouette, double[] CameraMm, string Label);
