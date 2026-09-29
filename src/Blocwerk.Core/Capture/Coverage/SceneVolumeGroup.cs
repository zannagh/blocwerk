// <copyright file="SceneVolumeGroup.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>The volumes standing on one facet, ready for the scene's occlusion test.</summary>
internal sealed class SceneVolumeGroup
{
    /// <summary>Initializes a new instance of the <see cref="SceneVolumeGroup"/> class.</summary>
    /// <param name="frame">The facet's frame.</param>
    /// <param name="volumes">The volumes on it.</param>
    public SceneVolumeGroup(FacetFrame frame, IEnumerable<CoverageVolume> volumes)
    {
        Frame = frame;
        Volumes = volumes.Select(v => new SceneVolume(v, frame)).ToArray();
    }

    /// <summary>The facet's frame.</summary>
    public FacetFrame Frame { get; }

    /// <summary>The volumes on the facet.</summary>
    public IReadOnlyList<SceneVolume> Volumes { get; }
}

/// <summary>One volume with its bounds in the facet frame: the grid's extent and the height a line of sight must be below to meet it.</summary>
internal sealed class SceneVolume
{
    private readonly double aLo;
    private readonly double aHi;
    private readonly double bLo;
    private readonly double bHi;
    private readonly double top;

    /// <summary>Initializes a new instance of the <see cref="SceneVolume"/> class.</summary>
    /// <param name="volume">The volume.</param>
    /// <param name="frame">Its facet's frame.</param>
    public SceneVolume(CoverageVolume volume, FacetFrame frame)
    {
        var g = volume.Surface.Grid;
        (aLo, aHi, bLo, bHi) = (g.ALo, g.ALo + (g.Cols * g.CellMm), g.BLo, g.BLo + (g.Rows * g.CellMm));
        top = volume.Surface.MaxHeightMm + 1;
        Surface = volume.Surface;
        Single = new FacetVolumes(frame, [volume.Surface]);
    }

    /// <summary>The volume's shape.</summary>
    public VolumeSurface Surface { get; }

    /// <summary>The volume as the occlusion test takes it.</summary>
    public FacetVolumes Single { get; }

    /// <summary>A cheap bounding-box test: whether the line of sight (from camera to target, facet coordinates) can pass over the volume's grid below its top.</summary>
    /// <param name="from">The camera.</param>
    /// <param name="to">The target.</param>
    /// <returns>False when the volume cannot be in the way.</returns>
    public bool MayCross((double A, double B, double H) from, (double A, double B, double H) to)
    {
        // Where the line of sight (continued to the facet plane) is at the volume's top, and where it meets the plane.
        var tTop = Math.Clamp((from.H - top) / (from.H - to.H), 0, 1);
        var tEnd = from.H / (from.H - to.H);
        double a0 = from.A + (tTop * (to.A - from.A)), b0 = from.B + (tTop * (to.B - from.B));
        double a1 = from.A + (tEnd * (to.A - from.A)), b1 = from.B + (tEnd * (to.B - from.B));
        return Math.Max(a0, a1) >= aLo && Math.Min(a0, a1) <= aHi && Math.Max(b0, b1) >= bLo && Math.Min(b0, b1) <= bHi;
    }
}
