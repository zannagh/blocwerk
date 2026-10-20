// <copyright file="CloseUpModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Geometry.HybridZoom;

/// <summary>One capture photo a close-up may come from.</summary>
/// <param name="Name">The solved camera's image name (<c>p01</c>…).</param>
/// <param name="Camera">Its camera, already scaled to the stored photo's pixel size.</param>
/// <param name="Sharpness">Its sharpness score (variance of the Laplacian), when known.</param>
public sealed record CloseUpPhoto(string Name, SolvedCamera Camera, double? Sharpness);

/// <summary>What a close-up must beat to be shown, and how it is judged.</summary>
/// <param name="MinGain">Least resolution gain over the panel photo (close-up px per mm ÷ panel px per mm).</param>
/// <param name="MinFrontality">Least cosine between the facet normal and the direction to the camera.</param>
/// <param name="EdgeMargin">Share of the photo's size the crop must keep clear of its edges (lens distortion is worst there).</param>
/// <param name="MaxResidualShare">Largest warp residual (the lens model against one homography), as a share of the crop's long side in photo px.</param>
public sealed record CloseUpRules(double MinGain = 1.3, double MinFrontality = 0.5, double EdgeMargin = 0.02, double MaxResidualShare = 0.01);

/// <summary>A capture photo that shows the requested view better than the panel photo does.</summary>
/// <param name="Name">The photo's camera name.</param>
/// <param name="ViewToPhoto">Rectangle (u, v in 0..1) → photo px.</param>
/// <param name="PhotoBounds">The crop's bounds in photo px (x0, y0, x1, y1).</param>
/// <param name="PxPerMm">Photo pixels per millimetre of wall over the crop.</param>
/// <param name="Gain">Resolution gain over the panel photo.</param>
/// <param name="Frontality">Cosine between the facet normal and the direction to the camera.</param>
/// <param name="ResidualPx">Largest deviation of the lens model from <see cref="ViewToPhoto"/> over the samples, px.</param>
/// <param name="Score">What the photos were ranked by (higher wins).</param>
public sealed record CloseUpChoice(
    string Name,
    PlaneHomography ViewToPhoto,
    (double X0, double Y0, double X1, double Y1) PhotoBounds,
    double PxPerMm,
    double Gain,
    double Frontality,
    double ResidualPx,
    double Score);
