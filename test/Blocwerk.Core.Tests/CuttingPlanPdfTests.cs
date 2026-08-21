using System.Text;
using Blocwerk.Core.Helpers;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Smoke tests for the cutting-plan PDF: it renders without throwing for every
/// shape, produces a valid PDF, and has one sheet per piece plus a front page.
/// Set the BLOCWERK_PDF_OUT env var to a directory to also dump the PDFs for review.
/// </summary>
public class CuttingPlanPdfTests
{
    [Fact]
    public void Wedge_ProducesValidPdf_WithSheetPerPiecePlusFrontPage()
    {
        var wedge = WedgeCalculator.Calculate(45, 90, 300, 400, 18, lowerPortionAngleDeg: 30);
        var pieces = CuttingPlan.FromWedge(wedge, 18);
        var assembly = AssemblyModels.FromWedge("Angle Change Wedge", wedge, 300);

        var bytes = CuttingPlanPdf.Build("Angle Change Wedge", assembly, pieces);

        AssertPdf(bytes, expectedPages: pieces.Count + 1);
        Dump("wedge", bytes);
    }

    [Fact]
    public void Roof_ProducesValidPdf()
    {
        var roof = VolumeCalculator.CalculateRoof(1000, 600, 400, 300, 18);
        var pieces = CuttingPlan.FromVolume(roof, 18);
        var assembly = AssemblyModels.FromVolume("Hip Roof Volume", roof);

        var bytes = CuttingPlanPdf.Build("Hip Roof Volume", assembly, pieces);

        AssertPdf(bytes, expectedPages: pieces.Count + 1);
        Dump("roof", bytes);
    }

    [Fact]
    public void Pyramid_ProducesValidPdf()
    {
        var pyramid = VolumeCalculator.CalculatePyramid(5, 200, 150, 18);
        var pieces = CuttingPlan.FromVolume(pyramid, 18);
        var assembly = AssemblyModels.FromVolume("Pyramid Volume", pyramid);

        var bytes = CuttingPlanPdf.Build("Pyramid Volume", assembly, pieces);

        AssertPdf(bytes, expectedPages: pieces.Count + 1);
        Dump("pyramid", bytes);
    }

    [Fact]
    public void DumpPiecePagePngs_ForReview()
    {
        var dir = Environment.GetEnvironmentVariable("BLOCWERK_PDF_OUT");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var wedge = WedgeCalculator.Calculate(45, 90, 300, 400, 18, lowerPortionAngleDeg: 30);
        var wedgePieces = CuttingPlan.FromWedge(wedge, 18);
        RenderPiece(dir, "wedge-face", wedgePieces[0]);
        RenderPiece(dir, "wedge-end", wedgePieces[2]);

        var roof = VolumeCalculator.CalculateRoof(1000, 600, 400, 300, 18);
        var roofPieces = CuttingPlan.FromVolume(roof, 18);
        RenderPiece(dir, "roof-long", roofPieces.First(p => p.Name == "Long face"));
        RenderPiece(dir, "roof-hip", roofPieces.First(p => p.Name == "Hip end"));

        RenderWedgeSideView(dir, "wedge-side-45-90-30", 45, WedgeCalculator.Calculate(45, 90, 300, 400, 18, 30));
        RenderWedgeSideView(dir, "wedge-side-45-30-50", 45, WedgeCalculator.Calculate(45, 30, 300, 400, 18, 50));
    }

