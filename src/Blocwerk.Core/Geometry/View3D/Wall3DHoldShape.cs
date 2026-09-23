// <copyright file="Wall3DHoldShape.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// A hold's real outline on its facet, in facet-plane millimetres RELATIVE to the hold's centre
/// (<see cref="Wall3DHold.PlaneA"/>, <see cref="Wall3DHold.PlaneB"/>): each vertex is <c>[da, db]</c>,
/// da along the facet's u (right), db along v (up). The renderer extrudes it flat onto the facet.
/// </summary>
/// <param name="Source">How the outline was mapped from the photo onto the facet.</param>
/// <param name="Outline">The outer ring (≥ 3 vertices, at most <see cref="HoldShapeProjector.MaxOutlineVertices"/>).</param>
/// <param name="Holes">Pocket / donut holes cut out of the outline (each ≥ 3 vertices).</param>
public sealed record Wall3DHoldShape(
    Wall3DShapeSource Source,
    IReadOnlyList<double[]> Outline,
    IReadOnlyList<IReadOnlyList<double[]>> Holes);

/// <summary>Where a <see cref="Wall3DHoldShape"/> came from. Serialised by name for the renderer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Wall3DShapeSource>))]
public enum Wall3DShapeSource
{
    /// <summary>The traced outline, mapped per vertex through the photo's marker homography for the facet.</summary>
    Markers,

    /// <summary>
    /// The traced outline, mapped per vertex through a homography fitted to the photo's placed hold
    /// centres on that facet (no stored marker observations for the photo).
    /// </summary>
    HoldFit,

    /// <summary>
    /// The traced outline scaled to the hold's measured mm size with no perspective mapping: the
    /// photo has neither marker observations nor enough placed holds on the facet to fit one.
    /// </summary>
    Approximate,

    /// <summary>The hold has no traced outline: an ellipse of its (measured or default) mm size.</summary>
    Circle,

    /// <summary>The stored contact footprint: silhouettes from several capture views intersected on the facet.</summary>
    Footprint,

    /// <summary>The stored footprint from one view, shortened along the view direction by an estimated protrusion.</summary>
    FootprintApproximate,
}
