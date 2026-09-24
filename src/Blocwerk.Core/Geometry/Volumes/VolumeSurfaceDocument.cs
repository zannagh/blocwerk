// <copyright file="VolumeSurfaceDocument.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>The stored JSON of a <see cref="VolumeSurface"/> (also what the 3D view receives).</summary>
/// <param name="Version">Format version (1).</param>
/// <param name="ALo">Grid's lower a edge, mm.</param>
/// <param name="BLo">Grid's lower b edge, mm.</param>
/// <param name="CellMm">Cell side, mm.</param>
/// <param name="Cols">Cells along a.</param>
/// <param name="Rows">Cells along b.</param>
/// <param name="Heights">Base64 of little-endian int16 heights, mm, row-major along a.</param>
public sealed record VolumeSurfaceDocument(int Version, double ALo, double BLo, double CellMm, int Cols, int Rows, string Heights);
