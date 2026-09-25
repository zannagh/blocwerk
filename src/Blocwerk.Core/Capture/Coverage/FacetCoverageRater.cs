// <copyright file="FacetCoverageRater.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>Rasterises each facet into cells and rates every cell's centre (<see cref="PointViews"/>).</summary>
public static class FacetCoverageRater
{
    /// <summary>The default cell side, mm.</summary>
    public const double DefaultCellMm = 200;

    /// <summary>A facet gets coarser cells rather than more than this many.</summary>
    public const int MaxCells = 2500;

    /// <summary>Rates every facet of the scene.</summary>
    /// <param name="scene">The wall.</param>
    /// <param name="cameras">The posed cameras.</param>
    /// <param name="cellMm">The cell side, mm.</param>
    /// <returns>One rated grid per facet.</returns>
    public static IReadOnlyList<RatedFacet> Rate(CoverageScene scene, IReadOnlyList<CoverageCamera> cameras, double cellMm = DefaultCellMm) =>
        scene.Facets.Select(f => Rate(f, scene, cameras, cellMm)).ToList();

    private static RatedFacet Rate(CoverageFacet facet, CoverageScene scene, IReadOnlyList<CoverageCamera> cameras, double cellMm)
    {
        var r = facet.Region;
        var cell = cellMm;
        while (Cells(r.Width, cell) * Cells(r.Height, cell) > MaxCells)
        {
            cell *= 1.25;
        }

        var grid = new CellGrid(r.AMin, r.BMin, cell, Cells(r.Width, cell), Cells(r.Height, cell));
        var views = new PointViews[grid.Cols * grid.Rows];
        var status = new CoverageCellStatus[views.Length];
        for (var k = 0; k < views.Length; k++)
        {
            var (a, b) = grid.Centre(k);
            var point = facet.Frame.ToWorld(a, b);
            if (scene.UnderVolume(facet.Id, a, b) || scene.InsideWall(facet.Id, point))
            {
                status[k] = CoverageCellStatus.Hidden;
                continue;
            }

            views[k] = PointViews.Evaluate(point, facet.Frame.Normal, facet.Id, cameras, scene);
            status[k] = views[k].Status;
        }

        return new RatedFacet(facet, grid, views, status);
    }

    private static int Cells(double length, double cell) => Math.Max(1, (int)Math.Ceiling(length / cell));
}
