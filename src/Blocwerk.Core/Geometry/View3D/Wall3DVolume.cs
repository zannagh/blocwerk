// <copyright file="Wall3DVolume.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// A volume for the 3D view: its height field over the facet (<see cref="Volumes.VolumeSurface"/>; cell (i, j)'s
/// centre is at a = ALo + (i + ½)·CellMm, b = BLo + (j + ½)·CellMm, heights mm above the facet plane, row-major
/// along a) and its footprint. <see cref="TextureCamera"/> is the capture camera that painted most of the facet
/// texture under it, in facet coordinates (a, b, height): the texture shows a volume point where that camera's
/// ray through it meets the plane, so the renderer can texture the volume by projecting from there.
/// </summary>
/// <param name="Id">The volume.</param>
/// <param name="Index">Its number on the model.</param>
/// <param name="FacetId">Its facet.</param>
/// <param name="ALo">Grid's lower a edge, mm.</param>
/// <param name="BLo">Grid's lower b edge, mm.</param>
/// <param name="CellMm">Cell side, mm.</param>
/// <param name="Cols">Cells along a.</param>
/// <param name="Rows">Cells along b.</param>
/// <param name="Heights">Height per cell, mm.</param>
/// <param name="Footprint">Convex outline on the facet, [a, b] mm.</param>
/// <param name="TextureCamera">The texture's source camera for the volume, or null (then it is drawn plain).</param>
/// <param name="Faces">A flat-sided volume's planar faces, each a list of [a, b, height] corners (mm), counter-clockwise seen from outside; null for a height field.</param>
public sealed record Wall3DVolume(
    Guid Id,
    int Index,
    string FacetId,
    double ALo,
    double BLo,
    double CellMm,
    int Cols,
    int Rows,
    IReadOnlyList<short> Heights,
    IReadOnlyList<double[]> Footprint,
    double[]? TextureCamera,
    double[][][]? Faces = null);
