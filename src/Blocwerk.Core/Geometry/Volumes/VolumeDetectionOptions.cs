// <copyright file="VolumeDetectionOptions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// Tuning of <see cref="VolumeDetector"/>. The defaults were fitted on The Attic (2026-09-25): bare wall scatters
/// about ±12 mm around its plane in the splat, volumes stand 65–160 mm proud, big holds reach 60–90 mm.
/// </summary>
public sealed record VolumeDetectionOptions
{
    /// <summary>
    /// For a capture's COLMAP sparse points instead of splat centres (no photo-real view): far fewer points, so a
    /// coarser grid (35 mm: on the copy wall's run 3 it finds all six splat volumes, 40 mm merges and misses one), and
    /// the points lie ON the surface (no scatter into the body), so the surface is their median.
    /// </summary>
    public static VolumeDetectionOptions Sparse { get; } = new()
    {
        CellMm = 35,
        MinCellPoints = 3,
        SurfaceQuantile = 0.5,
    };

    /// <summary>Grid cell on the facet plane, mm.</summary>
    public double CellMm { get; init; } = 20;

    /// <summary>A cell is raised when its 75th-percentile height exceeds this, mm.</summary>
    public double RaisedMm { get; init; } = 35;

    /// <summary>Fewest points for a cell height.</summary>
    public int MinCellPoints { get; init; } = 3;

    /// <summary>Smallest volume, m² on the facet (the small wooden boxes are below; a big hold too).</summary>
    public double MinAreaM2 { get; init; } = 0.035;

    /// <summary>A volume's 90th-percentile height must be at least this, mm.</summary>
    public double MinHeightMm { get; init; } = 50;

    /// <summary>Anything taller is not a volume (a mat, a person, the structure behind the wall), mm.</summary>
    public double MaxHeightMm { get; init; } = 300;

    /// <summary>A candidate reaching into this band along the facet's extent is the wall's edge or what lies beyond it, mm.</summary>
    public double EdgeBandMm { get; init; } = 60;

    /// <summary>Least share of bare wall in the ring around a volume (else it is not sitting on this facet).</summary>
    public double MinWallSupport { get; init; } = 0.35;

    /// <summary>Width of that ring, mm.</summary>
    public double RingMm { get; init; } = 150;

    /// <summary>A ring point counts as bare wall within this height, mm.</summary>
    public double WallToleranceMm { get; init; } = 25;

    /// <summary>One known hold covering more of the candidate than this: it is that hold (a macro).</summary>
    public double MaxSingleHoldCover { get; init; } = 0.5;

    /// <summary>Holds covering more than this of a low candidate: a cluster of holds.</summary>
    public double MaxHoldCover { get; init; } = 0.55;

    /// <summary>The hold-cluster rule only applies below this height, mm (a real volume carries holds too).</summary>
    public double ClusterMaxHeightMm { get; init; } = 75;

    /// <summary>A small candidate without any hold on it is a step in the wall, not a volume.</summary>
    public double BareMaxAreaM2 { get; init; } = 0.05;

    /// <summary>Percentile of the points per cell taken as the volume's surface (splat centres scatter into the body).</summary>
    public double SurfaceQuantile { get; init; } = 0.9;

    /// <summary>Points within this of another facet's plane (inside its extent) belong to that facet, mm.</summary>
    public double OtherSurfaceMm { get; init; } = 30;

    /// <summary>A hold whose photo ray meets a volume lower than this stays on the facet, mm.</summary>
    public double MinPlacementHeightMm { get; init; } = 15;
}
