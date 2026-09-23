// <copyright file="Wall3DPhotoKey.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// The photo a hold's X/Y/ShapePoints are normalised to: its panel row (null for the legacy
/// single-image wall photo) and the generation the photo belongs to.
/// </summary>
/// <param name="PanelId">The hold's <c>WallPanelId</c>.</param>
/// <param name="Generation">The hold's generation (marker observations carry the matching <c>PanelGeneration</c>).</param>
public readonly record struct Wall3DPhotoKey(Guid? PanelId, int Generation);

/// <summary>
/// One photo's stored marker observations, rebuilt into a square pixel grid of side <paramref name="Scale"/>:
/// a normalised photo point (x, y) sits at pixel (x·Scale, y·Scale). The true aspect ratio is not needed,
/// because a homography fitted in that grid absorbs the axis scaling exactly.
/// </summary>
/// <param name="Markers">The markers, corners in the scaled grid.</param>
/// <param name="Scale">Pixels per normalised unit in the grid.</param>
public sealed record Wall3DPhotoMarkers(IReadOnlyList<DetectedMarker> Markers, double Scale);
