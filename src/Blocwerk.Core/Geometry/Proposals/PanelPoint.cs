// <copyright file="PanelPoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>Where a 3D point shows on a panel photo: normalised centre and radius (of the photo's longer side).</summary>
/// <param name="PanelId">The panel.</param>
/// <param name="X">Normalised x.</param>
/// <param name="Y">Normalised y.</param>
/// <param name="Radius">Normalised radius.</param>
public readonly record struct PanelPoint(Guid PanelId, double X, double Y, double Radius);
