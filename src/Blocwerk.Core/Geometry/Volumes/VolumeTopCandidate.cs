// <copyright file="VolumeTopCandidate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>One reading of a volume's high points (<see cref="VolumeTop"/>).</summary>
/// <param name="Top">The top vertices (a, b, height).</param>
/// <param name="Decided">Whether this is the reading the height field suggests (the others are tried too).</param>
/// <param name="Shape">"pyramid" (an apex), "roof" (a ridge), "plateau" (a flat top) or "multi-peak" (several high regions).</param>
/// <param name="Plane">A plateau's plane h = α·a + β·b + γ: its vertices keep to it when moved.</param>
public sealed record VolumeTopCandidate(
    List<(double A, double B, double H)> Top,
    bool Decided,
    string Shape,
    (double Alpha, double Beta, double Gamma)? Plane = null);
