// <copyright file="MarkerPdfCanvas.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using SkiaSharp;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Millimetre drawing helpers over an <see cref="SKCanvas"/> already scaled to mm (y down), shared by
/// the PDF and the raster preview so both draw exactly the same thing.
/// </summary>
internal sealed class MarkerPdfCanvas(SKCanvas canvas)
{
    public static readonly SKColor Ink = new(0x1a, 0x1a, 0x2e);
    public static readonly SKColor Muted = new(0x55, 0x55, 0x66);
    public static readonly SKColor Accent = new(0xc0, 0x2a, 0x45);
    public static readonly SKColor Faint = new(0xee, 0xee, 0xf2);
    public static readonly SKColor Guide = new(0x99, 0x99, 0xaa);

    /// <summary>Mounting-hole marks: light grey, never dark enough to read as marker features.</summary>
    public static readonly SKColor HoleMark = new(0xb4, 0xb4, 0xbe);

    public SKCanvas Canvas => canvas;

    /// <summary>Draws text; <paramref name="baselineY"/> is the text baseline, sizes in mm.</summary>
    public void Text(string text, double x, double baselineY, double sizeMm, SKColor color, SKTextAlign align = SKTextAlign.Left, bool bold = false)
    {
        using var typeface = SKTypeface.FromFamilyName("sans-serif", bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        using var font = new SKFont(typeface, (float)sizeMm);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, (float)x, (float)baselineY, align, font, paint);
    }

    public void FillRect(double x, double y, double w, double h, SKColor color)
    {
        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = false };
        canvas.DrawRect((float)x, (float)y, (float)w, (float)h, paint);
    }

    public void StrokeRect(double x, double y, double w, double h, SKColor color, double widthMm, float[]? dash = null)
    {
        using var paint = Stroke(color, widthMm, dash);
        canvas.DrawRect((float)x, (float)y, (float)w, (float)h, paint);
    }

    public void Line(double x1, double y1, double x2, double y2, SKColor color, double widthMm)
    {
        using var paint = Stroke(color, widthMm, null);
        canvas.DrawLine((float)x1, (float)y1, (float)x2, (float)y2, paint);
    }

    public void Circle(double cx, double cy, double radiusMm, SKColor color, double widthMm, float[]? dash = null)
    {
        using var paint = Stroke(color, widthMm, dash);
        canvas.DrawCircle((float)cx, (float)cy, (float)radiusMm, paint);
    }

    public void Polygon(IReadOnlyList<SKPoint> points, SKColor fill, SKColor stroke, double widthMm)
    {
        using var path = new SKPath();
        path.AddPoly(points.ToArray(), close: true);
        using var fillPaint = new SKPaint { Color = fill, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawPath(path, fillPaint);
        using var strokePaint = Stroke(stroke, widthMm, null);
        canvas.DrawPath(path, strokePaint);
    }

    /// <summary>
    /// Draws ArUco marker <paramref name="id"/> as ONE vector path (the black modules, white payload
    /// cells cut out), so no viewer or printer shows hairline seams between cells.
    /// </summary>
    public void ArucoMarker(int id, double left, double top, double sizeMm)
    {
        var module = (float)(sizeMm / ArucoDict4X4.ModulesPerSide);
        using var square = new SKPath();
        square.AddRect(SKRect.Create((float)left, (float)top, (float)sizeMm, (float)sizeMm));
        using var white = new SKPath();
        for (var row = 1; row < ArucoDict4X4.ModulesPerSide - 1; row++)
        {
            for (var col = 1; col < ArucoDict4X4.ModulesPerSide - 1; col++)
            {
                if (ArucoDict4X4.IsWhite(id, row, col))
                {
                    white.AddRect(SKRect.Create((float)left + (col * module), (float)top + (row * module), module, module));
                }
            }
        }

        // One boolean op on the whole payload: the result keeps its own fill rule, so holes stay holes.
        using var black = square.Op(white, SKPathOp.Difference) ?? new SKPath(square);
        using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawPath(black, paint);
    }

    /// <summary>
    /// The 100 mm calibration bar with 10 mm ticks, left end at (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    public void CalibrationBar(double x, double y)
    {
        FillRect(x, y, 100, 2.5, SKColors.Black);
        for (var mm = 0; mm <= 100; mm += 10)
        {
            Line(x + mm, y - (mm % 50 == 0 ? 2.5 : 1.5), x + mm, y, SKColors.Black, 0.25);
        }

        Text("0", x, y - 3.2, 2.4, Muted, SKTextAlign.Center);
        Text("100 mm", x + 100, y - 3.2, 2.4, Muted, SKTextAlign.Center);
        Text("This bar must measure exactly 100 mm. If it doesn't, reprint at 100 % (\"actual size\", not \"fit to page\").", x, y + 6.5, 2.4, Ink);
    }

    private static SKPaint Stroke(SKColor color, double widthMm, float[]? dash)
    {
        var paint = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = (float)widthMm, IsAntialias = true };
        if (dash is not null)
        {
            paint.PathEffect = SKPathEffect.CreateDash(dash, 0);
        }

        return paint;
    }
}
