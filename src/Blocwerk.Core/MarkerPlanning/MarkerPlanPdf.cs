// <copyright file="MarkerPlanPdf.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using SkiaSharp;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// The printable marker plan, drawn with SkiaSharp's PDF backend (MIT, already a Core dependency; no
/// native code beyond Skia itself): an overview page (setup, placement map, instructions), the
/// placement table, then every marker at TRUE size as vector cells with its white border, a dashed
/// cut guide and a label. Every page carries a 100 mm calibration bar. The same drawing code renders
/// the raster preview (<see cref="RasterizePage"/>), which the tests feed to the real detector.
/// </summary>
public static partial class MarkerPlanPdf
{
    private const float PointsPerMm = 72f / 25.4f;

    /// <summary>
    /// Renders the PDF. With <paramref name="printOnly"/> (e.g. the markers added or changed since the last
    /// capture) the overview and table still show the whole plan, but only those markers get true-size pages.
    /// </summary>
    public static byte[] Render(MarkerPlan plan, string wallName, IReadOnlySet<int>? printOnly = null)
    {
        var pages = MarkerPlanPdfLayout.Build(plan, printOnly);
        var net = NetLayout.Compute(plan).Net;
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
        {
            Title = $"Marker plan – {wallName}",
            Creator = "Blocwerk",
            RasterDpi = 300,
        }))
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                var canvas = document.BeginPage((float)page.WidthMm * PointsPerMm, (float)page.HeightMm * PointsPerMm);
                canvas.Scale(PointsPerMm);
                DrawPage(new MarkerPdfCanvas(canvas), plan, net, wallName, page, i + 1, pages.Count);
                if (printOnly is not null && page.Kind == MarkerPdfPageKind.Overview)
                {
                    DrawPrintOnlyNote(new MarkerPdfCanvas(canvas), plan, printOnly);
                }

                document.EndPage();
            }

            document.Close();
        }

        return stream.ToArray();
    }

    /// <summary>Number of pages <see cref="Render"/> produces for <paramref name="plan"/>.</summary>
    public static int PageCount(MarkerPlan plan) => MarkerPlanPdfLayout.Build(plan).Count;

    /// <summary>Index (0-based) of the first true-size marker page, after the overview and the table.</summary>
    public static int FirstMarkerPage(MarkerPlan plan) =>
        MarkerPlanPdfLayout.Build(plan).FindIndex(p => p.Kind == MarkerPdfPageKind.Markers) is var i and >= 0 ? i : PageCount(plan);

    /// <summary>
    /// Renders page <paramref name="pageIndex"/> (0-based) as a PNG at <paramref name="dpi"/> — the same
    /// drawing as the PDF, for previews and for checking printed markers with the detector.
    /// </summary>
    public static byte[] RasterizePage(MarkerPlan plan, string wallName, int pageIndex, float dpi)
    {
        var pages = MarkerPlanPdfLayout.Build(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, pages.Count);
        var page = pages[pageIndex];
        var pxPerMm = dpi / 25.4f;
        var info = new SKImageInfo((int)Math.Ceiling(page.WidthMm * pxPerMm), (int)Math.Ceiling(page.HeightMm * pxPerMm));
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.Scale(pxPerMm);
        DrawPage(new MarkerPdfCanvas(canvas), plan, NetLayout.Compute(plan).Net, wallName, page, pageIndex + 1, pages.Count);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawPage(MarkerPdfCanvas c, MarkerPlan plan, NetGeometry net, string wallName, MarkerPdfPage page, int number, int count)
    {
        switch (page.Kind)
        {
            case MarkerPdfPageKind.Overview:
                DrawOverview(c, plan, net, wallName, page);
                break;
            case MarkerPdfPageKind.Table:
                DrawTable(c, plan, wallName, page);
                break;
            default:
                DrawMarkerPage(c, plan, wallName, page);
                break;
        }

        var footY = page.HeightMm - MarkerPlanPdfLayout.MarginMm - 12;
        c.CalibrationBar(MarkerPlanPdfLayout.MarginMm, footY);
        c.Text($"Page {number} / {count}", page.WidthMm - MarkerPlanPdfLayout.MarginMm, footY + 11, 2.4, MarkerPdfCanvas.Muted, SKTextAlign.Right);
    }

    private static void DrawPrintOnlyNote(MarkerPdfCanvas c, MarkerPlan plan, IReadOnlySet<int> printOnly)
    {
        var ids = plan.Markers.Select(m => m.Id).Where(printOnly.Contains).Order().ToList();
        var text = ids.Count == 0
            ? "Only changed markers were asked for, and none changed: no marker pages."
            : $"Only the {ids.Count} new or changed marker(s) are printed: {string.Join(", ", ids)}. Leave the others where they are.";
        c.Text(text, MarkerPlanPdfLayout.MarginMm, 41.5, 2.6, MarkerPdfCanvas.Accent, bold: true);
    }

    private static string SegmentName(MarkerPlan plan, int index) =>
        plan.Segments.FirstOrDefault(s => s.Index == index)?.Name ?? $"segment {index}";

    private static string Mm(double value) => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
}
