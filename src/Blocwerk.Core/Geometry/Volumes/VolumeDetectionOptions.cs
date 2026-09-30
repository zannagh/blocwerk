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

    /// <summary>
    /// Unless the edge is a seam (<see cref="FacetSeams"/>): a neighbouring facet whose normal is within this angle
    /// continues the wall there (on The Attic, 2026-09-29, the main wall's facet ends mid-wall and cut a roof off), degrees.
    /// </summary>
    public double SeamMaxAngleDeg { get; init; } = 10;

    /// <summary>Just beyond the seam the facet's plane is within this of the neighbour's plane, mm.</summary>
    public double SeamMaxOffsetMm { get; init; } = 50;

    /// <summary>How far beyond the edge the neighbour's extent must reach (a gap between the extents up to this), mm.</summary>
    public double SeamReachMm { get; init; } = 150;

    /// <summary>
    /// A neighbour's extent starting within this of an edge (and running along it) abuts it: the two were clipped apart at
    /// their midline and the whole edge is a seam (on The Attic, 2026-09-30, the main wall's clipped edge cut off a roof
    /// beside the lower neighbour's end), mm.
    /// </summary>
    public double SeamAbutMm { get; init; } = 30;

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

    /// <summary>
    /// A candidate whose outline covers more than this (m²) is large: on The Attic (2026-09-27) real volumes span at
    /// most 0.25 m², a shallow sheet of holds and bumps (a false volume) 0.86 m².
    /// </summary>
    public double SheetMinAreaM2 { get; init; } = 0.5;

    /// <summary>Or more than this share of its facet.</summary>
    public double SheetMinFacetShare { get; init; } = 0.15;

    /// <summary>A large candidate whose median height is below this is a shallow sheet, not a volume, mm.</summary>
    public double SheetMaxMedianMm { get; init; } = 70;

    /// <summary>And whose 90th-percentile height is below this (a real volume's top stands higher), mm.</summary>
    public double SheetMaxHeightMm { get; init; } = 120;

    /// <summary>
    /// A low candidate whose raised cells fill less than this share of its convex outline is a scatter of bumps, not a
    /// volume (a volume is convex: pyramids and roofs fill their outline). On The Attic (2026-09-29) false ones filled
    /// 46–55 %.
    /// </summary>
    public double MinFillRatio { get; init; } = 0.6;

    /// <summary>The fill rule applies only below this median height, mm (a tall candidate is kept).</summary>
    public double SparseMaxMedianMm { get; init; } = 80;

    /// <summary>
    /// A candidate lower than this whose flat-sided reading is several separate peaks is a cluster of holds and bumps, mm
    /// (on The Attic, 2026-09-30: 60 mm; the real volumes stand 79–164 mm).
    /// </summary>
    public double MultiPeakMaxHeightMm { get; init; } = 70;

    /// <summary>
    /// A small candidate without any hold on it is a step in the wall, not a volume (only judged when the facet has
    /// holds located on it at all).
    /// </summary>
    public double BareMaxAreaM2 { get; init; } = 0.05;

    /// <summary>Percentile of the points per cell taken as the volume's surface (splat centres scatter into the body).</summary>
    public double SurfaceQuantile { get; init; } = 0.9;

    /// <summary>Points within this of another facet's plane (inside its extent) belong to that facet, mm.</summary>
    public double OtherSurfaceMm { get; init; } = 30;

    /// <summary>A hold whose photo ray meets a volume lower than this stays on the facet, mm.</summary>
    public double MinPlacementHeightMm { get; init; } = 15;
}