    private static void AssertPdf(byte[] bytes, int expectedPages)
    {
        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));

        var text = Encoding.Latin1.GetString(bytes);
        var pageCount = text.Split("/Type /Page\n").Length - 1 + (text.Split("/Type /Page ").Length - 1);
        Assert.True(pageCount >= expectedPages, $"expected >= {expectedPages} pages, found markers for {pageCount}");
    }

    // Mirrors the rotation/hatch logic of AngleWedge.razor's RenderCrossSection so the
    // side-view orientation can be eyeballed: wall at its real incline, hatched behind.
    private static void RenderWedgeSideView(string dir, string name, double wallAngle, WedgeResult result)
    {
        const int w = 640;
        const int h = 640;
        using var bitmap = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        var sheet = new PdfSheet(canvas);

        var rad = (wallAngle - 90.0) * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        Point2D Rot(Point2D p) => new((p.X * cos) - (p.Y * sin), (p.X * sin) + (p.Y * cos));

        var cross = result.CrossSection;
        var rc = cross.Select(Rot).ToArray();
        var xsMin = Math.Min(0, cross.Min(p => p.X));
        var xsMax = Math.Max(0, cross.Max(p => p.X));
        var span = Math.Max(xsMax - xsMin, result.DepthMm);
        var ext = span * 0.28;
        var band = span * 0.32;

        var wallLo = Rot(new Point2D(xsMin - ext, 0));
        var wallHi = Rot(new Point2D(xsMax + ext, 0));
        var bandC = Rot(new Point2D(xsMax + ext, -band));
        var bandD = Rot(new Point2D(xsMin - ext, -band));
        var foot = Rot(new Point2D(cross[1].X, 0));

        var all = rc.Concat(new[] { wallLo, wallHi, bandC, bandD, foot }).ToArray();
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X);
        double minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        var scale = Math.Min((w - 80) / (maxX - minX), (h - 80) / (maxY - minY));
        Point2D S(Point2D p) => new(40 + ((p.X - minX) * scale), h - 40 - ((p.Y - minY) * scale));

        // Hatched wall band.
        for (var t = -1.0; t <= 2.0; t += 0.06)
        {
            var p1 = new Point2D(wallLo.X + ((wallHi.X - wallLo.X) * t), wallLo.Y + ((wallHi.Y - wallLo.Y) * t));
            var p2 = new Point2D(p1.X + ((bandD.X - wallLo.X) * 1.0), p1.Y + ((bandD.Y - wallLo.Y) * 1.0));
            if (t is >= 0 and <= 1)
            {
                sheet.Line(S(p1), S(p2), 0.5f, new SKColor(0xaa, 0xaa, 0xbb));
            }
        }

        sheet.Line(S(wallLo), S(wallHi), 2.4f, new SKColor(0x1a, 0x1a, 0x2e));
        sheet.Outline(rc.Select(S).ToArray(), fill: true);
        sheet.Line(S(rc[1]), S(foot), 1f, new SKColor(0xe9, 0x45, 0x60));
        sheet.Text($"Tip {PdfSheet.F1(result.DepthMm)} mm", S(new Point2D((rc[1].X + foot.X) / 2, (rc[1].Y + foot.Y) / 2)), 12f, new SKColor(0xe9, 0x45, 0x60), SKTextAlign.Center, bold: true);

        var labels = result.CrossSectionLabels;
        for (var i = 0; i < rc.Length; i++)
        {
            var a = rc[i];
            var b = rc[(i + 1) % rc.Length];
            var mid = new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            sheet.Text($"{labels[i]} {PdfSheet.F1(result.CrossSectionEdgeLengths[i])}", S(mid), 11f, new SKColor(0x1a, 0x1a, 0x2e), SKTextAlign.Center);
        }

        sheet.Text($"wall {PdfSheet.F1(wallAngle)}°", S(wallHi), 11f, new SKColor(0x55, 0x55, 0x66), SKTextAlign.Center);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.OpenWrite(Path.Combine(dir, $"{name}.png"));
        data.SaveTo(fs);
    }

    private static void RenderPiece(string dir, string name, CutPiece piece)
    {
        using var bitmap = new SKBitmap((int)CuttingPlanPdf.PageW, (int)CuttingPlanPdf.PageH);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        CuttingPlanPdf.PiecePage(canvas, piece, 2, 5);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.OpenWrite(Path.Combine(dir, $"piece-{name}.png"));
        data.SaveTo(fs);
    }

    private static void Dump(string name, byte[] bytes)
    {
        var dir = Environment.GetEnvironmentVariable("BLOCWERK_PDF_OUT");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"cutting-plan-{name}.pdf"), bytes);
        }
    }
}
