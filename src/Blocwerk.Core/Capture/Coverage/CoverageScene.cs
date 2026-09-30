// <copyright file="CoverageScene.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// The wall as solid geometry for the coverage report's occlusion test (approximate, as the footprint refinement's
/// <see cref="VolumeFootprints.Occluded"/>): every facet is an opaque board where it really is (its region cut by its
/// seams, <see cref="CoverageOccluder"/>), every volume a solid over its facet. A line of sight from a camera is blocked
/// when it crosses another facet's board or meets a volume first; being behind another facet's plane alone hides nothing.
/// </summary>
public sealed class CoverageScene
{
    /// <summary>Heights below this are the wall around a volume, not the volume, mm.</summary>
    private const double VolumeMinHeightMm = 15;

    private readonly Dictionary<string, List<CoverageVolume>> volumesByFacet;
    private readonly IReadOnlyList<CoverageOccluder> occluders;
    private readonly SceneVolumeGroup[] groups;

    /// <summary>Initializes a new instance of the <see cref="CoverageScene"/> class.</summary>
    /// <param name="facets">The facets.</param>
    /// <param name="volumes">The visible volumes.</param>
    public CoverageScene(IReadOnlyList<CoverageFacet> facets, IReadOnlyList<CoverageVolume> volumes)
    {
        Facets = facets;
        Volumes = volumes.Where(v => facets.Any(f => f.Id == v.FacetId)).ToList();
        volumesByFacet = Volumes.GroupBy(v => v.FacetId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        groups = volumesByFacet.Select(kv => new SceneVolumeGroup(Facet(kv.Key)!.Frame, kv.Value)).ToArray();
        occluders = CoverageOccluderSeams.Build(facets);
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

    /// <summary>Whether another facet's board stands between the camera and the target.</summary>
    /// <param name="camera">Camera centre, world mm.</param>
    /// <param name="target">The target point, world mm.</param>
    /// <param name="facetId">The target's facet (its own board never blocks it).</param>
    /// <returns>True when blocked.</returns>
    public bool BlockedByFacet(double[] camera, double[] target, string facetId)
    {
        foreach (var o in occluders)
        {
            if (o.Facet.Id != facetId && o.Blocks(camera, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a volume stands between the camera and the target.</summary>
    /// <param name="camera">Camera centre, world mm.</param>
    /// <param name="target">The target point, world mm.</param>
    /// <param name="ownConvex">The convex volume the target lies on, if any: seen from the front of its surface it cannot block itself.</param>
    /// <returns>True when blocked.</returns>
    public bool BlockedByVolume(double[] camera, double[] target, VolumeSurface? ownConvex = null)
    {
        foreach (var group in groups)
        {
            if (VolumeOccludes(group, camera, target, ownConvex))
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
    /// <param name="ownConvex">The convex volume the target lies on, if any.</param>
    /// <returns>True when blocked.</returns>
    public bool Occluded(double[] camera, double[] target, string facetId, VolumeSurface? ownConvex = null) =>
        BlockedByFacet(camera, target, facetId) || BlockedByVolume(camera, target, ownConvex);

    private static bool VolumeOccludes(SceneVolumeGroup group, double[] camera, double[] target, VolumeSurface? ownConvex)
    {
        var from = FacetCloud.Local(group.Frame, camera[0], camera[1], camera[2]);
        var to = FacetCloud.Local(group.Frame, target[0], target[1], target[2]);
        if (from.H <= to.H || from.H <= 0)
        {
            return false;
        }

        foreach (var v in group.Volumes)
        {
            if (!ReferenceEquals(v.Surface, ownConvex) && v.MayCross(from, to) && VolumeFootprints.Occluded(camera, target, v.Single))
            {
                return true;
            }
        }

        return false;
    }
}
