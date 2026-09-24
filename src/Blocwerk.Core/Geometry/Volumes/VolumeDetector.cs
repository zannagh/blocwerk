// <copyright file="VolumeDetector.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// Finds volumes WITHOUT markers, from 3D evidence already in the wall frame (the photo-real scene's surface-like
/// splat centres): per facet, the height above the wall's own surface on a 20 mm grid, raised cells
/// (75th percentile &gt; 35 mm) grouped into connected regions, each region measured and judged
/// (<see cref="VolumeChecks"/>), and an accepted one shaped as a <see cref="VolumeSurface"/>. On The Attic
/// (2026-09-25) this finds the six big main-wall volumes (0.06–0.18 m², 80–145 mm) and one false step; about
/// 2 s for 600k points. Small wooden boxes (&lt; 0.035 m²) are missed on purpose (a big hold looks the same).
/// </summary>
public static class VolumeDetector
{
    private const int MinComponentPoints = 15;
    private const double RaisedQuantile = 0.75;
    private const double HeightQuantile = 0.9;

    /// <summary>Every candidate on every facet (accepted and rejected).</summary>
    /// <param name="points">World points, mm.</param>
    /// <param name="frames">The facets by id.</param>
    /// <param name="extents">Their extents.</param>
    /// <param name="holds">Known holds per facet (for the hold tests).</param>
    /// <param name="options">Tuning; null for the defaults.</param>
    /// <returns>The candidates.</returns>
    public static List<DetectedVolume> Detect(
        IReadOnlyList<(float X, float Y, float Z)> points,
        IReadOnlyDictionary<string, FacetFrame> frames,
        IReadOnlyDictionary<string, PlaneRectMm> extents,
        IReadOnlyDictionary<string, List<KnownHoldEllipse>> holds,
        VolumeDetectionOptions? options = null)
    {
        options ??= new VolumeDetectionOptions();
        var result = new List<DetectedVolume>();
        foreach (var (id, frame) in frames.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!extents.TryGetValue(id, out var extent))
            {
                continue;
            }

            var others = frames.Where(f => f.Key != id && extents.ContainsKey(f.Key)).Select(f => (f.Value, extents[f.Key])).ToList();
            var cloud = FacetCloud.Build(points, id, frame, extent, others, options.OtherSurfaceMm);
            result.AddRange(DetectOnFacet(cloud, holds.GetValueOrDefault(id) ?? [], options));
        }

        return result;
    }

    /// <summary>The candidates on one facet.</summary>
    /// <param name="cloud">The facet's cloud.</param>
    /// <param name="holds">Its known holds.</param>
    /// <param name="options">Tuning.</param>
    /// <returns>The candidates.</returns>
    public static List<DetectedVolume> DetectOnFacet(FacetCloud cloud, IReadOnlyList<KnownHoldEllipse> holds, VolumeDetectionOptions options)
    {
        var result = new List<DetectedVolume>();
        if (cloud.Count < 50)
        {
            return result;
        }

        var e = cloud.Extent;
        var grid = CellGrid.Covering(e.AMin, e.AMax, e.BMin, e.BMax, options.CellMm);
        var q75 = grid.Quantile(cloud.A, cloud.B, cloud.H, RaisedQuantile, options.MinCellPoints);
        var raised = grid.CloseOpen(q75.Select(h => !double.IsNaN(h) && h > options.RaisedMm).ToArray());
        var (labels, count) = grid.Label(raised);
        var members = Members(cloud, grid, labels, count);
        for (var k = 1; k <= count; k++)
        {
            if (Candidate(cloud, grid, labels, k, members[k], holds, options) is { } candidate)
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    private static List<int>[] Members(FacetCloud cloud, CellGrid grid, int[] labels, int count)
    {
        var members = new List<int>[count + 1];
        for (var k = 0; k <= count; k++)
        {
            members[k] = [];
        }

        for (var p = 0; p < cloud.Count; p++)
        {
            var idx = grid.IndexOf(cloud.A[p], cloud.B[p]);
            if (idx >= 0 && labels[idx] > 0)
            {
                members[labels[idx]].Add(p);
            }
        }

        return members;
    }

    private static DetectedVolume? Candidate(
        FacetCloud cloud, CellGrid grid, int[] labels, int label, List<int> members, IReadOnlyList<KnownHoldEllipse> holds, VolumeDetectionOptions options)
    {
        var cells = Enumerable.Range(0, labels.Length).Where(i => labels[i] == label).ToList();
        var area = cells.Count * options.CellMm * options.CellMm / 1e6;
        if (area < options.MinAreaM2 * 0.5 || members.Count < MinComponentPoints)
        {
            return null;
        }

        var heights = members.Select(p => cloud.H[p]).Order().ToList();
        var height = heights[(int)(HeightQuantile * (heights.Count - 1))];
        if (height < options.MinHeightMm)
        {
            return null;
        }

        var corners = cells.SelectMany(c => CellCorners(grid, c));
        var footprint = PlanePolygon.ConvexHull(corners);
        var (any, single) = VolumeChecks.HoldCover(footprint, holds);
        var candidate = new DetectedVolume(
            cloud.FacetId, footprint, Math.Round(area, 4), Math.Round(height, 1), members.Count, Math.Round(any, 3), Math.Round(single, 3),
            Math.Round(VolumeChecks.WallSupport(cloud, footprint, options), 3), string.Empty, null);
        var status = VolumeChecks.Judge(candidate, cloud.Extent, options);
        var surface = status == DetectedVolume.Accepted ? VolumeSurfaceBuilder.Build(cloud, footprint, options) : null;
        return candidate with { Status = status, Surface = surface };
    }

    private static IEnumerable<(double A, double B)> CellCorners(CellGrid grid, int index)
    {
        var (a, b) = grid.Centre(index);
        var h = grid.CellMm / 2;
        yield return (a - h, b - h);
        yield return (a + h, b - h);
        yield return (a - h, b + h);
        yield return (a + h, b + h);
    }
}
