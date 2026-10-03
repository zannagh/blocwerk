// <copyright file="MarkerPlanPdf.Markers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using SkiaSharp;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>The placement table and the true-size marker pages.</summary>
public static partial class MarkerPlanPdf
{
    private static readonly float[] CutDash = [2f, 1.2f];

    private static void DrawTable(MarkerPdfCanvas c, MarkerPlan plan, string wallName, MarkerPdfPage page)
    {
        var left = MarkerPlanPdfLayout.MarginMm;
        Header(c, $"{wallName} · placement table", "x, y = marker centre in mm from the surface's bottom-left corner (y up the surface)");
        double[] columns = [left, left + 14, left + 72, left + 92, left + 114, left + 136, left + 158];
        string[] heads = ["id", "surface", "role", "x mm", "y mm", "size mm", "≈ px"];
        var y = MarkerPlanPdfLayout.MarginMm + MarkerPlanPdfLayout.HeaderMm + 6;
        for (var i = 0; i < heads.Length; i++)
        {
            c.Text(heads[i], columns[i], y, 2.8, MarkerPdfCanvas.Muted, bold: true);
        }

        var segments = plan.Segments.GroupBy(s => s.Index).ToDictionary(g => g.Key, g => g.First());
        foreach (var m in plan.Markers.OrderBy(m => m.Id).Skip(page.TableStart).Take(page.TableCount))
        {
            y += 4.6;
            var px = segments.TryGetValue(m.Segment, out var s) ? MarkerSizing.EstimatedPx(m.SizeMm, s, plan.Photo).ToString("0", CultureInfo.InvariantCulture) : "–";
            string[] cells = [m.Id.ToString(CultureInfo.InvariantCulture), SegmentName(plan, m.Segment), m.Role == MarkerRole.Corner ? "corner" : "filler", Mm(m.XMm), Mm(m.YMm), Mm(m.SizeMm), px];
            for (var i = 0; i < cells.Length; i++)
            {
                c.Text(cells[i], columns[i], y, 2.8, i == 0 ? MarkerPdfCanvas.Accent : MarkerPdfCanvas.Ink, bold: i == 0);
            }
        }
    }

    private static void DrawMarkerPage(MarkerPdfCanvas c, MarkerPlan plan, string wallName, MarkerPdfPage page)
    {
        Header(c, $"{wallName} · markers at true size", "Cut on the dashed line; keep the white border. The TOP edge goes up the surface.");
        var holes = MarkerPlanPdfLayout.Holes(plan);
        if (holes is not null)
        {
            DrawHoleLegend(c, page, holes);
        }

        foreach (var tile in page.Tiles)
        {
            var m = tile.Marker;
            var box = tile.BoxMm;
            c.StrokeRect(tile.LeftMm, tile.TopMm, box, box, MarkerPdfCanvas.Guide, 0.2, CutDash);
            c.ArucoMarker(m.Id, tile.SquareLeftMm, tile.SquareTopMm, m.SizeMm);
            if (holes is not null)
            {
                DrawHoles(c, tile, holes);
            }

            c.Text("TOP", tile.LeftMm + (box / 2), tile.TopMm - 1.2, 2.6, MarkerPdfCanvas.Muted, SKTextAlign.Center);
            var label = string.Create(
                CultureInfo.InvariantCulture,
                $"id {m.Id} · {SegmentName(plan, m.Segment)} · ({m.XMm:0}, {m.YMm:0}) mm · {m.SizeMm:0} mm");
            c.Text(label, tile.LeftMm, tile.TopMm + box + 4.2, 3, MarkerPdfCanvas.Ink, bold: true);
        }
    }

    private static void Header(MarkerPdfCanvas c, string title, string subtitle)
    {
        var left = MarkerPlanPdfLayout.MarginMm;
        c.Text(title, left, left + 5, 4.2, MarkerPdfCanvas.Ink, bold: true);
        c.Text(subtitle, left, left + 10, 2.6, MarkerPdfCanvas.Muted);
    }
}
