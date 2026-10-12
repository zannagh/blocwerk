// <copyright file="LookOverOutline.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// A hold as it lies on one look-over picture, in the picture's own 0..1 square: the page draws it over the picture
/// with the same renderer and stroke as the wall's hold overlay, instead of a ring burnt into the image.
/// </summary>
/// <param name="X">Centre x.</param>
/// <param name="Y">Centre y.</param>
/// <param name="Radius">Radius (of a hold without an outline).</param>
/// <param name="Shape">The outline as offsets from the centre, or null for a plain circle.</param>
/// <param name="Holes">The holes of a pocket hold.</param>
/// <param name="Color">The hold's colour (a palette key or hex), when it has one.</param>
/// <param name="Kept">The hold is kept, not confirmed: drawn faded and dashed, as on the wall.</param>
/// <param name="Foot">A foothold (drawn as a rounded square without an outline).</param>
public sealed record LookOverOutline(
    double X,
    double Y,
    double Radius,
    IReadOnlyList<ShapePoint>? Shape,
    IReadOnlyList<IReadOnlyList<ShapePoint>>? Holes,
    string? Color,
    bool Kept,
    bool Foot);
