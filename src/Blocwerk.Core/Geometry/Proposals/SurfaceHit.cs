// <copyright file="SurfaceHit.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>Where a detection's viewing ray first meets the wall (a facet, or a volume on it).</summary>
/// <param name="Detection">The detection.</param>
/// <param name="FacetId">The facet hit.</param>
/// <param name="A">Hit along u, mm.</param>
/// <param name="B">Hit along v, mm.</param>
/// <param name="H">Hit height above the facet plane (a volume), mm.</param>
/// <param name="World">Hit, world mm.</param>
/// <param name="Origin">The camera centre, world mm.</param>
/// <param name="Direction">The ray's unit direction.</param>
/// <param name="SizeMm">The box's longer side at the hit's distance, mm.</param>
/// <param name="CosView">Cosine between the ray and the facet normal (1 = straight on).</param>
public sealed record SurfaceHit(
    CaptureDetection Detection,
    string FacetId,
    double A,
    double B,
    double H,
    double[] World,
    double[] Origin,
    double[] Direction,
    double SizeMm,
    double CosView);
