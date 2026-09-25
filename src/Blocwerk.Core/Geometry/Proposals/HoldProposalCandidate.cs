// <copyright file="HoldProposalCandidate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>A hold seen in several capture photos that matches no existing hold (<see cref="HoldProposalFinder"/>).</summary>
/// <param name="FacetId">The facet it is on.</param>
/// <param name="A">Its point along u, mm.</param>
/// <param name="B">Its point along v, mm.</param>
/// <param name="H">Its point's height above the facet plane (on a volume, or its own relief), mm.</param>
/// <param name="World">Its point, world mm.</param>
/// <param name="SizeMm">Median detected size, mm.</param>
/// <param name="Views">Photos that see it.</param>
/// <param name="Confidence">Mean detector confidence.</param>
/// <param name="ResidualMm">How well its rays agree, mm.</param>
/// <param name="Best">The clearest detection (most frontal and confident), for the review crop.</param>
/// <param name="Detections">All its detections, one per photo.</param>
public sealed record HoldProposalCandidate(
    string FacetId,
    double A,
    double B,
    double H,
    double[] World,
    double SizeMm,
    int Views,
    double Confidence,
    double ResidualMm,
    CaptureDetection Best,
    IReadOnlyList<CaptureDetection> Detections);
