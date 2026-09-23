// <copyright file="MarkerPlanPdf.Overview.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using SkiaSharp;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Page 1: title, photo setup, the placement map and the instructions.</summary>
public static partial class MarkerPlanPdf
{
    private const double MapTopMm = 44;
    private const double MapHeightMm = 118;

    /// <summary>One row of the surface table: "main wall: rectangle 5200 × 3400 mm · 12° slab · yaw straight".</summary>
    internal static string SurfaceLine(PlanSegment s)
    {
        var kind = s.Shape == SegmentShape.Rectangle ? "rectangle" : $"right triangle ({s.RightAngle})";
        var faceOn = MarkerSizing.IsGrazing(s) ? " · SHOOT FACE-ON" : string.Empty;
        return $"{s.Name}: {kind} {Mm(s.WidthMm)} × {Mm(s.HeightMm)} mm · {SurfaceAngle.Describe(s.OverhangDeg)} · yaw {SurfaceYaw.Describe(s.YawDeg)}{faceOn}";
    }

    /// <summary>The map's second label line under a surface's name: "12° slab · yaw straight".</summary>
    internal static string MapLabel(PlanSegment s) => $"{SurfaceAngle.Describe(s.OverhangDeg)} · yaw {SurfaceYaw.Describe(s.YawDeg)}";

    internal static IEnumerable<string> Instructions(MarkerPlan plan)
    {
        yield return "1. Print at 100 % (\"actual size\"). Measure the 100 mm bar at the bottom of EVERY page before cutting.";
        yield return "2. Cut along the dashed line. Keep the white border around the black square — it is part of the marker.";
        yield return "3. Stick each marker flat on its surface, centre at its (x, y) from the table (within about 2 cm is fine;";
        yield return "    the photos measure the exact spot). Its TOP edge points up the surface. Never bend a marker over an edge.";
        if (MarkerPlanPdfLayout.Holes(plan) is not null)
        {
            yield return "    Screw through the marked holes; keep screw heads inside the dashed circles.";
        }
        else
        {
            yield return "    Tape, or screw well clear of the black square: screw heads near it shift the detected corners.";
        }

        yield return $"4. Photograph from about {plan.Photo.DistanceMm / 1000:0.0#} m. Only markers FULLY in frame count; overlap photos by a third.";
        yield return "5. Every corner marker should appear in at least 3 photos taken from different spots.";
        yield return "6. Surfaces marked SHOOT FACE-ON (side walls, roofs, near-flat slabs): photograph them square-on, not from the side.";
        yield return "7. Upload this plan's JSON together with the photos — it tells Blocwerk which marker sits where.";
    }

    private static void DrawOverview(MarkerPdfCanvas c, MarkerPlan plan, NetGeometry net, string wallName, MarkerPdfPage page)
    {
        var left = MarkerPlanPdfLayout.MarginMm;
        var width = page.WidthMm - (2 * left);
        c.Text($"Marker plan · {wallName}", left, 18, 7, MarkerPdfCanvas.Ink, bold: true);
        c.Text($"{plan.Markers.Count} markers on {plan.Segments.Count} surfaces · ArUco {plan.Dictionary} · keep this plan's JSON with your photos", left, 25, 3, MarkerPdfCanvas.Muted);
        c.Text(PhotoLine(plan.Photo), left, 31, 3, MarkerPdfCanvas.Ink);
        c.Text("Map: the wall unfolded flat, seen from the front. Each surface's (x, y) starts at its bottom-left corner, y runs up the surface.", left, 37, 2.6, MarkerPdfCanvas.Muted);

        var box = SKRect.Create((float)left, (float)MapTopMm, (float)width, (float)MapHeightMm);
        c.StrokeRect(box.Left, box.Top, box.Width, box.Height, MarkerPdfCanvas.Guide, 0.2);
        DrawMap(c, plan, net, box);

        var y = MapTopMm + MapHeightMm + 7;
        foreach (var s in plan.Segments.Take(8))
        {
            c.Text(SurfaceLine(s), left, y, 2.7, MarkerPdfCanvas.Ink);
            y += 4.2;
        }

        y += 3;
        c.Text("How to use this plan", left, y, 3.6, MarkerPdfCanvas.Ink, bold: true);
        y += 5.5;
        foreach (var line in Instructions(plan))
        {
            c.Text(line, left, y, 2.7, MarkerPdfCanvas.Ink);
            y += 4.2;
        }
    }

    private static string PhotoLine(PhotoSetup photo)
    {
        var pxPerMm = MarkerSizing.PxPerMm(photo);
        var w = MarkerSizing.FootprintWidthMm(photo) / 1000;
        var h = MarkerSizing.FootprintHeightMm(photo) / 1000;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Photos from about {photo.DistanceMm / 1000:0.0#} m (at most) with {MarkerSizingAdvice.DescribeCamera(photo)} ({photo.HorizontalFovDeg:0}° wide, {photo.ImageLongEdgePx} px): one photo covers ≈ {w:0.0} × {h:0.0} m, a 100 mm marker ≈ {100 * pxPerMm:0} px face-on.");
    }

    private static void DrawMap(MarkerPdfCanvas c, MarkerPlan plan, NetGeometry net, SKRect box)
    {
        var spanX = net.MaxX - net.MinX;
        var spanY = net.MaxY - net.MinY;
        if (spanX <= 0 || spanY <= 0)
        {
            c.Text("(no surfaces to draw)", box.MidX, box.MidY, 3, MarkerPdfCanvas.Muted, SKTextAlign.Center);
            return;
        }

        const double pad = 8;
        var scale = Math.Min((box.Width - (2 * pad)) / spanX, (box.Height - (2 * pad)) / spanY);
        var offX = box.Left + pad + (((box.Width - (2 * pad)) - (spanX * scale)) / 2);
        var offY = box.Top + pad + (((box.Height - (2 * pad)) - (spanY * scale)) / 2);
        SKPoint Map(double[] p) => new((float)(offX + ((p[0] - net.MinX) * scale)), (float)(offY + ((net.MaxY - p[1]) * scale)));

        foreach (var s in net.Segments)
        {
            var points = s.PolygonMm.Select(Map).ToList();
            c.Polygon(points, MarkerPdfCanvas.Faint, MarkerPdfCanvas.Ink, 0.3);
            var segment = plan.Segments.First(p => p.Index == s.Index);
            var cx = points.Average(p => p.X);
            var cy = points.Average(p => p.Y);
            c.Text(segment.Name, cx, cy, 2.8, MarkerPdfCanvas.Ink, SKTextAlign.Center, bold: true);
            c.Text(MapLabel(segment), cx, cy + 3.4, 2.2, MarkerPdfCanvas.Muted, SKTextAlign.Center);
        }

        foreach (var m in net.Markers)
        {
            // True-scale squares (rotated with their surface), never smaller than 1.2 mm on paper.
            var corners = m.CornersMm.Select(Map).ToList();
            var centre = new SKPoint(corners.Average(p => p.X), corners.Average(p => p.Y));
            var grow = Math.Max(1, 1.2 / Math.Max(0.01, SKPoint.Distance(corners[0], corners[1])));
            var drawn = corners.Select(p => centre + new SKPoint((float)((p.X - centre.X) * grow), (float)((p.Y - centre.Y) * grow))).ToList();
            c.Polygon(drawn, SKColors.Black, SKColors.Black, 0.1);
            c.Text(m.Id.ToString(CultureInfo.InvariantCulture), drawn.Max(p => p.X) + 0.6, drawn.Min(p => p.Y) + 1.8, 2.4, MarkerPdfCanvas.Accent, bold: true);
        }
    }
}
