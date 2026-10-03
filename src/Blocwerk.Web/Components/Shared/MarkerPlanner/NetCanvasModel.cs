// <copyright file="NetCanvasModel.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>
/// What the net canvas draws, precomputed from a plan and its <see cref="NetGeometry"/>: SVG strings in
/// the SVG frame (mm, y DOWN — net y negated) so the markup stays a plain loop.
/// </summary>
/// <param name="ViewBox">The SVG viewBox (net bounds plus a margin).</param>
/// <param name="LabelSizeMm">Font size of the surface labels.</param>
/// <param name="Segments">Surfaces to draw.</param>
/// <param name="Markers">Markers to draw.</param>
public sealed record NetCanvasModel(
    string ViewBox,
    double LabelSizeMm,
    IReadOnlyList<CanvasSegment> Segments,
    IReadOnlyList<CanvasMarker> Markers)
{
    /// <summary>Builds the drawing for <paramref name="plan"/>.</summary>
    public static NetCanvasModel Build(MarkerPlan plan, NetGeometry net, MarkerGenerationOptions options)
    {
        var width = Math.Max(net.MaxX - net.MinX, 1);
        var height = Math.Max(net.MaxY - net.MinY, 1);
        var extent = Math.Max(width, height);
        var margin = extent * 0.04;
        var viewBox = string.Join(' ', F(net.MinX - margin), F(-net.MaxY - margin), F(width + (2 * margin)), F(height + (2 * margin)));
        var labelSize = Math.Clamp(extent / 60, 20, 300);

        var byIndex = plan.Segments.GroupBy(s => s.Index).ToDictionary(g => g.Key, g => g.First());
        var segments = net.Segments
            .Where(s => byIndex.ContainsKey(s.Index))
            .DistinctBy(s => s.Index)
            .Select(s => ToCanvas(s, byIndex[s.Index]))
            .ToList();

        var roles = plan.Markers.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var markers = net.Markers
            .Where(m => roles.ContainsKey(m.Id))
            .DistinctBy(m => m.Id)
            .Select(m => ToCanvas(m, roles[m.Id], byIndex.GetValueOrDefault(roles[m.Id].Segment), plan.Photo, options))
            .ToList();
        return new NetCanvasModel(viewBox, labelSize, segments, markers);
    }

    /// <summary>Invariant, compact number formatting for SVG attributes.</summary>
    public static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Points(IReadOnlyList<double[]> polygon) =>
        string.Join(' ', polygon.Select(p => $"{F(p[0])},{F(-p[1])}"));

    private static CanvasSegment ToCanvas(NetSegment net, PlanSegment segment)
    {
        var centre = SegmentLabelAnchor(net, segment);
        var title = $"#{segment.Index} {segment.Name}";
        var detail = $"{F(segment.WidthMm)}×{F(segment.HeightMm)} · {SurfaceAngle.Describe(segment.OverhangDeg)} · yaw {SurfaceYaw.Describe(segment.YawDeg)}";
        return new CanvasSegment(segment.Index, Points(net.PolygonMm), centre.X, -centre.Y, title, detail);
    }

    private static PlanVector SegmentLabelAnchor(NetSegment net, PlanSegment segment)
    {
        var incentre = SegmentOutline.Incentre(segment);
        return PlannerGeometry.SegmentToNet(net, incentre.X, incentre.Y);
    }

    private static CanvasMarker ToCanvas(NetMarker net, PlanMarker marker, PlanSegment? segment, PhotoSetup photo, MarkerGenerationOptions options)
    {
        var centre = PlannerGeometry.Centre(net.CornersMm);
        var ok = net.EstimatedPx >= PlanMarkerEdits.TargetPx(marker.Role, segment, photo, options) - 1e-6;
        return new CanvasMarker(marker.Id, marker.Segment, Points(net.CornersMm), centre.X, -centre.Y, marker.SizeMm, marker.Role, net.EstimatedPx, ok);
    }
}
