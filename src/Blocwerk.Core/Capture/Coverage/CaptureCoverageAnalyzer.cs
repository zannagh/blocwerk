// <copyright file="CaptureCoverageAnalyzer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// Computes a capture's coverage report (CPU only, seconds): rates every facet cell and volume face against the posed
/// cameras, the markers per facet, the video against the recipe, and writes the "what to add" list.
/// </summary>
public static class CaptureCoverageAnalyzer
{
    /// <summary>A facet without an extent gets its markers' bounds plus this margin, mm.</summary>
    private const double MarkerMarginMm = 100;

    /// <summary>Placed holds widen a facet's region by their bounds plus this margin, mm.</summary>
    private const double HoldMarginMm = 50;

    /// <summary>Computes the report.</summary>
    /// <param name="inputs">The capture's model, cameras, volumes and video.</param>
    /// <param name="now">The time stamp.</param>
    /// <returns>The report.</returns>
    public static CaptureCoverageReport Analyze(CoverageInputs inputs, DateTimeOffset now)
    {
        var doc = inputs.Document;
        var cameras = inputs.Photos.Concat(inputs.VideoFrames).ToList();
        var scene = new CoverageScene(Facets(doc, inputs.HoldBounds), inputs.Volumes);
        var rated = FacetCoverageRater.Rate(scene, cameras);
        var markers = rated.ToDictionary(r => r.Facet.Id, r => MarkerCoverageRater.Rate(doc, r, cameras), StringComparer.Ordinal);
        var volumes = VolumeCoverageRater.Rate(scene, cameras);
        var (recipeCameras, source) = inputs.VideoFrames.Count > 0 ? (inputs.VideoFrames, CoveragePoseSource.Video)
            : inputs.Photos.Count > 0 ? (inputs.Photos, CoveragePoseSource.Photos)
            : (inputs.Photos, CoveragePoseSource.None);
        IReadOnlyList<RecipePass> passes = source == CoveragePoseSource.None ? [] : CaptureRecipeCheck.Check(scene, recipeCameras, Up(doc));
        var video = new VideoCoverage(inputs.Video.HasVideo, inputs.Video.FramesExtracted, inputs.Video.FramesRegistered, source, passes);
        var advice = CoverageAdviceWriter.Write(rated, markers, volumes, video);
        var facets = rated.Select(r => new FacetCoverage(
            r.Facet.Id, r.Facet.Name, r.Grid.ALo, r.Grid.BLo, r.Grid.CellMm, r.Grid.Cols, r.Grid.Rows, r.Codes,
            CoverageCellCodes.Count(r.Status), markers[r.Facet.Id])).ToList();
        return new CaptureCoverageReport(
            CaptureCoverageReport.CurrentVersion, inputs.CaptureId, inputs.ModelId, now, FacetCoverageRater.DefaultCellMm,
            inputs.Photos.Count, inputs.VideoFrames.Count, advice, facets, volumes, video);
    }

    /// <summary>The model's facets with their regions: the solver's extent (or the markers' bounds) widened by the placed holds.</summary>
    /// <param name="doc">The model.</param>
    /// <param name="holdBounds">Per facet, the placed holds' bounds.</param>
    /// <returns>The facets that have a frame and a region.</returns>
    public static IReadOnlyList<CoverageFacet> Facets(WallGeometryDocument doc, IReadOnlyDictionary<string, PlaneRectMm> holdBounds)
    {
        var result = new List<CoverageFacet>();
        foreach (var segment in doc.Segments)
        {
            foreach (var facet in segment.Facets)
            {
                if (string.IsNullOrEmpty(facet.Id) || FacetFrame.From(facet) is not { } frame || Region(doc, facet, holdBounds) is not { } region)
                {
                    continue;
                }

                var name = segment.Name ?? $"Segment {segment.Index}";
                name = segment.Facets.Count > 1 ? $"{name} ({facet.Id})" : name;
                var overhang = facet.MeasuredAngleDeg ?? segment.MeasuredAngleDeg ?? segment.DeclaredAngleDeg ?? 0;
                result.Add(new CoverageFacet(facet.Id, name, frame, region, overhang, facet.YawDeg ?? 0));
            }
        }

        return result;
    }

    private static PlaneRectMm? Region(WallGeometryDocument doc, WallGeometryFacet facet, IReadOnlyDictionary<string, PlaneRectMm> holdBounds)
    {
        var region = facet.ExtentMm is { Area: > 0 } e ? e : MarkerBounds(doc, facet.Id);
        if (holdBounds.TryGetValue(facet.Id, out var h))
        {
            var held = new PlaneRectMm(h.AMin - HoldMarginMm, h.AMax + HoldMarginMm, h.BMin - HoldMarginMm, h.BMax + HoldMarginMm);
            region = region is { } r
                ? new PlaneRectMm(Math.Min(r.AMin, held.AMin), Math.Max(r.AMax, held.AMax), Math.Min(r.BMin, held.BMin), Math.Max(r.BMax, held.BMax))
                : held;
        }

        return region is { Area: > 0 } ? region : null;
    }

    private static PlaneRectMm? MarkerBounds(WallGeometryDocument doc, string facetId)
    {
        var bounds = PlaneRectMm.Bounds(doc.Markers
            .Where(m => m.Facet == facetId)
            .SelectMany(m => m.CornersPlaneMm)
            .Where(c => c.Length >= 2)
            .Select(c => (c[0], c[1])));
        return bounds is { } b
            ? new PlaneRectMm(b.AMin - MarkerMarginMm, b.AMax + MarkerMarginMm, b.BMin - MarkerMarginMm, b.BMax + MarkerMarginMm)
            : null;
    }

    private static double[] Up(WallGeometryDocument doc)
    {
        if (doc.World?.Up is { Length: 3 } u && u.All(double.IsFinite))
        {
            var len = Math.Sqrt((u[0] * u[0]) + (u[1] * u[1]) + (u[2] * u[2]));
            if (len > 1e-6)
            {
                return [u[0] / len, u[1] / len, u[2] / len];
            }
        }

        return [0, 0, 1];
    }
}
