// <copyright file="MarkerPlanPdf.Holes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using SkiaSharp;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Mounting holes on the marker pages, and where every printed square landed.</summary>
public static partial class MarkerPlanPdf
{
    private static readonly float[] HeadDash = [1f, 0.8f];

    /// <summary>
    /// Every marker's black square in the PDF (page and page-mm position), in print order — for checking
    /// a rendered page against the exact spot each marker was drawn.
    /// </summary>
    public static IReadOnlyList<PrintedMarker> PrintedMarkers(MarkerPlan plan)
    {
        var pages = MarkerPlanPdfLayout.Build(plan);
        return pages
            .SelectMany((page, index) => page.Tiles.Select(t => new PrintedMarker(t.Marker.Id, index, t.SquareLeftMm, t.SquareTopMm, t.Marker.SizeMm, t.BorderMm)))
            .ToList();
    }

    /// <summary>A hole at its true diameter with a crosshair to drill or punch, and the head's dashed circle.</summary>
    private static void DrawHoles(MarkerPdfCanvas c, MarkerTile tile, MountingHoles holes)
    {
        var holeR = holes.HoleDiameterMm / 2;
        var headR = holes.ScrewHeadDiameterMm / 2;
        var arm = holeR + Math.Min(1.2, (headR - holeR) * 0.8);
        foreach (var (x, y) in MountingHoleLayout.HoleCentres(tile.SquareLeftMm, tile.SquareTopMm, tile.Marker.SizeMm, holes))
        {
            c.Circle(x, y, holeR, MarkerPdfCanvas.HoleMark, 0.2);
            c.Line(x - arm, y, x + arm, y, MarkerPdfCanvas.HoleMark, 0.15);
            c.Line(x, y - arm, x, y + arm, MarkerPdfCanvas.HoleMark, 0.15);
            c.Circle(x, y, headR, MarkerPdfCanvas.HoleMark, 0.2, HeadDash);
        }
    }

    /// <summary>Legend at the header's right: one sample hole with its dashed head circle, true size.</summary>
    private static void DrawHoleLegend(MarkerPdfCanvas c, MarkerPdfPage page, MountingHoles holes)
    {
        var headR = holes.ScrewHeadDiameterMm / 2;
        var cx = page.WidthMm - MarkerPlanPdfLayout.MarginMm - headR;
        var cy = MarkerPlanPdfLayout.MarginMm + Math.Max(4, headR);
        var holeR = holes.HoleDiameterMm / 2;
        c.Circle(cx, cy, holeR, MarkerPdfCanvas.Muted, 0.2);
        c.Line(cx - holeR - 0.8, cy, cx + holeR + 0.8, cy, MarkerPdfCanvas.Muted, 0.15);
        c.Line(cx, cy - holeR - 0.8, cx, cy + holeR + 0.8, MarkerPdfCanvas.Muted, 0.15);
        c.Circle(cx, cy, headR, MarkerPdfCanvas.Muted, 0.2, HeadDash);
        var text = string.Create(CultureInfo.InvariantCulture, $"hole Ø{holes.HoleDiameterMm:0.#} mm · dashed = screw head Ø{holes.ScrewHeadDiameterMm:0.#} mm");
        c.Text(text, cx - headR - 2, MarkerPlanPdfLayout.MarginMm + 5, 2.4, MarkerPdfCanvas.Muted, SKTextAlign.Right);
        c.Text("keep screw heads inside the dashed circles", cx - headR - 2, MarkerPlanPdfLayout.MarginMm + 8.4, 2.4, MarkerPdfCanvas.Muted, SKTextAlign.Right);
    }
}
