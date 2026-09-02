using SkiaSharp;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Renders a cutting-plan PDF with SkiaSharp: a front page with an isometric of the
/// assembled piece and its key dimensions, then one engineering sheet per cut piece
/// (isometric, dimensioned front profile, and the four orthographic thickness bands).
/// </summary>
public static class CuttingPlanPdf
{
    internal const float PageW = 842f; // A4 landscape, points
    internal const float PageH = 595f;
    private const float Margin = 40f;
    private const int DimNone = 0;
    private const int DimTopBottom = 1;
    private const int DimLeftRight = 2;

    public static byte[] Build(string title, AssemblyModel assembly, IReadOnlyList<CutPiece> pieces)
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = title }))
        {
            var front = document.BeginPage(PageW, PageH);
            FrontPage(front, title, assembly, pieces);
            document.EndPage();

            var pageNo = 2;
            foreach (var piece in pieces)
            {
                var canvas = document.BeginPage(PageW, PageH);
                PiecePage(canvas, piece, pageNo++, pieces.Count + 1);
                document.EndPage();
            }

            document.Close();
        }

        return stream.ToArray();
    }

    internal static void FrontPage(SKCanvas canvas, string title, AssemblyModel assembly, IReadOnlyList<CutPiece> pieces)
    {
        var sheet = new PdfSheet(canvas);

        sheet.Ink(title, new Point2D(Margin, Margin), 20f, SKTextAlign.Left, bold: true);
        sheet.Muted("Assembled overview — check every dimension before cutting.", new Point2D(Margin, Margin + 22), 10f, SKTextAlign.Left);

        var isoBox = new SKRect(Margin, 90, PageW * 0.62f, PageH - Margin);
        sheet.FrameBox(isoBox, "Isometric");

        var pts = assembly.Edges.SelectMany(e => new[] { Project(e.A), Project(e.B) }).ToArray();
        var fit = Fit(pts, isoBox, 40f);
        foreach (var e in assembly.Edges)
        {
            sheet.InkLine(fit(Project(e.A)), fit(Project(e.B)));
        }

        var infoX = PageW * 0.66f;
        sheet.Ink("Key dimensions", new Point2D(infoX, 100), 12f, SKTextAlign.Left, bold: true);
        var y = 122f;
        foreach (var d in assembly.Dimensions)
        {
            sheet.Muted(d.Label, new Point2D(infoX, y), 10f, SKTextAlign.Left);
            sheet.Ink(d.Value, new Point2D(PageW - Margin, y), 10f, SKTextAlign.Right);
            y += 18f;
        }

        y += 12f;
        sheet.Ink("Pieces", new Point2D(infoX, y), 12f, SKTextAlign.Left, bold: true);
        y += 22f;
        foreach (var p in pieces)
        {
            sheet.Muted(p.Name, new Point2D(infoX, y), 10f, SKTextAlign.Left);
            sheet.Ink($"x{p.Quantity}", new Point2D(PageW - Margin, y), 10f, SKTextAlign.Right);
            y += 18f;
        }
    }

    internal static void PiecePage(SKCanvas canvas, CutPiece piece, int pageNo, int pageCount)
    {
        var sheet = new PdfSheet(canvas);

        var qty = piece.Quantity > 1 ? $"  (x{piece.Quantity})" : string.Empty;
        sheet.Ink(piece.Name + qty, new Point2D(Margin, Margin), 18f, SKTextAlign.Left, bold: true);
        sheet.Muted($"Wood thickness {PdfSheet.F1(piece.Thickness)} mm", new Point2D(Margin, Margin + 20), 10f, SKTextAlign.Left);
        sheet.Muted($"Sheet {pageNo}/{pageCount}", new Point2D(PageW - Margin, Margin), 10f, SKTextAlign.Right);

        var panel = EngineeringViews.Beveled(piece);
        var gray = new SKColor(0x99, 0x99, 0xaa);
        var faint = new SKColor(0xc2, 0xc2, 0xcc);
        var widthMm = EngineeringViews.ProfileBounds(piece.Profile).Width;

        // Left column: isometric of the actual beveled solid + the edge table.
        DrawView(sheet, panel, EngineeringViews.Iso, new SKRect(Margin, 74, 300, 300), "Isometric", fillFront: false, stretch: false, 22f, DimNone, 0, 0, gray, faint);
        DrawNotes(sheet, piece, new SKRect(Margin, 312, 300, 556));

        // Right region: front in the centre with the four orthographic views laid out
        // around it on a grid (top above, bottom below, left/right to the sides).
        var frontBox = new SKRect(412, 186, 700, 452);
        var frontFit = DrawView(sheet, panel, EngineeringViews.Front, frontBox, "Front — cut to these dimensions", fillFront: true, stretch: false, 48f, DimNone, 0, 0, gray, faint);
        DrawFrontDims(sheet, piece, frontFit);

        DrawView(sheet, panel, EngineeringViews.Top, new SKRect(412, 74, 700, 176), "Top (not to scale)", false, true, 28f, DimTopBottom, piece.Thickness, widthMm, gray, faint);
        DrawView(sheet, panel, EngineeringViews.Bottom, new SKRect(412, 462, 700, 556), "Bottom (not to scale)", false, true, 28f, DimTopBottom, piece.Thickness, widthMm, gray, faint);
        DrawView(sheet, panel, EngineeringViews.Left, new SKRect(316, 186, 404, 452), "Left (nts)", false, true, 24f, DimLeftRight, piece.Thickness, 0, gray, faint);
        DrawView(sheet, panel, EngineeringViews.Right, new SKRect(708, 186, 796, 452), "Right (nts)", false, true, 24f, DimLeftRight, piece.Thickness, 0, gray, faint);
    }

    private static Func<Point2D, Point2D> DrawView(
        PdfSheet sheet, BeveledPanel panel, Func<Point3D, Point2D> proj, SKRect box, string caption, bool fillFront, bool stretch, float pad, int dimKind, double thickness, double widthMm, SKColor gray, SKColor faint)
    {
        sheet.FrameBox(box, caption);
        var edges = EngineeringViews.SolidEdges(panel).ToList();
        var pts = edges.SelectMany(e => new[] { proj(e.A), proj(e.B) }).ToArray();
        var fit = FitBox(pts, box, pad, stretch);

        if (fillFront)
        {
            var face = panel.Top.Select(v => fit(proj(new Point3D(v.X, v.Y, 0)))).ToArray();
            sheet.Outline(face, fill: true);
        }

        foreach (var e in edges)
        {
            var isTop = FloatCompare.AboutZero(e.A.Z) && FloatCompare.AboutZero(e.B.Z);
            var isBottom = FloatCompare.Above(e.A.Z, 0) && FloatCompare.Above(e.B.Z, 0);
            if (isTop)
            {
                sheet.InkLine(fit(proj(e.A)), fit(proj(e.B)));
            }
            else
            {
                sheet.Line(fit(proj(e.A)), fit(proj(e.B)), 0.7f, isBottom ? gray : faint);
            }
        }

        DrawViewDims(sheet, pts.Select(fit).ToArray(), dimKind, thickness, widthMm);
        return fit;
    }

    private static void DrawViewDims(PdfSheet sheet, Point2D[] sp, int dimKind, double thickness, double widthMm)
    {
        if (dimKind == DimNone || sp.Length == 0)
        {
            return;
        }

        var x0 = sp.Min(p => p.X);
        var x1 = sp.Max(p => p.X);
        var y0 = sp.Min(p => p.Y);
        var y1 = sp.Max(p => p.Y);
        var midX = (x0 + x1) / 2.0;
        var midY = (y0 + y1) / 2.0;

        if (dimKind == DimTopBottom)
        {
            // Wood thickness is the shallow (vertical) extent; width runs across.
            sheet.DimLine(new Point2D(x0, y0), new Point2D(x0, y1), new Point2D(x0 + 5, midY), $"{PdfSheet.F1(thickness)} mm", SKTextAlign.Left);
            sheet.DimLine(new Point2D(x0, y1 + 9), new Point2D(x1, y1 + 9), new Point2D(midX, y1 + 17), $"{PdfSheet.F1(widthMm)} mm", SKTextAlign.Center);
        }
        else
        {
            // Left/right: thickness is the shallow (horizontal) extent, dimensioned
            // below the band so the label clears the box caption.
            sheet.DimLine(new Point2D(x0, y1 + 9), new Point2D(x1, y1 + 9), new Point2D(midX, y1 + 17), $"{PdfSheet.F1(thickness)} mm", SKTextAlign.Center);
        }
    }

    private static void DrawFrontDims(PdfSheet sheet, CutPiece piece, Func<Point2D, Point2D> fit)
    {
        var screen = piece.Profile.Select(v => fit(EngineeringViews.Front(new Point3D(v.X, v.Y, 0)))).ToArray();
        var edges = EngineeringViews.Edges(piece);
        var cx = screen.Average(p => p.X);
        var cy = screen.Average(p => p.Y);
        for (var i = 0; i < edges.Length; i++)
        {
            var a = screen[i];
            var b = screen[(i + 1) % screen.Length];
            var mid = new Point2D((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
            var ox = mid.X - cx;
            var oy = mid.Y - cy;
            var ol = Math.Sqrt((ox * ox) + (oy * oy));
            var off = ol > 1e-6 ? 20.0 * Math.Sign(((b.X - a.X) * oy) - ((b.Y - a.Y) * ox)) : 20.0;
            sheet.Dimension(a, b, off, $"{PdfSheet.F1(edges[i].Length)} mm");
            sheet.AngleLabel(mid, new Point2D(cx, cy), $"{PdfSheet.F1(edges[i].BevelAngle)}°");
        }
    }

    private static void DrawNotes(PdfSheet sheet, CutPiece piece, SKRect box)
    {
        sheet.FrameBox(box, "Edges");
        var edges = EngineeringViews.Edges(piece);
        var y = box.Top + 22f;
        sheet.Muted($"Thickness {PdfSheet.F1(piece.Thickness)} mm", new Point2D(box.Left + 6, y), 10f, SKTextAlign.Left);
        y += 22f;
        for (var i = 0; i < edges.Length; i++)
        {
            sheet.Muted($"Edge {i + 1}", new Point2D(box.Left + 6, y), 9.5f, SKTextAlign.Left);
            sheet.Ink($"{PdfSheet.F1(edges[i].Length)} mm · {PdfSheet.F1(edges[i].BevelAngle)}°", new Point2D(box.Right - 6, y), 9.5f, SKTextAlign.Right);
            y += 17f;
            if (y > box.Bottom - 10)
            {
                break;
            }
        }
    }

    private static Point2D Project(Point3D p) => VolumeCalculator.ProjectIsometric(p);

    private static Func<Point2D, Point2D> Fit(Point2D[] pts, SKRect box, float pad) => FitBox(pts, box, pad, false);

    private static Func<Point2D, Point2D> FitBox(Point2D[] pts, SKRect box, float pad, bool stretch)
    {
        var minX = pts.Min(p => p.X);
        var maxX = pts.Max(p => p.X);
        var minY = pts.Min(p => p.Y);
        var maxY = pts.Max(p => p.Y);
        var w = Math.Max(1e-6, maxX - minX);
        var h = Math.Max(1e-6, maxY - minY);

        var availW = box.Width - (2 * pad);
        var availH = box.Height - (2 * pad);
        var sx = availW / w;
        var sy = availH / h;
        if (!stretch)
        {
            sx = sy = Math.Min(sx, sy);
        }

        var offX = box.Left + pad + ((availW - (w * sx)) / 2.0);
        var offY = box.Top + pad + ((availH - (h * sy)) / 2.0);

        // Flip Y so model-up maps to screen-up.
        return p => new Point2D(
            offX + ((p.X - minX) * sx),
            offY + ((maxY - p.Y) * sy));
    }
}
