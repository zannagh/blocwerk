// <copyright file="VolumeSurfaceDocument.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>The stored JSON of a <see cref="VolumeSurface"/> (also what the 3D view receives).</summary>
/// <param name="Version">Format version: 1 (a height field) or 2 (also flat faces).</param>
/// <param name="ALo">Grid's lower a edge, mm.</param>
/// <param name="BLo">Grid's lower b edge, mm.</param>
/// <param name="CellMm">Cell side, mm.</param>
/// <param name="Cols">Cells along a.</param>
/// <param name="Rows">Cells along b.</param>
/// <param name="Heights">Base64 of little-endian int16 heights, mm, row-major along a.</param>
/// <param name="Faces">Version 2: the flat faces, each a list of [a, b, height] corners (mm); null otherwise.</param>
/// <param name="Shape">Version 2: "pyramid", "roof", "plateau" or "multi-peak"; null when derived from the faces.</param>
public sealed record VolumeSurfaceDocument(
    int Version,
    double ALo,
    double BLo,
    double CellMm,
    int Cols,
    int Rows,
    string Heights,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double[][][]? Faces = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Shape = null);
