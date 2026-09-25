// <copyright file="CoverageScene.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// The wall as solid geometry for the coverage report's occlusion test (approximate, as the footprint refinement's
/// <see cref="VolumeFootprints.Occluded"/>): every facet is an opaque board over its region, every volume a solid
/// over its facet. A line of sight is blocked when it crosses another facet's board or meets a volume first.
/// </summary>
public sealed class CoverageScene
{
    /// <summary>A board edge this close to the crossing does not block (neighbouring facets share their edges), mm.</summary>
    private const double EdgeMarginMm = 60;

    /// <summary>A crossing this close to the target is the target's own surroundings, mm.</summary>
    private const double NearTargetMm = 80;

    /// <summary>Heights below this are the wall around a volume, not the volume, mm.</summary>
    private const double VolumeMinHeightMm = 15;

    /// <summary>A point this far behind another facet's board is inside the wall, mm.</summary>
    private const double BehindMm = 30;

    private readonly Dictionary<string, List<CoverageVolume>> volumesByFacet;

    /// <summary>Initializes a new instance of the <see cref="CoverageScene"/> class.</summary>
    /// <param name="facets">The facets.</param>
    /// <param name="volumes">The visible volumes.</param>
    public CoverageScene(IReadOnlyList<CoverageFacet> facets, IReadOnlyList<CoverageVolume> volumes)
    {
        Facets = facets;
        Volumes = volumes.Where(v => facets.Any(f => f.Id == v.FacetId)).ToList();
        volumesByFacet = Volumes.GroupBy(v => v.FacetId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    /// <summary>The facets.</summary>
    public IReadOnlyList<CoverageFacet> Facets { get; }

    /// <summary>The volumes on those facets.</summary>
    public IReadOnlyList<CoverageVolume> Volumes { get; }

    /// <summary>The facet with the id, or null.</summary>
    /// <param name="id">Facet id.</param>
    /// <returns>The facet.</returns>
    public CoverageFacet? Facet(string id) => Facets.FirstOrDefault(f => f.Id == id);

    /// <summary>Whether a volume stands on the facet at (a, b): that part of the facet is not surface.</summary>
    /// <param name="facetId">The facet.</param>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <returns>True under a volume.</returns>
    public bool UnderVolume(string facetId, double a, double b) =>
        volumesByFacet.TryGetValue(facetId, out var list) && list.Any(v => v.Surface.HeightAt(a, b) >= VolumeMinHeightMm);

    /// <summary>
    /// Whether a point of a facet's region lies behind another facet's board (inside the wall): the region is a
    /// rectangle, so a triangular side panel's region reaches behind the wall it meets. Such a point is no surface.
    /// </summary>
    /// <param name="facetId">The point's facet.</param>
    /// <param name="point">The point, world mm.</param>
    /// <returns>True inside the wall.</returns>
    public bool InsideWall(string facetId, double[] point)
    {
        foreach (var f in Facets.Where(f => f.Id != facetId))
        {
            var (a, b, h) = FacetCloud.Local(f.Frame, point[0], point[1], point[2]);
            var r = f.Region;
            if (h < -BehindMm && a > r.AMin + EdgeMarginMm && a < r.AMax - EdgeMarginMm && b > r.BMin + EdgeMarginMm && b < r.BMax - EdgeMarginMm)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether another facet's board or a volume stands between the camera and the target.</summary>
    /// <param name="camera">Camera centre, world mm.</param>
    /// <param name="target">The target point, world mm.</param>
    /// <param name="facetId">The target's facet (its own board never blocks it).</param>
    /// <returns>True when blocked.</returns>
    public bool Occluded(double[] camera, double[] target, string facetId)
    {
        foreach (var f in Facets)
        {
            if (f.Id != facetId && CrossesBoard(f, camera, target))
            {
                return true;
            }
        }

        foreach (var (id, list) in volumesByFacet)
        {
            var frame = Facet(id)!.Frame;
            var hits = list.Where(v => MayCross(frame, v, camera, target)).Select(v => v.Surface).ToList();
            if (hits.Count > 0 && VolumeFootprints.Occluded(camera, target, new FacetVolumes(frame, hits)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CrossesBoard(CoverageFacet f, double[] camera, double[] target)
    {
        var from = FacetCloud.Local(f.Frame, camera[0], camera[1], camera[2]);
        var to = FacetCloud.Local(f.Frame, target[0], target[1], target[2]);
        if (Math.Sign(from.H) == Math.Sign(to.H) || from.H == to.H)
        {
            return false;
        }

        var t = from.H / (from.H - to.H);
        var length = Math.Sqrt(Sq(to.A - from.A) + Sq(to.B - from.B) + Sq(to.H - from.H));
        if ((1 - t) * length < NearTargetMm)
        {
            return false;
        }

        double a = from.A + (t * (to.A - from.A)), b = from.B + (t * (to.B - from.B));
        var r = f.Region;
        return a > r.AMin + EdgeMarginMm && a < r.AMax - EdgeMarginMm && b > r.BMin + EdgeMarginMm && b < r.BMax - EdgeMarginMm;
    }

    /// <summary>A cheap bounding-box test: whether the line of sight can pass over the volume's grid below its top.</summary>
    private static bool MayCross(FacetFrame frame, CoverageVolume v, double[] camera, double[] target)
    {
        var from = FacetCloud.Local(frame, camera[0], camera[1], camera[2]);
        var to = FacetCloud.Local(frame, target[0], target[1], target[2]);
        var top = v.Surface.MaxHeightMm + 1;
        if (from.H <= to.H || from.H <= 0)
        {
            return false;
        }

        // Where the line of sight (continued to the facet plane) is at the volume's top, and where it meets the plane.
        var tTop = Math.Clamp((from.H - top) / (from.H - to.H), 0, 1);
        var tEnd = from.H / (from.H - to.H);
        double a0 = from.A + (tTop * (to.A - from.A)), b0 = from.B + (tTop * (to.B - from.B));
        double a1 = from.A + (tEnd * (to.A - from.A)), b1 = from.B + (tEnd * (to.B - from.B));
        var g = v.Surface.Grid;
        double gA1 = g.ALo + (g.Cols * g.CellMm), gB1 = g.BLo + (g.Rows * g.CellMm);
        return Math.Max(a0, a1) >= g.ALo && Math.Min(a0, a1) <= gA1 && Math.Max(b0, b1) >= g.BLo && Math.Min(b0, b1) <= gB1;
    }

    private static double Sq(double x) => x * x;
}
