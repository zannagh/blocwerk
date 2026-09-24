// <copyright file="DetectedVolume.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>A candidate found by <see cref="VolumeDetector"/>, accepted or not (<see cref="Status"/>).</summary>
/// <param name="FacetId">The facet it stands on.</param>
/// <param name="Footprint">Convex outline on the facet, (a, b) mm.</param>
/// <param name="AreaM2">Area of the raised cells, m².</param>
/// <param name="HeightMm">90th-percentile height of its points above the wall, mm.</param>
/// <param name="Points">Evidence points in it.</param>
/// <param name="HoldCover">Share of the footprint covered by known hold outlines.</param>
/// <param name="SingleHoldCover">Largest share covered by one hold.</param>
/// <param name="WallSupport">Share of bare wall in the ring around it.</param>
/// <param name="Status">"accepted" or "rejected:&lt;reason&gt;".</param>
/// <param name="Surface">The shape (accepted candidates only).</param>
public sealed record DetectedVolume(
    string FacetId,
    IReadOnlyList<(double A, double B)> Footprint,
    double AreaM2,
    double HeightMm,
    int Points,
    double HoldCover,
    double SingleHoldCover,
    double WallSupport,
    string Status,
    VolumeSurface? Surface)
{
    /// <summary>The status of an accepted candidate.</summary>
    public const string Accepted = "accepted";

    /// <summary>Whether it was accepted.</summary>
    public bool IsAccepted => Status == Accepted;

    /// <summary>A 0–1 plausibility: tall enough, well sampled, sitting on bare wall.</summary>
    public double Confidence =>
        Math.Round(Math.Clamp(HeightMm / 120, 0, 1) * Math.Clamp(Points / 300.0, 0, 1) * Math.Clamp(WallSupport, 0, 1) * (1 - (0.5 * HoldCover)), 3);
}
